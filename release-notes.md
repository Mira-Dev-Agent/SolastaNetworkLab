Solasta Network Lab 0.4.0 discovers the room host and connects clients without manual IP, port or session-key entry.

- Uses the current Photon room master as the sole direct host.
- Advertises detected IPv4 LAN/VPN endpoints and a generated session token through room properties.
- Connects clients in the background and requires acknowledgment from the expected host/session.
- Rebuilds setup automatically when room membership or the master changes before activation.
- Retains host activation once everyone is connected. MaxPlayers remains controlled by the game or Unfinished Business, with no fixed transport player-count cap.

Experimental: Photon is still required for lobby/control traffic; reconnect/resume is not implemented. The mod has not been validated in live Unity multiplayer. Use identical mod versions/settings and disposable test saves.

All participants must use 0.4.0. Host discovery does not bypass NAT/firewalls: use a reachable LAN/VPN route. No router/firewall settings are changed. Install the attached ZIP with Unity Mod Manager or accept the update prompt in 0.3.0, then close and relaunch Solasta. DiscoveryChecks.exe is a developer test harness and is not shipped in the package.

Validation includes the existing transport/updater suites and automatic-discovery socket tests for six/seven-player rosters, pre-activation host changes, stale/foreign advertisements and wrong-host rejection.
