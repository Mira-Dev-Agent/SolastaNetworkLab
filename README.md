# Solasta Network Lab 0.3.0

Experimental, partial Photon replacement for Solasta: Crown of the Magister 1.5.97. Remote clients connect directly by TCP to a server inside the host's game process. The host uses an in-process endpoint instead of a loopback socket. No separate server application is needed. Photon still supplies rooms, identities, properties, the initial PhotonView announcement and other control traffic. The existing Photon DLL is also used as a payload codec. **This is not Photon-free multiplayer, and no reduction in disconnects or desyncs has been demonstrated.**

There is no fixed player-count limit in the direct transport. Six-player sessions mean one host plus five remote players; the checks also exercise one host plus seven remote players. The game and party-size mods still determine playable slots. Set Unfinished Business's party size to six on every participant for a six-character campaign. The transport does not create extra characters or remove game/mod limits.

MaxPlayers is determined entirely by the game or Unfinished Business. This mod does not override room creation options or provide a room-cap toggle. It accepts the game's current lobby roster without adding a fixed numeric player limit of its own. Version 0.2.1 removes the room-cap override introduced in 0.2.0; recreate rooms previously created with that override to use your normal game/UB settings.

Built against the user's installed game and Unity Mod Manager, with Unfinished Business 1.5.97.127 present. No installed files were modified. The package contains only this project's two DLLs, metadata and this guide. Private decompilations and game binaries are excluded.

## Build and verified scope

Run `./build.ps1 -Restore -Test` using PowerShell and .NET SDK 8 or 9. Use `-GameDir 'path'` to override the installation path. Reference packages and CLI settings stay in this project. The script restores its process environment afterward. Subsequent offline builds can omit `-Restore`.

The mod compiles for .NET Framework 4.7.2; its transport targets .NET Standard 2.0. The socket checks run on .NET 8. The binary was compiled against the actual installed assembly, but has **not been loaded in Unity or tested in a live multiplayer game**.

The test executable checks the installed Photon codec with representative RPC payload types, fragmented framing, malformed/truncated/oversize messages, six-player and eight-player groups with 600 and 800 total commands respectively delivered identically to every participant, per-sender order, host/remote recipient filtering, idle heartbeats across the read timeout, invalid keys, reserved host identity, duplicate actors, activation barriers and session failure after peer loss. Host commands and remote commands enter the same ordering lock. A passing result does not prove Unity/Harmony runtime compatibility, operation over packet loss, recovery, or gameplay determinism.

## Install and baseline diagnostics

