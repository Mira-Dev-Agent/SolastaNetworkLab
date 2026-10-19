# Solasta Network Lab 0.5.0

All multiplayer connections and gameplay traffic use Solasta's normal Photon networking. Host and join through the game's existing multiplayer screens. There is no direct TCP listener, host-address discovery, LAN/VPN requirement, session-key entry or transport activation button. MaxPlayers remains controlled by the game or Unfinished Business.

This version retains passive synchronization diagnostics and the GitHub updater. **Host-authorized command sequencing is a recommendation, not an implemented enforcement feature.** No reduction in desyncs has been demonstrated, and live Unity multiplayer has not been validated.

## Installation

Download [version 0.5.0](https://github.com/Mira-Dev-Agent/SolastaNetworkLab/releases/tag/v0.5.0) and install the ZIP through Unity Mod Manager, or accept the update prompt in version 0.3.0 or newer. All participants should use the same release. Fully exit and relaunch Solasta after updating; changing versions inside a running session cannot undo patches from an older loaded DLL.

Create a fresh multiplayer room after upgrading from 0.4.0. Older rooms may retain unused discovery properties, but 0.5.0 neither reads them nor publishes host addresses/tokens. The old direct-connection setup is not needed.

The archive includes an inert `DirectTransport.dll` solely because earlier updaters require that filename. Its former socket/discovery source files are excluded from compilation, and the game mod no longer references that assembly. Old transport source and tests remain in the repository as historical experiments; they are not active runtime features. No test executable is shipped.

## Diagnostics

Logs are `network-<UTC timestamp>.log` in the installed mod directory. They record Photon disconnect causes and master changes, tick spread/waiting state, incoming RPC argument hashes and entity-GUID divergence. RPC records include the current host actor, local tick and a local receive index. That index is not a host-issued sequence, and receipt/handler entry is not proof that a delayed command finished executing. The first long argument is recorded without assuming every RPC uses it as a simulation tick.

Diagnostic patches only observe calls: they do not reject, reroute, reorder, pause or replace gameplay commands. The mod does not override room capacity or timeouts. Logs omit raw RPC payloads, saves, chat and credentials. The entity-GUID check is a limited divergence indicator, not a full-state checksum.

For enforcing host identity and execution order over Photon, see the [proposed protocol](https://github.com/Mira-Dev-Agent/SolastaNetworkLab/blob/main/docs/PHOTON_ORDERING.md). Its scope is executable action/combat-log actions; cursor updates and character movement retain their native Photon paths without additional host approval. The game already uses AllViaServer for many commands; that guarantee alone does not establish identical simulation state.

## Updates

The mod checks the public repository's latest stable release once at startup in the background. It shows the loaded DLL version and available version, and prompts at the menu when an update is available. Downloading requires a user click. Only a newer stable release with the expected ZIP and SHA-256 digest is offered. Drafts and prereleases are excluded. Failed checks leave gameplay available, with a release-page link and a manual retry after a one-minute cooldown.

On Windows, accepting the update downloads into the mod's `.updates` directory, verifies the asset size/digest, allowed file list and mod identity/version, then starts a hidden PowerShell helper. The helper waits for Solasta to exit, verifies again, backs up installed files and applies the update. It attempts rollback on failure. Relaunch the game afterward. Other platforms can install the release manually.

The helper records results in `.updates/install.log`; backups remain in `.updates/backup-<id>`. To restore a backup, close the game and copy its contents over the mod directory. Keep `ApplyUpdate.ps1` when installing. The checker sends only an unauthenticated HTTPS release request and updater User-Agent to GitHub, not account credentials or game data. Versions before 0.3.0 require a manual upgrade once.

## Build and checks

Use PowerShell and .NET SDK 8 or 9: `./build.ps1 -Restore -Test`. Override the reference installation with `-GameDir 'path'`. Local output is `artifacts/SolastaNetworkLab-0.5.0.zip`. CLI settings/reference packages stay inside the project, and the build restores its temporary environment afterward. Subsequent offline builds can omit `-Restore`.

The mod targets .NET Framework 4.7.2 and references the user's installed Solasta 1.5.97/UMM assemblies. Game, Unity, Photon, UMM and UB binaries and private decompilations are not published. The .NET 8 updater checks exercise version/release validation, package identity, wait-for-exit installation, backups, checksum refusal and rollback in disposable project-local folders.

`-Test` does not launch socket or discovery tests. Historical transport tests require `-LegacyTransportTests`; historical discovery tests require `-DiscoveryTests`. DiscoveryChecks.exe can remain blocked by the firewall. Neither program is part of the mod or necessary for Photon gameplay. They were not rerun for this release.

See [the investigation history](analysis/FINDINGS.md) for earlier experiments. Their direct-networking descriptions apply to old versions, not this release.
