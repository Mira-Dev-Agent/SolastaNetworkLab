using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using Solasta.DirectTransport;
using UnityEngine;
using UnityModManagerNet;
using InvalidDataException = System.IO.InvalidDataException;

namespace Solasta.NetworkLab
{
    public static class Main
    {
        private static UnityModManager.ModEntry mod;
        private static Harmony harmony;
        private static UpdatePanel updates;
        private static IGameplayPeer client;
        private static HostServer relay;
        private static AutoRoomTransport automatic;
        private static string status = "Join a multiplayer room to discover its host.";
        private static bool automaticEnabled = true;
        private static int hostActor;
        private static int[] roster;
        private static bool armed, faulted, dispatchingDirect;
        private static StreamWriter log;
        private static float lastSample;
        private static string binaryHash;
        private const string SupportedHash = "a87e45cbd187e42e9d64a610850b39edba4896168b14d71ca71c5d32cf9e96ee";
        private static readonly Dictionary<string, MethodInfo> rpcs = typeof(NetworkingManager)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(typeof(PunRPC), true).Length != 0).ToDictionary(m => m.Name);

        public static bool Load(UnityModManager.ModEntry entry)
        {
            mod = entry;
            // Logs stay next to this mod. No save, account, chat or RPC payload data is recorded.
            string logPath = Path.Combine(entry.Path, "network-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log");
            log = new StreamWriter(logPath) { AutoFlush = true };
            binaryHash = Hash(File.ReadAllBytes(typeof(NetworkingManager).Assembly.Location));
            Write("loaded assembly_sha256=" + binaryHash + " rpc_count=" + rpcs.Count);
            harmony = new Harmony("solasta.networklab");
            try { harmony.PatchAll(typeof(Main).Assembly); }
            catch { harmony.UnpatchAll(harmony.Id); log.Dispose(); throw; }
            entry.OnGUI = OnGUI;
            entry.OnUpdate = OnUpdate;
            updates = new UpdatePanel(entry.Path);
            entry.OnUnload = _ =>
            {
                // Unloading would mix transport streams during an active session.
                if (armed) return false;
                updates?.Dispose(); automatic?.Dispose(); client?.Dispose(); relay?.Dispose(); harmony.UnpatchAll(harmony.Id); log.Dispose(); return true;
            };
            return true;
        }
        private static string Hash(byte[] data)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        internal static void Write(string text) { log?.WriteLine(DateTime.UtcNow.ToString("O") + " " + text); }
        private static void Fault(Exception e)
        {
            if (faulted) return;
            faulted = true; status = "Transport stopped: " + e.Message + ". Quit to main menu and restart the game.";
            Write("FAULT " + e.GetType().Name + " " + e.Message);
            mod.Logger.Error(status); client?.Dispose(); relay?.Dispose();
        }
        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            updates?.Draw();
            GUILayout.Label("Experimental gameplay RPC transport. Photon still provides lobby, identity and file setup.");
            GUILayout.Label("Use a disposable save, identical game/mod settings, and trusted LAN/VPN peers. No encryption or NAT relay.");
            GUILayout.Label("The room host is detected automatically. Clients connect without entering an IP, port or key.");
            GUILayout.Label("Join the same room on a shared LAN/VPN, then the room host activates before starting/loading gameplay.");
            GUILayout.Label("MaxPlayers is controlled by the game or Unfinished Business. The transport adds no fixed player-count cap.");
            GUILayout.Label(status);
            if (armed) { GUILayout.Label("Active session: restart game to return to Photon gameplay."); return; }
            bool enabled = GUILayout.Toggle(automaticEnabled, "Automatically connect to this room's host");
            if (enabled != automaticEnabled)
            {
                automaticEnabled = enabled;
                if (!enabled && relay != null) PublishAdvertisement(null);
                automatic?.Dispose(); automatic = null; client = null; relay = null;
            }
            if (relay != null && relay.ExpectedCount > 1 && relay.ConnectedCount == relay.ExpectedCount
                && GUILayout.Button("Activate when all room players are connected"))
            {
                try { CheckRoster(); automatic.Activate(CurrentRoom()); Arm(); }
                catch (Exception e) { status = e.Message; }
            }
        }
        private static RoomSnapshot CurrentRoom()
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode || PhotonNetwork.MasterClient == null) return null;
            return new RoomSnapshot { LocalActor = PhotonNetwork.LocalPlayer.ActorNumber, MasterActor = PhotonNetwork.MasterClient.ActorNumber,
                Room = PhotonNetwork.CurrentRoom.Name, GameHash = binaryHash, ModVersion = typeof(Main).Assembly.GetName().Version.ToString(3),
                Actors = PhotonNetwork.PlayerList.Select(p => p.ActorNumber).OrderBy(id => id).ToArray() };
        }
        private static bool PublishAdvertisement(byte[] bytes)
        {
            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient || PhotonNetwork.OfflineMode) return false;
            return PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { [HostAdvertisement.PropertyName] = bytes });
        }
        private static void UpdateAutomaticSetup()
        {
            var room = CurrentRoom();
            if (!automaticEnabled || room == null || binaryHash != SupportedHash || ServiceRepository.GetService<IGameService>()?.Game != null)
            {
                automatic?.Dispose(); automatic = null; client = null; relay = null;
                status = !automaticEnabled ? "Diagnostics only; automatic direct transport disabled."
                    : binaryHash != SupportedHash ? "Direct mode is unavailable for this unaudited game assembly."
                    : room == null ? "Join a multiplayer room to discover its host." : "Automatic setup is available in the room lobby before gameplay.";
                return;
            }
            if (automatic == null) automatic = new AutoRoomTransport(PublishAdvertisement);
            object raw;
            PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(HostAdvertisement.PropertyName, out raw);
            automatic.Tick(room, raw as byte[]);
            client = automatic.Peer; relay = automatic.Server; roster = room.Actors; hostActor = room.MasterActor;
            status = automatic.Status;
        }
        private static void Arm()
        {
            armed = true; status = "Direct TCP gameplay transport active; room host #" + hostActor; Write("transport_activated host_actor=" + hostActor);
        }
        private static void CheckRoster()
        {
            if (!PhotonNetwork.InRoom || PhotonNetwork.MasterClient?.ActorNumber != hostActor
                || !PhotonNetwork.PlayerList.Select(p => p.ActorNumber).OrderBy(id => id).SequenceEqual(roster))
                throw new InvalidOperationException("Room host or membership changed");
        }
        private static void OnUpdate(UnityModManager.ModEntry entry, float dt)
        {
            updates?.Tick(!armed && (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) && Gui.GuiService != null
                && ServiceRepository.GetService<IGameService>()?.Game == null);
            if (client != null && client.Active && !armed)
                Arm();
            if (!armed) UpdateAutomaticSetup();
            if (client != null && client.Active && !armed) Arm();
            if (armed && !faulted)
            { try { CheckRoster(); } catch (Exception e) { Fault(e); } }
            if (armed && client != null && client.Failure != null) Fault(new IOException(client.Failure));
            if (armed && !faulted && (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode)) Fault(new IOException("Photon control connection lost"));
            try
            {
                Packet p;
                while (!faulted && client != null && client.TryReceive(out p))
                {
                    if (!Client.IsRecipient(p, client.Actor)) continue;
                    var values = (object[])Protocol.Deserialize(p.Payload);
                    var method = rpcs[(string)values[0]];
                    var args = (object[])values[1];
                    var signature = method.GetParameters();
                    if (signature.Length == args.Length + 1 && signature[signature.Length - 1].ParameterType == typeof(PhotonMessageInfo))
                    {
                        if (PhotonNetwork.CurrentRoom.GetPlayer(p.Sender) == null) throw new InvalidDataException("Unknown sender");
                        Array.Resize(ref args, args.Length + 1);
                        args[args.Length - 1] = new PhotonMessageInfo(PhotonNetwork.CurrentRoom.GetPlayer(p.Sender), PhotonNetwork.ServerTimestamp, null);
                    }
                    if (signature.Length != args.Length) throw new InvalidDataException("RPC signature mismatch");
                    var manager = ServiceRepository.GetService<INetworkingService>() as NetworkingManager;
                    if (manager == null) throw new InvalidOperationException("NetworkingManager unavailable");
                    dispatchingDirect = true;
                    try { method.Invoke(manager, args); }
                    finally { dispatchingDirect = false; }
                }
            }
            catch (Exception e) { Fault(e); }
            if (faulted && armed)
            {
                var time = ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign?.GameTime;
                if (time != null) time.NetworkTimeScale = 0f;
            }
            if (Time.unscaledTime - lastSample >= 1f)
            {
                lastSample = Time.unscaledTime;
                var manager = ServiceRepository.GetService<INetworkingService>() as NetworkingManager;
                Write("sample state=" + PhotonNetwork.NetworkClientState + " ping_ms=" + PhotonNetwork.GetPing()
                    + " transport=" + (armed ? "direct" : "photon") + " tick=" + manager?.Tick.LocalTick
                    + " min=" + manager?.Tick.MinTick + " max=" + manager?.Tick.MaxTick + " waiting=" + manager?.Tick.WaitingForPlayer);
            }
        }
        [HarmonyPatch(typeof(PhotonView), "RPC", new[] { typeof(string), typeof(RpcTarget), typeof(object[]) })]
        private static class SendPatch
        {
            private static bool Prefix(PhotonView __instance, string methodName, RpcTarget target, object[] parameters)
            {
                if (__instance.GetComponent<NetworkingManager>() == null) return true;
                if (!armed && client != null && client.Active) Arm();
                if (!armed) return true;
                if (faulted) return false;
                try
                {
                    int destination;
                    switch (target)
                    {
                        // All actors are present before activation and late joins are rejected.
                        // Buffered readiness RPCs therefore need live delivery, without replay caching.
                        case RpcTarget.AllViaServer: case RpcTarget.All: case RpcTarget.AllBufferedViaServer: case RpcTarget.AllBuffered: destination = 0; break;
                        case RpcTarget.Others: case RpcTarget.OthersBuffered: destination = -1; break;
                        case RpcTarget.MasterClient: destination = PhotonNetwork.MasterClient.ActorNumber; break;
                        default: throw new NotSupportedException("Buffered RPC target is not supported");
                    }
                    client.Send(destination, Protocol.Serialize(new object[] { methodName, parameters }));
                }
                catch (Exception e) { Fault(e); }
                return false;
            }
        }
        [HarmonyPatch(typeof(NetworkingManager), "FixedUpdate")]
        private static class FreezeOnFailure
        { private static bool Prefix() => !faulted || !armed; }
        // Any unexpected overload must stop rather than split the gameplay stream.
        [HarmonyPatch]
        private static class UnsupportedSend
        {
            private static IEnumerable<MethodBase> TargetMethods() => typeof(PhotonView).GetMethods()
                .Where(m => m.Name == "RpcSecure" || m.Name == "RPC" && m.GetParameters()[1].ParameterType == typeof(Player));
            private static bool Prefix(PhotonView __instance)
            {
                if (!armed || __instance.GetComponent<NetworkingManager>() == null) return true;
                Fault(new NotSupportedException("Unexpected Photon RPC overload")); return false;
            }
        }
        [HarmonyPatch(typeof(NetworkingManager), "OnDisconnected")]
        private static class DisconnectLog
        { private static void Prefix(DisconnectCause cause) { Write("photon_disconnect cause=" + cause); if (armed) Fault(new IOException("Photon disconnected: " + cause)); } }
        [HarmonyPatch(typeof(NetworkingManager), "OnPlayerEnteredRoom")]
        private static class MembershipLog
        { private static void Prefix() { if (armed) Fault(new IOException("Membership changed after activation")); } }
        [HarmonyPatch(typeof(NetworkingManager), "OnPlayerLeftRoom")]
        private static class DepartureLog
        { private static void Prefix() { if (armed) Fault(new IOException("Player left active session")); } }
        [HarmonyPatch]
        private static class ReceiveLog
        {
            private static IEnumerable<MethodBase> TargetMethods() => rpcs.Values;
            private static bool Prefix(MethodBase __originalMethod, object[] __args)
            {
                if (armed && !dispatchingDirect)
                { Fault(new InvalidOperationException("Photon RPC received after direct activation")); return false; }
                string name = __originalMethod.Name;
                if (name == "NotifyTickRPC" || name == "NotifyPingRPC" || name == "NotifyPlayerWaitingTimeRPC") return true;
                int sender = __args.OfType<PhotonMessageInfo>().Select(info => info.Sender?.ActorNumber ?? 0).FirstOrDefault();
                string payloadHash;
                try { payloadHash = Hash(Protocol.Serialize(__args.Where(a => !(a is PhotonMessageInfo)).ToArray())); }
                catch { payloadHash = "unavailable"; }
                Write("rpc method=" + name + " sender=" + sender + " tick=" + (__args.Length > 0 && __args[0] is long ? __args[0].ToString() : "-") + " args_sha256=" + payloadHash);
                return true;
            }
        }
        [HarmonyPatch(typeof(RulesetEntityManager), "CheckCurEntityGuidByTick")]
        private static class GuidCheckLog
        {
            private static readonly FieldInfo map = AccessTools.Field(typeof(RulesetEntityManager), "entityGuidByTick");
            private static void Prefix(RulesetEntityManager __instance, ulong localTick, ulong incomingGuid)
            {
                var values = (Dictionary<ulong, ulong>)map.GetValue(__instance);
                ulong other;
                if (values.TryGetValue(localTick, out other) && other != incomingGuid)
                    Write("ENTITY_GUID_DIVERGENCE tick=" + localTick + " previous=" + other + " incoming=" + incomingGuid);
            }
        }
    }
}
