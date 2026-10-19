using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityModManagerNet;

namespace Solasta.NetworkLab
{
    // Diagnostics only. Gameplay calls and Photon callbacks always retain their original behavior.
    public static class Main
    {
        private static Harmony harmony;
        private static UpdatePanel updates;
        private static StreamWriter log;
        private static float lastSample;
        private static long receiveOrdinal;
        private static readonly MethodInfo[] rpcs = typeof(NetworkingManager)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes(typeof(PunRPC), true).Length != 0).ToArray();

        public static bool Load(UnityModManager.ModEntry entry)
        {
            log = new StreamWriter(Path.Combine(entry.Path, "network-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log")) { AutoFlush = true };
            Write("loaded transport=photon diagnostics_only=true assembly_sha256="
                + Hash(File.ReadAllBytes(typeof(NetworkingManager).Assembly.Location)) + " rpc_count=" + rpcs.Length);
            harmony = new Harmony("solasta.networklab");
            try { harmony.PatchAll(typeof(Main).Assembly); }
            catch { harmony.UnpatchAll(harmony.Id); log.Dispose(); throw; }
            entry.OnGUI = OnGUI;
            entry.OnUpdate = OnUpdate;
            updates = new UpdatePanel(entry.Path);
            entry.OnUnload = _ => {
                updates?.Dispose(); harmony.UnpatchAll(harmony.Id); log.Dispose(); log = null; return true;
            };
            return true;
        }
        private static string Hash(byte[] data)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        internal static void Write(string text)
        {
            // Diagnostic I/O must never interrupt a game command.
            try { log?.WriteLine(DateTime.UtcNow.ToString("O") + " " + text); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            updates?.Draw();
            GUILayout.Label("Photon networking is used normally for all multiplayer traffic.");
            GUILayout.Label("Join and host through the game's normal multiplayer screens. No direct connection setup is required.");
            GUILayout.Label("MaxPlayers is controlled by the game or Unfinished Business.");
            if (PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode)
                GUILayout.Label("Current room host: #" + PhotonNetwork.MasterClient?.ActorNumber);
            GUILayout.Label("Synchronization diagnostics are enabled. Host command-order enforcement is a proposed feature, not active in this version.");
        }
        private static void OnUpdate(UnityModManager.ModEntry entry, float dt)
        {
            updates?.Tick((!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode) && Gui.GuiService != null
                && ServiceRepository.GetService<IGameService>()?.Game == null);
            if (Time.unscaledTime - lastSample < 1f) return;
            lastSample = Time.unscaledTime;
            var manager = ServiceRepository.GetService<INetworkingService>() as NetworkingManager;
            Write("sample state=" + PhotonNetwork.NetworkClientState + " ping_ms=" + PhotonNetwork.GetPing()
                + " transport=photon host_actor=" + PhotonNetwork.MasterClient?.ActorNumber + " tick=" + manager?.Tick.LocalTick
                + " min=" + manager?.Tick.MinTick + " max=" + manager?.Tick.MaxTick + " waiting=" + manager?.Tick.WaitingForPlayer);
        }
        [HarmonyPatch(typeof(NetworkingManager), "OnDisconnected")]
        private static class DisconnectLog
        { private static void Prefix(DisconnectCause cause) { Write("photon_disconnect cause=" + cause); } }
        [HarmonyPatch(typeof(NetworkingManager), "OnMasterClientSwitched")]
        private static class MasterLog
        { private static void Prefix(Player newMasterClient) { Write("photon_master_changed actor=" + newMasterClient?.ActorNumber); } }
        [HarmonyPatch]
        private static class ReceiveLog
        {
            private static IEnumerable<MethodBase> TargetMethods() => rpcs;
            private static void Prefix(MethodBase __originalMethod, object[] __args)
            {
                string name = __originalMethod.Name;
                if (name == "NotifyTickRPC" || name == "NotifyPingRPC" || name == "NotifyPlayerWaitingTimeRPC") return;
                int sender = __args.OfType<PhotonMessageInfo>().Select(info => info.Sender?.ActorNumber ?? 0).FirstOrDefault();
                string payloadHash;
                try { payloadHash = Hash(Protocol.Serialize(__args.Where(a => !(a is PhotonMessageInfo)).ToArray())); }
                catch { payloadHash = "unavailable"; }
                var manager = ServiceRepository.GetService<INetworkingService>() as NetworkingManager;
                Write("rpc_received local_receive_index=" + (++receiveOrdinal) + " method=" + name + " sender=" + sender
                    + " host=" + PhotonNetwork.MasterClient?.ActorNumber + " local_tick=" + manager?.Tick.LocalTick
                    + " first_long_argument=" + (__args.Length > 0 && __args[0] is long ? __args[0].ToString() : "-")
                    + " args_sha256=" + payloadHash);
                // This is receipt/handler-entry logging, not proof of completed simulation execution.
                // The index is local, not an authoritative sequence shared by all peers.
            }
        }
        [HarmonyPatch(typeof(RulesetEntityManager), "CheckCurEntityGuidByTick")]
        private static class GuidCheckLog
        {
            private static readonly FieldInfo map = AccessTools.Field(typeof(RulesetEntityManager), "entityGuidByTick");
            private static void Prefix(RulesetEntityManager __instance, ulong localTick, ulong incomingGuid)
            {
                if (map?.GetValue(__instance) is Dictionary<ulong, ulong> values
                    && values.TryGetValue(localTick, out var other) && other != incomingGuid)
                    Write("ENTITY_GUID_DIVERGENCE tick=" + localTick + " previous=" + other + " incoming=" + incomingGuid);
            }
        }
    }
}
