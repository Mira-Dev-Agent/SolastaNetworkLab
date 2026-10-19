# Reverse engineering findings — 2026-10-20

Inspected with ILSpy CLI 9.1.0.7988 from the user's authorized game installation (`GameDir`). Source-like output remains in the ignored `analysis/decompiled` and `analysis/photon` folders. It is not packaged or published.

`Assembly-CSharp.dll` SHA-256: `A87E45CBD187E42E9D64A610850B39EDBA4896168B14D71CA71C5D32CF9E96EE`.

Installed Unfinished Business metadata reports game 1.5.97 and mod 1.5.97.127. Public mod source is in `research/UnfinishedBusiness`, commit `806836299a050d7b0183dc25152a1280dd8fc9c6`. The installed game binary is authoritative for this adapter. No game installation files were changed.

## Confirmed architecture

| Boundary | Observed behavior | Consequence |
|---|---|---|
| NetworkingManager | 186 PunRPC handlers, 185 view.RPC call sites, 172 PhotonNetwork references | A full replacement requires substantial integration |
| Gameplay delivery | Mostly AllViaServer; also Others, All, MasterClient | Preserve ordering and targeted delivery |
| Scheduler | FixedUpdate advances ticks, delayed commands, then deterministic commands | Invoke existing handlers and retain their scheduling |
| Timing | CommandTick = MaxTick + send buffer; default buffer 6, adaptive target 4–12 | Retain tick notifications and buffer logic |
| Waiting | NetworkTick slows/stops GameTime when actor ticks lag | Stalls can occur without permanent packet loss |
| Disconnect | OnConnected sets a 10,000 ms peer disconnect timeout; disconnect resets offline state | A longer timeout alone does not implement server retention or rejoin |
| Identity | PhotonMessageInfo.Sender reconciles local action callbacks | Preserve original actor IDs |
| Rooms | Photon properties carry assignments, readiness, settings, DLC and other state | Full removal needs room and property replication |
| Views | Cached event 10 distributes the allocated PhotonView ID | This prototype retains a Photon control dependency |
| Desync check | RulesetEntityManager compares entity GUID progression at matching ticks | A limited divergence signal, not a full-state checksum or repair |
| UB | Public patches adjust maximum players and character assignments | Match party size and mod configuration across peers |