1. Back up a test save using the game's normal facilities. Give every participant the same game, mods and settings.
2. Download [the latest release](https://github.com/Mira-Dev-Agent/SolastaNetworkLab/releases/latest) and install `SolastaNetworkLab-0.3.0.zip` using your existing Unity Mod Manager installation, replacing the previous version. Use the same release on all participants. Local builds produce `artifacts/SolastaNetworkLab-0.3.0.zip`. The unchanged transport uses the version 0.2.0 wire handshake. Restart the game. To install manually, place the `SolastaNetworkLab` directory from the archive beneath the game's `Mods` directory.
3. Leave direct transport unconnected for the baseline. The mod logs Photon disconnect causes, tick spread, waiting state, incoming RPC names/ticks/argument hashes, and entity-GUID divergence. It does not change MaxPlayers, timeouts or synchronization buffers.
4. Logs are `network-<UTC timestamp>.log` inside the installed mod directory. They contain method names, hashes and numeric state, not raw RPC payloads, session keys, chat or save data.

## Update checks and automatic installation

Version 0.3.0 checks the public [Mira-Dev-Agent/SolastaNetworkLab](https://github.com/Mira-Dev-Agent/SolastaNetworkLab) latest published release once at startup, in the background. The mod panel displays the version of the loaded DLL and the available release. Only a numerically newer stable release with the expected ZIP asset and SHA-256 digest is offered. Drafts and prereleases are excluded. No account, token, save or multiplayer information is sent; GitHub receives a normal unauthenticated HTTPS request and the updater's User-Agent.

At the menu, a newer release prompts **Download update** or **Later**. The same download button and a manual version check are available in the mod panel. Downloading requires that click; checks do not silently install updates. Network/API failures leave the mod usable and offer a link to the releases page. Manual checks have a one-minute cooldown.

On Windows, accepting the update downloads into this mod's `.updates` directory, verifies the GitHub asset's size/SHA-256 and the ZIP's file list, mod ID and version, then starts a hidden PowerShell helper. The helper waits for the current Solasta process to exit, verifies the package again, backs up the installed files and applies the update. It attempts rollback on installation failure. Exit normally and relaunch Solasta afterward. All players should update before their next multiplayer session. Automatic installation is Windows-only; other platforms can use the release-page download.

The helper records its result in `.updates/install.log`; backups remain in `.updates/backup-<id>`. It replaces only this mod's packaged files, leaving logs and unrelated files alone. To roll back manually, close the game and copy a backup's contents over this mod's directory. Keep the entire package, including `ApplyUpdate.ps1`, when installing.

Versions before 0.3.0 do not contain this updater: install 0.3.0 manually once to receive future in-game prompts. The update checker and installer have automated fixture tests, including a real helper process waiting for a disposable process to exit, checksum refusal, and rollback after a locked-file failure. The Unity prompt itself has not been tested inside Solasta.

## Experimental direct gameplay route

1. Every player installs the same package and configures compatible Unfinished Business party size/settings. The host creates a Photon multiplayer room using the game/UB's normal MaxPlayers setting. All players join it. Stay in the lobby, before creating/loading a gameplay `Game` object. The host must be the Photon master client.
2. Open this mod's panel in Unity Mod Manager. The host generates a session key and shares it privately with the group. Use a fresh key for each session.
3. The host enters a local interface IP (default `0.0.0.0` listens on all interfaces) and a TCP port (default `27090`), then selects **Host direct transport**. `127.0.0.1` only permits remote sockets on the same machine. Other players enter the host's reachable LAN/VPN address, port and key and select **Join direct transport**. Allow the chosen TCP port through the host's firewall; direct Internet use also needs a reachable address and appropriate router forwarding. A VPN can provide a private reachable route. The host's own endpoint stays in process.
4. When the displayed player count equals the complete lobby roster (six for one host plus five clients), the host selects **Activate**. Every player waits until their panel says **Direct TCP gameplay transport active**, then the host starts or loads the disposable test campaign. The roster is checked and fingerprinted during connection; if someone joins/leaves during setup, cancel and reconnect everyone.
5. Do not change membership, rejoin, migrate host or switch transports during this run. The supported assembly hash is pinned; an unaudited assembly can use diagnostics but cannot activate direct mode.

This transport is for trusted LAN/VPN peers. The shared key is a plaintext admission check, not encryption or identity authentication. Anyone with it can claim an available expected actor ID. There is no NAT traversal, Steam relay integration, public server browser, TLS, reconnect/replay, snapshot recovery, file transfer replacement or host migration. TCP loss can stall later messages behind missing bytes. A socket failure, queue overflow, unexpected RPC mode, membership change or loss of Photon stops direct processing and requests a simulation pause; restart the game afterward. It never silently falls back to Photon gameplay.

The activation barrier fixes the expected actor set and sends a start marker to each remote socket before any data. This is a fixed session roster, not a numeric player cap. All gameplay envelopes pass through one sequencer, including those addressed to one player; each remote client verifies contiguous sequence numbers before applying recipient filtering. The local host receives from that same sequencer. RPC invocation and game access occur on the Unity main thread. The adapter retains the game's tick scheduling and constructs PhotonMessageInfo using the original Photon actor. Buffered RPC targets are delivered to the already-present recipients without replay caching, since late joining is disallowed. Secure/unexpected send overloads stop the session instead of mixing delivery routes.

## Is direct hosting better than Photon?

It may provide a shorter gameplay route when players can reach a nearby host, and lets the group control its gameplay endpoint. That is a hypothesis to measure against Photon, not a benchmark result. It does not remove dependence on Photon for lobby/control traffic in this version.

| Consideration | Direct host prototype | Existing Photon gameplay |
|---|---|---|
| Gameplay route | Each remote client connects to the host | Commands pass through the selected Photon server |
| Potential latency | Depends on every client's route to the host | Depends on routes to the selected server region |
| Reachability | Requires reachable host IP/port or VPN | Managed server connection |
| Host resources | Host uploads ordered traffic to all remote players | Photon server distributes gameplay RPCs |
| Recovery | No reconnect, snapshot recovery or host migration | Established SDK behavior; Solasta's integration controls recovery |
| Simulation desyncs | Existing simulation and mod logic remain | Same underlying game and mod logic |
| Six-player support | Transport tests pass; game test remains | Depends on room settings and compatible party-size mods |

Photon already offers reliable ordered delivery; swapping that property alone does not solve desyncs. TCP can stall subsequent messages behind packet loss, and the host becomes the gameplay distribution bottleneck. For the stated goal of fewer disconnects/desyncs, call this better only after comparable live sessions demonstrate it. See [Photon's disconnect analysis](https://doc.photonengine.com/pun/current/troubleshooting/analyzing-disconnects) and [RPC ordering](https://doc.photonengine.com/pun/current/gameplay/rpcsandraiseevent/).

## Compare the same failure

Use the same disposable save, players, mod settings and reproduction steps for one diagnostics-only run and one direct-route run. Record disconnect cause, time to failure, tick spread, waiting duration, the first entity-GUID divergence and nearby RPC hashes on every peer. Compare corresponding commands, accounting for RPCs deliberately addressed to only one player. Equal RPC hashes with diverging simulation state point toward game/mod determinism rather than transport corruption; an entity-GUID mismatch is a limited divergence signal, not a full-state checksum. The first host/client difference needs investigation before changing synchronization rules.

See [analysis/FINDINGS.md](analysis/FINDINGS.md) for the inspected architecture and remaining work. To remove the experiment, quit the game, remove/disable this mod in Unity Mod Manager and restart. No original networking DLL needs restoration.
