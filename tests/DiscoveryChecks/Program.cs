using System;
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
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, message); }
    static RoomSnapshot Room(int local, int master = 3, int count = 6) => new RoomSnapshot {
        LocalActor = local, MasterActor = master, Room = "test-room", GameHash = new string('a', 64), ModVersion = "0.4.0", Actors = Enumerable.Range(1, count).ToArray()
    };
    static void Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Check(condition(), message);
    }
    static void Main()
    {
        var room = Room(3);
        var ad = HostAdvertisement.Create(room, 27090, new[] { "127.0.0.1" });
        var decoded = HostAdvertisement.Read(ad.Encode(), Room(1));
        Check(decoded.HostActor == 3 && decoded.SessionKey == ad.SessionKey, "Master identity/handshake changed");
        Check(HostAdvertisement.Create(Room(3, 3, 50), 27090, new[] { "127.0.0.1" }).RosterHash != ad.RosterHash, "Roster fingerprint ignored size");
        var reversed = Room(2); Array.Reverse(reversed.Actors);
        Check(HostAdvertisement.Read(ad.Encode(), reversed).RosterHash == ad.RosterHash, "Roster order affects identity");
        Reject(() => HostAdvertisement.Read(ad.Encode(), Room(1, 2)), "Foreign master accepted");
        Reject(() => HostAdvertisement.Read(ad.Encode(), Room(1, 3, 7)), "Stale roster accepted");
        var foreign = Room(1); foreign.Room = "other-room";
        Reject(() => HostAdvertisement.Read(ad.Encode(), foreign), "Foreign room accepted");
        foreign = Room(1); foreign.GameHash = new string('b', 64);
        Reject(() => HostAdvertisement.Read(ad.Encode(), foreign), "Foreign game accepted");
        foreign = Room(1); foreign.ModVersion = "0.3.0";
        Reject(() => HostAdvertisement.Read(ad.Encode(), foreign), "Old mod accepted");
        ad.Token = "invalid"; Reject(() => HostAdvertisement.Read(ad.Encode(), room), "Invalid token accepted");
        ad = HostAdvertisement.Create(room, 0, new[] { "127.0.0.1" });
        Reject(() => HostAdvertisement.Read(ad.Encode(), room), "Invalid port accepted");
        ad = HostAdvertisement.Create(room, 27090, new[] { "example.com" });
        Reject(() => HostAdvertisement.Read(ad.Encode(), room), "Arbitrary DNS host accepted");
        Reject(() => HostAdvertisement.Read(Encoding.UTF8.GetBytes("not json"), room), "Malformed metadata accepted");
        Reject(() => HostAdvertisement.Read(new byte[32769], room), "Oversize metadata accepted");

        byte[] advertisement = null;
        int master = 3, count = 6;
        var controllers = Enumerable.Range(1, 7).Select(actor => new AutoRoomTransport(bytes => {
            if (actor != master) throw new Exception("Non-master tried to publish");
            advertisement = bytes; return true;
        }, () => new[] { "127.0.0.1", "127.0.0.2" }, 0)).ToArray();
        Action tick = () => { for (int i = 0; i < count; i++) controllers[i].Tick(Room(i + 1, master, count), advertisement); };
        try
        {
            Until(() => { tick(); return controllers.Take(count).All(c => c.Peer != null) && controllers[master - 1].Server.ConnectedCount == count; }, "Automatic six-player connection failed");
            Check(controllers[2].Server != null && controllers.Where((c, i) => i != 2).All(c => c.Server == null), "Wrong player became host");
            var epoch = HostAdvertisement.Read(advertisement, Room(1)).Epoch;
            count = 7;
            Until(() => { tick(); return controllers.All(c => c.Peer != null) && controllers[master - 1].Server.ConnectedCount == count; }, "Roster expansion did not rebuild automatically");
            Check(HostAdvertisement.Read(advertisement, Room(1, master, count)).Epoch != epoch, "Roster change reused session epoch");
            master = 2;
            Until(() => { tick(); return controllers.All(c => c.Peer != null) && controllers[master - 1].Server?.ConnectedCount == count; }, "New room master not adopted before activation");
            Check(controllers.Where((c, i) => i != master - 1).All(c => c.Server == null), "Two players remained hosts");
            Reject(() => controllers[0].Activate(Room(1, master, count)), "Non-host activated session");
            controllers[master - 1].Activate(Room(master, master, count));
            Until(() => controllers.All(c => c.Peer.Active), "Activation marker not received");
            foreach (var controller in controllers)
                for (int n = 0; n < 25; n++) controller.Peer.Send(0, BitConverter.GetBytes(n));
            var received = controllers.Select(_ => new System.Collections.Generic.List<Packet>()).ToArray();
            Until(() => {
                for (int i = 0; i < controllers.Length; i++) while (controllers[i].Peer.TryReceive(out var p)) received[i].Add(p);
                return received.All(p => p.Count == count * 25);
            }, "Automatically discovered peers did not exchange commands");
            foreach (var packets in received)
                Check(packets.Select(p => p.Sender + ":" + BitConverter.ToInt32(p.Payload, 0)).SequenceEqual(
                    received[0].Select(p => p.Sender + ":" + BitConverter.ToInt32(p.Payload, 0))), "Discovery changed common command order");
        }
        finally { foreach (var controller in controllers) controller.Dispose(); }

        // A TCP listener is insufficient: the acknowledged actor must be the room master.
        var fake = new TcpListener(IPAddress.Loopback, 0); fake.Start();
        try
        {
            ad = HostAdvertisement.Create(Room(3), ((IPEndPoint)fake.LocalEndpoint).Port, new[] { "127.0.0.1" });
            var responder = Task.Run(() => {
                using var socket = fake.AcceptTcpClient();
                var hello = Wire.Read(socket.GetStream());
                var bytes = Wire.Encode(new Packet { Kind = Wire.Welcome, Sender = 99, Target = 1, Payload = hello.Payload });
                socket.GetStream().Write(bytes, 0, bytes.Length);
            });
            Reject(() => ad.ConnectAsync(1, CancellationToken.None).GetAwaiter().GetResult(), "Unrelated listening host accepted");
            responder.GetAwaiter().GetResult();
        }
        finally { fake.Stop(); }
        // Leaving the room cancels and clears a pre-activation session.
        using (var controller = new AutoRoomTransport(_ => true, () => new[] { "127.0.0.1" }, 0))
        {
            controller.Tick(Room(3), null); Check(controller.Server != null, "Host setup failed");
            controller.Tick(null, null); Check(controller.Server == null && controller.Peer == null, "Room exit retained transport");
        }
        Console.WriteLine("PASS: " + checks + " discovery checks; metadata binding, automatic host/client connection, six/seven-player rosters, host change before activation, ACK identity and common ordering.");
    }
}