AllViaServer already provides a common receiver order, consistent with [Photon's documentation](https://doc.photonengine.com/pun/current/gameplay/rpcsandraiseevent/). This experiment changes the gameplay route; it does not establish that Photon lacked ordering or reliability.

## Version 0.1 baseline

`src/DirectTransport` implements a bounded TCP relay with one global sequence, per-socket send queues, heartbeat/read deadlines, expected-actor admission and a start barrier. Loss after activation closes all peers. There is no automatic retry or replay that could duplicate actions.

`src/SolastaNetworkLab` supplies a UMM/Harmony adapter for the audited game assembly. Opt-in mode intercepts NetworkingManager's PhotonView sends, serializes method/arguments with the installed Photon codec, and invokes whitelisted handlers on the Unity main thread. Original tick scheduling remains intact. Diagnostics also work with ordinary Photon delivery. The adapter compiles against the actual installation with no warnings; live Unity/Harmony loading has not been tested.

28 checks passed: representative installed-Photon codec roundtrip, fragmented/malformed/truncated/oversize frames, four simultaneous peers with 400 total commands delivered identically to each, recipient filtering, idle heartbeats beyond the 15-second read deadline, invalid keys, duplicate actors, activation barriers and stop after peer loss. These are loopback tests, not evidence of a gameplay fix or WAN performance.

TCP is an inspectable baseline and may stall behind packet loss. For a later production backend, [LiteNetLib reliable ordered UDP](https://github.com/RevenantX/LiteNetLib) could retain the same sequencer. [SteamNetworkingSockets](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets) is another candidate, but access to Valve relay services through a mod must be verified. Owning the game does not establish backend access.

## Version 0.2 direct host changes

Remote clients connect to `HostServer` inside the host's game process. The host's `LocalPeer` enters the same sequencing lock and receives from an in-process queue, removing the host's extra TCP loopback connection. There is no fixed four-, six- or eight-player admission maximum, listener backlog override or pending-connection count cutoff. Message and queue bounds remain. The fixed session roster still rejects unregistered actors and late joins; this is session consistency, not a numeric capacity setting.

The adapter fingerprints the lobby roster and checks it before activation and during updates. The handshake identifies version 0.2.0. Online room creation sets MaxPlayers to zero by default, which the [Photon SDK documents as no player-count limit](https://doc-api.photonengine.com/en/dotnet/current/class_photon_1_1_realtime_1_1_room_options.html). This runs after UB's game-level room-info patch and does not alter party size. Recreate old rooms to apply it. Service and game/mod constraints still apply.

Readiness paths can select buffered RPC targets at runtime. Version 0.1 rejected those modes. Version 0.2 delivers them to the already-present recipients without caching because late joining is disallowed. This supplies live readiness delivery without claiming buffered replay support.

Current checks exercise six players (host plus five clients, 600 commands) and eight players (host plus seven clients, 800 commands). Every participant sees identical total order and per-sender order. Coverage includes local-host targeting, reserved host identity, late-join rejection without disrupting the session, host-only transport, idle heartbeat, framing, invalid/duplicate admission and peer-loss shutdown. These checks do not run the Solasta simulation or Harmony inside Unity. Six-player gameplay and fewer disconnects/desyncs remain unverified.

A nearby reachable host might shorten the gameplay path. Costs include host upload/CPU load, reachability requirements and TCP stalls on loss. Existing simulation bugs can still cause desyncs, and Photon control/lobby outages can still end the game. This is not yet demonstrated to be more reliable than Photon or independent of Photon Cloud.

## Version 0.2.1 correction

Removed the room-creation Harmony patch and its GUI toggle. MaxPlayers is now determined solely by the game or Unfinished Business. The direct transport is unchanged and retains no fixed numeric player-count cap; it admits the game's actual roster. The 0.2.0 wire handshake is retained because the wire protocol did not change. Recreate a room created under 0.2.0's override to restore its normal game/UB capacity setting.

## Version 0.3.0 updater and publication

The updater reads the loaded assembly's version and compares it numerically with the repository's latest published stable GitHub release. Checks and downloads run in background tasks. Update prompts are deferred until menu/setup context, and downloading requires a user click. GitHub's asset size/digest, ZIP file allowlist and package ID/version are validated before staging. A hidden Windows helper waits for the game process to exit, verifies the package again, backs up files, applies the update and attempts rollback on failure. There is no live DLL replacement, automatic game restart or credential embedded in the mod.

33 fixture checks cover version ordering, equal/newer installed versions, draft/prerelease rejection, release/asset origin and digest validation, package contents/identity, wait-for-exit installation, backup, checksum refusal and rollback after a locked-file failure. The transport suite remains separate. Unity UI integration and actual multiplayer gameplay remain unverified.

## Version 0.4.0 automatic room-host discovery

Removed manual address, port and session-key entry. Setup automatically derives the host from PhotonNetwork.MasterClient, publishes IPv4 interface candidates and a generated token in room custom properties, and connects clients in background tasks. Advertisements are bound to room name, master actor, game hash, mod version, roster fingerprint and a fresh session epoch. These properties are not added to the lobby listing. A new welcome acknowledgment confirms the expected master/session before accepting a socket, and the wire handshake version advances to 0.4.0.

Before activation, roster or master changes rebuild the host listener and client connections automatically. After activation, the existing fixed-roster failure behavior remains; this is not gameplay host migration or reconnect. Every player still joins the Photon room, and the host still activates direct gameplay once all players connect. MaxPlayers is unchanged.

Discovery only advertises locally assigned IPv4 addresses, including LAN/VPN interfaces and loopback for same-machine instances. It performs no external-IP lookup, UPnP, NAT traversal or firewall modification. A reachable LAN/VPN route is required. Room members see the addresses/token, so this is for trusted peers rather than strong identity authentication.

32 additional automated discovery checks cover metadata validation, six-player automatic connections, expansion to seven players, master changes before activation, matching command order after discovery, wrong-host rejection and room-exit cleanup. The developer test executable is not included in the mod archive. Live Photon-room/UI operation and live gameplay remain unverified.

## Version 0.5.0 restores Photon-only gameplay

Removed all runtime direct transport, host advertisements, activation controls, RPC-send interception and direct-session freeze/rejection patches. The game mod no longer references the DirectTransport assembly. A compatibility DLL with that filename is still packaged for old updaters, but its socket/discovery source files are excluded from compilation. Historical source/tests remain inactive in the repository.

Runtime Harmony patches only log RPC receipt, Photon disconnect/master-change callbacks and entity-GUID divergence. Diagnostics do not authorize, reject, reorder, reroute or execute gameplay commands. Local receive indices are explicitly not authoritative sequence numbers or proof of completed execution. Normal Photon host/join, capacity and command delivery are preserved.

The recommended next design is documented in docs/PHOTON_ORDERING.md: Photon-authenticated host identity plus session epochs, player proposals and host-issued sequenced commits, execution-tick barriers, execution acknowledgments and canonical checkpoint hashes. It is not implemented by this release. Direct/discovery networking tests are opt-in historical tests and were not rerun after the user blocked DiscoveryChecks.exe.

## Remaining work

1. Test the package on two actual game clients: Harmony loading, lobby activation, start/load, exploration, combat, reactions, inventory, travel and save/reload on disposable campaigns.
2. Reproduce the reported disconnect/desync and compare baseline versus direct logs. A reproducible save and specific failure sequence have not yet been supplied; no root cause is established.
3. Validate latency, loss, jitter, bandwidth limits, background stalls, abrupt process death and malformed peers. Current tests do not cover these conditions.
4. Complete Photon removal by implementing room discovery/lifecycle, properties, identities, view creation and other SDK-dependent systems. Audit save/character synchronization, platform invites and serialization separately.
5. Design safe recovery around an authoritative snapshot, content fingerprint, committed command watermark and deduplicated replay. Socket reconnect alone is insufficient.
6. If identical commands still diverge, investigate game/mod random state, entity creation order, coroutine scheduling and settings/content mismatches. Add canonical state checksums before attempting automatic repair.
