using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Solasta.DirectTransport;

class Program
{
    const string Key = "test-session-key-for-local-checks";
    static int assertions;
    static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    static void Until(Func<bool> condition, string message)
    { if (!SpinWait.SpinUntil(condition, 10000)) throw new TimeoutException(message); }
    static List<Packet> Drain(IGameplayPeer client, int count)
    {
        var packets = new List<Packet>();
        Until(() => { while (client.TryReceive(out var p)) packets.Add(p); return packets.Count >= count; }, "Receive timeout " + client.Failure);
        return packets;
    }
    static void Main()
    {
        // Exercise the actual installed Photon codec retained by the adapter.
        var rpc = new object[] { "ExampleRPC", new object[] { (long)123, new byte[] { 1, 2, 3 }, new string[] { "a", "b" },
            new Dictionary<int, int> { [2] = 7 }, true, 1.5f, null } };
#pragma warning disable CS0612, CS0618
        var roundtrip = (object[])ExitGames.Client.Photon.Protocol.Deserialize(ExitGames.Client.Photon.Protocol.Serialize(rpc));
#pragma warning restore CS0612, CS0618
        var args = (object[])roundtrip[1];
        Check((string)roundtrip[0] == "ExampleRPC" && (long)args[0] == 123 && ((byte[])args[1]).Length == 3
            && ((string[])args[2])[1] == "b" && ((Dictionary<int, int>)args[3])[2] == 7 && (bool)args[4]
            && (float)args[5] == 1.5f && args[6] == null, "Installed Photon codec roundtrip");
        var original = new Packet { Kind = Wire.Data, Sequence = 42, Sender = 3, Target = 7, Payload = Encoding.UTF8.GetBytes("test") };
        var copy = Wire.Read(new FragmentedStream(Wire.Encode(original)));
        Check(copy.Sequence == 42 && copy.Sender == 3 && copy.Target == 7 && copy.Payload.SequenceEqual(original.Payload), "Fragmented frame");
        var invalid = Wire.Encode(original); invalid[0] = 0;
        Reject(() => Wire.Read(new MemoryStream(invalid)), "Bad magic accepted");
        Reject(() => Wire.Read(new MemoryStream(Wire.Encode(original).Take(26).ToArray())), "Truncation accepted");
        Reject(() => Wire.Encode(new Packet { Payload = new byte[Wire.MaxPayload + 1] }), "Oversize accepted");
        invalid = Wire.Encode(original); Array.Copy(BitConverter.GetBytes(-1), 0, invalid, 21, 4);
        Reject(() => Wire.Read(new MemoryStream(invalid)), "Negative payload length accepted");
        CheckGroup(6, true);
        CheckGroup(8, false); // A six-player test must not accidentally introduce a six-player cap.
        using (var relay = new HostServer(IPAddress.Loopback, 0, 1, new[] { 1, 2 }, Key))
        {
            using (var bad = new Client("127.0.0.1", relay.Port, 2, "wrong-key"))
            { Until(() => bad.Failure != null, "Bad key not rejected"); Check(relay.ConnectedCount == 1, "Bad key admitted"); }
            Reject(() => relay.Activate(), "Incomplete room activated");
            using (var spoofHost = new Client("127.0.0.1", relay.Port, 1, Key))
            { Until(() => spoofHost.Failure != null, "Host identity not reserved"); Check(relay.ConnectedCount == 1, "Host spoof admitted"); }
            using (var good = new Client("127.0.0.1", relay.Port, 2, Key))
            {
                Until(() => relay.ConnectedCount == 2, "Good admission");
                using (var duplicate = new Client("127.0.0.1", relay.Port, 2, Key))
                { Until(() => duplicate.Failure != null, "Duplicate actor not rejected"); Check(good.Failure == null, "Duplicate killed legitimate peer"); }
            }
        }
        // No remote player is required by the host transport. This is a lobby/test capability;
        // the game adapter still requires an ordinary multiplayer room.
        using (var solo = new HostServer(IPAddress.Loopback, 0, 7, new[] { 7 }, Key))
        {
            solo.Activate(); solo.Host.Send(0, new byte[] { 42 });
            Check(Drain(solo.Host, 1)[0].Sender == 7, "Host-only operation");
        }
        Console.WriteLine("PASS: " + assertions + " checks; direct host + 5 and 7 remote peers, common order, idle heartbeat, admission, framing, peer-loss stop.");
    }
    static void CheckGroup(int count, bool testIdle)
    {
        using (var relay = new HostServer(IPAddress.Loopback, 0, 1, Enumerable.Range(1, count), Key))
        {
            var clients = new IGameplayPeer[] { relay.Host }.Concat(Enumerable.Range(2, count - 1)
                .Select(id => (IGameplayPeer)new Client("127.0.0.1", relay.Port, id, Key))).ToArray();
            try
            {
                Until(() => relay.ConnectedCount == count, "Admission timeout");
                relay.Activate(); Until(() => clients.All(c => c.Active), "Activation timeout");
                // Simultaneous senders; every receiver, including sender, must observe identical order.
                Task.WaitAll(clients.Select(c => Task.Run(() => {
                    for (int n = 0; n < 100; n++) c.Send(n % 4 == 0 ? 0 : n % 4 == 1 ? -1 : n % 4 == 2 ? 2 : 1, BitConverter.GetBytes(n));
                })).ToArray());
                var received = clients.Select(c => Drain(c, count * 100)).ToArray();
                for (int i = 0; i < received.Length; i++)
                {
                    Check(received[i].Count == count * 100, "Command count");
                    Check(received[i].Select(p => p.Sequence).SequenceEqual(Enumerable.Range(1, count * 100).Select(n => (long)n)), "Sequence gap");
                    Check(received[i].Select(p => p.Sender + ":" + Convert.ToBase64String(p.Payload)).SequenceEqual(
                        received[0].Select(p => p.Sender + ":" + Convert.ToBase64String(p.Payload))), "Divergent total order");
                    Check(received[i].Where(p => Client.IsRecipient(p, i + 1)).All(p => p.Target == 0 || p.Target == i + 1 || p.Target == -1 && p.Sender != i + 1), "Routing");
                    foreach (int actor in Enumerable.Range(1, count))
                        Check(received[i].Where(p => p.Sender == actor).Select(p => BitConverter.ToInt32(p.Payload, 0))
                            .SequenceEqual(Enumerable.Range(0, 100)), "Per-sender ordering");
                }
                // All peers see targeted envelope sequences, but only actor 2 executes them.
                var targeted = received[0].First(p => p.Target == 2);
                Check(clients.Count(c => Client.IsRecipient(targeted, c.Actor)) == 1, "Target leakage");
                var toHost = received[0].First(p => p.Target == 1 && p.Sender != 1);
                Check(clients.Count(c => Client.IsRecipient(toHost, c.Actor)) == 1 && Client.IsRecipient(toHost, relay.Host.Actor), "Remote-to-host targeting");
                // Check keepalive survives the 15-second socket read deadline in an idle lobby.
                if (testIdle) { Thread.Sleep(16000); Check(clients.All(c => c.Failure == null && c.Active), "Idle heartbeat failure"); }
                using (var late = new Client("127.0.0.1", relay.Port, count + 1, Key))
                    Until(() => late.Failure != null, "Live membership not frozen");
                Check(relay.Host.Failure == null, "Rejected late join stopped host");
                clients[count - 1].Dispose();
                Until(() => clients.Take(count - 1).All(c => c.Failure != null), "Peer loss did not stop session");
                Check(relay.Failure != null, "Relay did not record peer failure");
            }
            finally { foreach (var c in clients) c.Dispose(); }
        }
    }
    static void Reject(Action action, string message)
    { bool threw = false; try { action(); } catch { threw = true; } Check(threw, message); }
    sealed class FragmentedStream : MemoryStream
    {
        public FragmentedStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }
}
