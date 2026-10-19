using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Solasta.DirectTransport
{
    public interface IGameplayPeer : IDisposable
    {
        int Actor { get; }
        bool Active { get; }
        string Failure { get; }
        void Send(int target, byte[] payload);
        bool TryReceive(out Packet packet);
    }
    public sealed class Packet
    {
        public byte Kind;
        public long Sequence;
        public int Sender;
        // 0 = all; -1 = others; positive = actor id.
        public int Target;
        public byte[] Payload = new byte[0];
    }

    public static class Wire
    {
        public const int MaxPayload = 1024 * 1024;
        public const byte Hello = 1, Start = 2, Data = 3, Heartbeat = 4;
        public static byte[] Encode(Packet p)
        {
            if (p.Payload == null || p.Payload.Length > MaxPayload) throw new InvalidDataException("Payload limit");
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(0x534f4c31); w.Write(p.Kind); w.Write(p.Sequence);
                w.Write(p.Sender); w.Write(p.Target); w.Write(p.Payload.Length); w.Write(p.Payload);
                return ms.ToArray();
            }
        }
        public static Packet Read(Stream stream)
        {
            using (var r = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (r.ReadInt32() != 0x534f4c31) throw new InvalidDataException("Protocol mismatch");
                var p = new Packet { Kind = r.ReadByte(), Sequence = r.ReadInt64(), Sender = r.ReadInt32(), Target = r.ReadInt32() };
                var length = r.ReadInt32();
                if (length < 0 || length > MaxPayload) throw new InvalidDataException("Payload limit");
                p.Payload = r.ReadBytes(length);
                if (p.Payload.Length != length) throw new EndOfStreamException();
                return p;
            }
        }
    }

    // Each socket has one writer. Backpressure is bounded and fails the session;
    // commands are never discarded, retried or silently rerouted through Photon.
    internal sealed class Link : IDisposable
    {
        private readonly TcpClient socket;
        private readonly BlockingCollection<byte[]> outbound = new BlockingCollection<byte[]>(1024);
        private int closed;
        public Action<Exception> Failed;
        public Link(TcpClient socket)
        {
            this.socket = socket;
            socket.NoDelay = true;
            socket.SendTimeout = 15000;
            socket.ReceiveTimeout = 15000;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            new Thread(WriteLoop) { IsBackground = true, Name = "Solasta relay writer" }.Start();
        }
        public void Send(Packet packet)
        {
            if (Volatile.Read(ref closed) != 0 || !outbound.TryAdd(Wire.Encode(packet)))
                throw new IOException("Transport closed or outbound queue full");
        }
        public Packet Read() => Wire.Read(socket.GetStream());
        private void WriteLoop()
        {
            try
            {
                foreach (var bytes in outbound.GetConsumingEnumerable()) socket.GetStream().Write(bytes, 0, bytes.Length);
            }
            catch (Exception e) { if (Volatile.Read(ref closed) == 0) Failed?.Invoke(e); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            outbound.CompleteAdding(); socket.Close();
        }
    }

    public sealed class HostServer : IDisposable
    {
        private readonly object gate = new object();
        private readonly TcpListener listener;
        private readonly Dictionary<int, Link> peers = new Dictionary<int, Link>();
        private readonly HashSet<Link> connections = new HashSet<Link>();
        private readonly HashSet<int> expected;
        private readonly string key;
        private bool started, closed;
        private long sequence;
        public LocalPeer Host { get; }
        public string Failure { get; private set; }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int ConnectedCount { get { lock (gate) return peers.Count + 1; } }
        public int ExpectedCount => expected.Count;
        public HostServer(IPAddress bind, int port, int hostActor, IEnumerable<int> actors, string sessionKey)
        {
            expected = new HashSet<int>(actors);
            if (!expected.Contains(hostActor) || expected.Any(x => x <= 0)) throw new ArgumentException("Roster must contain the positive host actor");
            if (string.IsNullOrEmpty(sessionKey) || sessionKey.Length < 16) throw new ArgumentException("Session key must have at least 16 characters");
            key = sessionKey;
            Host = new LocalPeer(this, hostActor);
            listener = new TcpListener(bind, port); listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "Solasta host accept" }.Start();
        }
        private void AcceptLoop()
        {
            try
            {
                while (true)
                {
                    var socket = listener.AcceptTcpClient();
                    Link link;
                    lock (gate)
                    {
                        if (closed || started) { socket.Close(); continue; }
                        link = new Link(socket); connections.Add(link);
                    }
                    link.Failed = e => Lost(link, e);
                    new Thread(() => ReadPeer(link)) { IsBackground = true, Name = "Solasta relay reader" }.Start();
                }
            }
            catch (Exception e) { lock (gate) { if (!closed) Fail(e); } }
        }
        private void ReadPeer(Link link)
        {
            try
            {
                var hello = link.Read();
                int actor = hello.Sender;
                lock (gate)
                {
                    if (closed || started || hello.Kind != Wire.Hello || actor == Host.Actor || !expected.Contains(actor) || peers.ContainsKey(actor)
                        || hello.Payload.Length > 512 || Encoding.UTF8.GetString(hello.Payload) != key)
                        throw new InvalidDataException("Rejected handshake");
                    peers.Add(actor, link);
                }
                while (true)
                {
                    var p = link.Read();
                    lock (gate)
                    {
                        if (closed) return;
                        if (p.Kind == Wire.Heartbeat && p.Sender == actor && p.Payload.Length == 0)
                        { link.Send(new Packet { Kind = Wire.Heartbeat }); continue; }
                        if (p.Sender != actor) throw new InvalidDataException("Sender mismatch");
                        Broadcast(p);
                    }
                }
            }
            catch (Exception e) { Lost(link, e); }
        }
        // Both the local host and remote clients enter this same sequencing lock.
        // Host commands never execute early or bypass the common receiver order.
        internal void SendHost(int target, byte[] payload)
        {
            lock (gate)
            {
                try { Broadcast(new Packet { Kind = Wire.Data, Sender = Host.Actor, Target = target, Payload = payload }); }
                catch (Exception e) { Fail(e); throw; }
            }
        }
        private void Broadcast(Packet p)
        {
            if (closed || !started || p.Kind != Wire.Data || p.Sequence != 0 || p.Target < -1
                || (p.Target > 0 && !expected.Contains(p.Target))) throw new InvalidDataException("Invalid command envelope");
            // Validate even for a host-only group; remote Link.Send performs this validation too.
            Wire.Encode(p);
            p.Sequence = ++sequence;
            foreach (var peer in peers.Values) peer.Send(p);
            Host.Deliver(p);
        }
        private void Lost(Link link, Exception e)
        {
            lock (gate)
            {
                bool admitted = peers.Values.Contains(link);
                connections.Remove(link);
                foreach (var id in peers.Where(p => p.Value == link).Select(p => p.Key).ToArray()) peers.Remove(id);
                link.Dispose();
                if (started && admitted && !closed) Fail(e);
            }
        }
        public void Activate()
        {
            lock (gate)
            {
                if (closed || started || peers.Count + 1 != expected.Count) throw new InvalidOperationException("Every roster actor must connect before activation");
                started = true;
                try { foreach (var peer in peers.Values) peer.Send(new Packet { Kind = Wire.Start }); Host.Activate(); }
                catch (Exception e) { Fail(e); throw; }
            }
        }
        private void Fail(Exception e) { Failure = e.GetType().Name + ": " + e.Message; Dispose(); }
        public void Dispose()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true; listener.Stop();
                Host.Stop(Failure ?? "Host server closed");
                foreach (var peer in connections) peer.Dispose();
                connections.Clear(); peers.Clear();
            }
        }
    }

    public sealed class LocalPeer : IGameplayPeer
    {
        private readonly HostServer server;
        private readonly ConcurrentQueue<Packet> incoming = new ConcurrentQueue<Packet>();
        private int queued;
        private volatile bool active;
        private string failure;
        public int Actor { get; }
        public bool Active => active;
        public string Failure => Volatile.Read(ref failure);
        internal LocalPeer(HostServer server, int actor) { this.server = server; Actor = actor; }
        internal void Activate() { active = true; }
        internal void Stop(string reason) { Volatile.Write(ref failure, reason); active = false; }
        internal void Deliver(Packet p)
        {
            if (Interlocked.Increment(ref queued) > 4096) throw new IOException("Host inbound queue full");
            incoming.Enqueue(p);
        }
        public void Send(int target, byte[] payload)
        {
            if (!active || Failure != null) throw new InvalidOperationException("Host transport is not active");
            server.SendHost(target, payload);
        }
        public bool TryReceive(out Packet packet)
        {
            if (!incoming.TryDequeue(out packet)) return false;
            Interlocked.Decrement(ref queued); return true;
        }
        public void Dispose() { server.Dispose(); }
    }

    public sealed class Client : IGameplayPeer
    {
        private readonly Link link;
        private readonly ConcurrentQueue<Packet> incoming = new ConcurrentQueue<Packet>();
        private int queued;
        private int closed;
        private long received;
        public int Actor { get; }
        private volatile bool active;
        private string failure;
        public bool Active => active;
        public string Failure => Volatile.Read(ref failure);
        public Client(string host, int port, int actor, string sessionKey)
        {
            Actor = actor;
            var socket = new TcpClient();
            try
            {
                var task = socket.ConnectAsync(host, port);
                if (!task.Wait(5000)) throw new IOException("Connect timeout");
                task.GetAwaiter().GetResult();
                link = new Link(socket);
                link.Failed = Fail;
                link.Send(new Packet { Kind = Wire.Hello, Sender = actor, Payload = Encoding.UTF8.GetBytes(sessionKey) });
                new Thread(ReadLoop) { IsBackground = true, Name = "Solasta direct receiver" }.Start();
                new Thread(HeartbeatLoop) { IsBackground = true, Name = "Solasta direct heartbeat" }.Start();
            }
            catch { socket.Close(); throw; }
        }
        public void Send(int target, byte[] payload)
        {
            if (!Active || Failure != null) throw new InvalidOperationException("Transport is not active");
            link.Send(new Packet { Kind = Wire.Data, Sender = Actor, Target = target, Payload = payload });
        }
        public bool TryReceive(out Packet packet)
        {
            if (!incoming.TryDequeue(out packet)) return false;
            Interlocked.Decrement(ref queued); return true;
        }
        public static bool IsRecipient(Packet p, int actor) => p.Target == 0 || p.Target == actor || (p.Target == -1 && p.Sender != actor);
        private void ReadLoop()
        {
            try
            {
                while (true)
                {
                    var p = link.Read();
                    if (p.Kind == Wire.Heartbeat && p.Payload.Length == 0) continue;
                    if (p.Kind == Wire.Start && !Active && received == 0 && p.Sequence == 0) { active = true; continue; }
                    if (!Active || p.Kind != Wire.Data || p.Sequence != received + 1) throw new InvalidDataException("Sequence gap or protocol error");
                    received = p.Sequence;
                    if (Interlocked.Increment(ref queued) > 4096) throw new IOException("Inbound queue full");
                    incoming.Enqueue(p);
                }
            }
            catch (Exception e) { if (Volatile.Read(ref closed) == 0) Fail(e); }
        }
        private void HeartbeatLoop()
        {
            while (Volatile.Read(ref closed) == 0)
            {
                Thread.Sleep(3000);
                if (Volatile.Read(ref closed) != 0) return;
                try { link.Send(new Packet { Kind = Wire.Heartbeat, Sender = Actor }); }
                catch (Exception e) { Fail(e); return; }
            }
        }
        private void Fail(Exception e) { Volatile.Write(ref failure, e.GetType().Name + ": " + e.Message); Dispose(); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            active = false; link.Dispose();
        }
    }
}
