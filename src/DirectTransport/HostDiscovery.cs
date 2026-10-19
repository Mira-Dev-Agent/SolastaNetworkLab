using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Solasta.DirectTransport
{
    public sealed class RoomSnapshot
    {
        public int LocalActor, MasterActor;
        public string Room, GameHash, ModVersion;
        public int[] Actors;
        public string RosterHash => HostAdvertisement.Hash(string.Join(",", Actors.OrderBy(x => x)));
        public string Identity => Room + ":" + MasterActor + ":" + GameHash + ":" + ModVersion + ":" + RosterHash;
    }

    [DataContract]
    public sealed class HostAdvertisement
    {
        public const string PropertyName = "snl.host.v1";
        public const string Protocol = "direct-v0.4.0";
        [DataMember] public int HostActor { get; set; }
        [DataMember] public int Port { get; set; }
        [DataMember] public string[] Addresses { get; set; }
        [DataMember] public string Room { get; set; }
        [DataMember] public string GameHash { get; set; }
        [DataMember] public string ModVersion { get; set; }
        [DataMember] public string RosterHash { get; set; }
        [DataMember] public string Epoch { get; set; }
        [DataMember] public string Token { get; set; }
        [DataMember] public string TransportProtocol { get; set; }
        public string SessionKey => Protocol + ":" + Hash(Room) + ":" + HostActor + ":" + GameHash + ":" + RosterHash + ":" + Epoch + ":" + Token;
        public static HostAdvertisement Create(RoomSnapshot room, int port, string[] addresses)
            => new HostAdvertisement { HostActor = room.MasterActor, Room = room.Room, GameHash = room.GameHash,
                ModVersion = room.ModVersion, RosterHash = room.RosterHash, Port = port, Addresses = addresses.Distinct().ToArray(),
                Epoch = Guid.NewGuid().ToString("N"), Token = Guid.NewGuid().ToString("N"), TransportProtocol = Protocol };
        public byte[] Encode()
        {
            using (var stream = new MemoryStream())
            { new DataContractJsonSerializer(typeof(HostAdvertisement)).WriteObject(stream, this); return stream.ToArray(); }
        }
        public static HostAdvertisement Read(byte[] bytes, RoomSnapshot room)
        {
            if (bytes == null || bytes.Length > 32768) throw new InvalidDataException("Invalid host advertisement size");
            HostAdvertisement ad;
            using (var stream = new MemoryStream(bytes)) ad = (HostAdvertisement)new DataContractJsonSerializer(typeof(HostAdvertisement)).ReadObject(stream);
            Guid id;
            if (ad == null || ad.HostActor != room.MasterActor || ad.Room != room.Room || ad.GameHash != room.GameHash
                || ad.ModVersion != room.ModVersion || ad.RosterHash != room.RosterHash || ad.TransportProtocol != Protocol
                || ad.Port < 1 || ad.Port > 65535 || !Guid.TryParseExact(ad.Epoch, "N", out id) || !Guid.TryParseExact(ad.Token, "N", out id)
                || ad.Addresses == null || ad.Addresses.Length == 0)
                throw new InvalidDataException("Host advertisement does not match the current room/master/version/roster");
            foreach (var address in ad.Addresses)
            {
                IPAddress ip;
                if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any)
                    || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224)
                    throw new InvalidDataException("Invalid host address");
            }
            return ad;
        }
        public static string Hash(string value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        public static string[] LocalAddresses()
        {
            var addresses = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                        addresses.Add(address.Address.ToString());
            // Also permits multiple game instances on one machine. The room handshake rejects an unrelated listener.
            addresses.Add(IPAddress.Loopback.ToString());
            return addresses.Distinct().ToArray();
        }
        public async Task<Client> ConnectAsync(int actor, CancellationToken token)
        {
            // Try all advertised interfaces; a reachable TCP port alone is not proof that this is the room host.
            var attempts = Addresses.Distinct().Select(address => Task.Run(async () => {
                Client candidate = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    candidate = new Client(address, Port, actor, SessionKey, HostActor);
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!candidate.Acknowledged && candidate.Failure == null && DateTime.UtcNow < deadline)
                    { await Task.Delay(25, token).ConfigureAwait(false); }
                    if (!candidate.Acknowledged || candidate.Failure != null) throw new IOException("Host handshake failed");
                    token.ThrowIfCancellationRequested();
                    return candidate;
                }
                catch { candidate?.Dispose(); throw; }
            })).ToList();
            Client winner = null;
            try
            {
                while (attempts.Count > 0)
                {
                    var done = await Task.WhenAny(attempts).ConfigureAwait(false); attempts.Remove(done);
                    try { winner = await done.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return winner; }
                    catch (OperationCanceledException) { throw; }
                    catch { /* Other interfaces may still reach the same host. */ }
                }
                throw new IOException("Cannot reach the room host. Check the host firewall and use a shared LAN/VPN route.");
            }
            finally
            {
                if (token.IsCancellationRequested) winner?.Dispose();
                foreach (var pending in attempts)
                    _ = pending.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else { var ignored = t.Exception; } }, TaskScheduler.Default);
            }
        }
    }

    // Called on the Unity thread; only socket dial attempts run on workers.
    public sealed class AutoRoomTransport : IDisposable
    {
        private readonly Func<byte[], bool> publish;
        private readonly Func<string[]> addresses;
        private readonly int listenPort;
        private HostAdvertisement advertisement;
        private string identity;
        private Task<Client> dialing;
        private CancellationTokenSource cancellation;
        private DateTime retryAt, publishAt;
        public IGameplayPeer Peer { get; private set; }
        public HostServer Server { get; private set; }
        public string Status { get; private set; } = "Join a multiplayer room to discover its host.";
        public AutoRoomTransport(Func<byte[], bool> publish, Func<string[]> addresses = null, int listenPort = 27090)
        { this.publish = publish; this.addresses = addresses ?? HostAdvertisement.LocalAddresses; this.listenPort = listenPort; }
        public void Tick(RoomSnapshot room, byte[] advertised)
        {
            if (room == null)
            { Reset(); Status = "Join a multiplayer room to discover its host."; return; }
            if (identity != room.Identity) { Reset(); identity = room.Identity; }
            if (Peer != null && Peer.Active) return;
            if (Peer != null && Peer.Failure != null) { Reset(); identity = room.Identity; retryAt = DateTime.UtcNow.AddSeconds(2); }
            try
            {
                if (room.LocalActor == room.MasterActor)
                {
                    if (Server == null && DateTime.UtcNow >= retryAt)
                    {
                        var endpoints = addresses();
                        // Validate the advertisement before creating any listener.
                        var ad = HostAdvertisement.Create(room, Math.Max(1, listenPort), endpoints);
                        HostAdvertisement.Read(ad.Encode(), room);
                        try { Server = new HostServer(IPAddress.Any, listenPort, room.MasterActor, room.Actors, ad.SessionKey); }
                        catch (SocketException) when (listenPort != 0)
                        { Server = new HostServer(IPAddress.Any, 0, room.MasterActor, room.Actors, ad.SessionKey); }
                        ad.Port = Server.Port; advertisement = ad; Peer = Server.Host;
                    }
                    if (Server != null && DateTime.UtcNow >= publishAt)
                    {
                        publishAt = DateTime.UtcNow.AddSeconds(2);
                        var bytes = advertisement.Encode();
                        if (advertised == null || !bytes.SequenceEqual(advertised)) publish(bytes);
                        Status = "You are the room host. Automatically connected players: " + Server.ConnectedCount + "/" + Server.ExpectedCount;
                    }
                    return;
                }
                if (dialing != null && dialing.IsCompleted)
                {
                    var done = dialing; dialing = null;
                    if (done.Status == TaskStatus.RanToCompletion) Peer = done.Result;
                    else { var ignored = done.Exception; retryAt = DateTime.UtcNow.AddSeconds(5); Status = "Cannot reach the room host; retrying. Check the host firewall and shared LAN/VPN."; }
                }
                HostAdvertisement next;
                try { next = HostAdvertisement.Read(advertised, room); }
                catch { if (Peer != null || dialing != null) { Reset(); identity = room.Identity; } Status = "Waiting for the current room host to advertise a compatible direct connection."; return; }
                if (advertisement != null && advertisement.Epoch != next.Epoch)
                { Reset(); identity = room.Identity; }
                advertisement = next;
                if (Peer != null) { Status = "Automatically connected to room host #" + room.MasterActor + "; waiting for activation."; return; }
                if (dialing == null && DateTime.UtcNow >= retryAt)
                {
                    cancellation = new CancellationTokenSource();
                    dialing = next.ConnectAsync(room.LocalActor, cancellation.Token);
                    Status = "Automatically connecting to room host #" + room.MasterActor + "...";
                }
            }
            catch (Exception)
            { Reset(); identity = room.Identity; retryAt = DateTime.UtcNow.AddSeconds(5); Status = "Automatic host setup failed; retrying. Check host network availability."; }
        }
        public void Activate(RoomSnapshot current)
        {
            if (current == null || current.Identity != identity || current.LocalActor != current.MasterActor || Server == null)
                throw new InvalidOperationException("Only the current room host can activate this roster");
            if (current.Actors.Length < 2) throw new InvalidOperationException("Wait for the other room players before activating");
            Server.Activate();
        }
        private void Reset()
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            var pending = dialing; dialing = null;
            if (pending != null) _ = pending.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); else { var ignored = t.Exception; } }, TaskScheduler.Default);
            Peer?.Dispose(); Server?.Dispose(); Peer = null; Server = null;
            identity = null; advertisement = null; retryAt = DateTime.MinValue; publishAt = DateTime.MinValue;
        }
        public void Dispose() { Reset(); }
    }
}
