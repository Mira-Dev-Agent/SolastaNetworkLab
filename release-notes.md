Solasta Network Lab 0.5.0 restores normal Photon networking for all multiplayer connections and gameplay.

- Removes direct TCP setup, automatic address discovery and the activation flow.
- Leaves MaxPlayers, host/join behavior, command delivery and timeouts to the game/Unfinished Business/Photon.
- Retains passive RPC/tick/desync diagnostics and the in-game GitHub updater.
- Documents a proposed host-authorized sequencer over Photon. Enforcement is not implemented or claimed by this release.

Upgrade all participants and fully exit/relaunch Solasta. Create a new multiplayer room after upgrading from 0.4.0. No LAN/VPN or manual host details are required beyond normal Photon connectivity.

An inert DirectTransport.dll is kept only for compatibility with the older updater package contract; no direct-network code is compiled into it and the game mod does not reference it. No test executables are shipped. DiscoveryChecks.exe was not rerun.

Experimental diagnostics: live Unity/multiplayer validation and a demonstrated desync fix remain outstanding.
