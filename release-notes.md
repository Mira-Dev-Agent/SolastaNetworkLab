Solasta Network Lab 0.3.0 adds in-game version checking and one-click download/install-after-exit updates from this repository.

- Shows installed and latest published versions, with a menu prompt when a newer release is available.
- Verifies release asset size, SHA-256, package contents and mod identity before staging.
- Installs after Solasta closes, with backups and attempted rollback on failure (Windows).
- Retains direct client-to-host gameplay routing with no fixed transport player-count cap. MaxPlayers remains controlled by the game or Unfinished Business.

Experimental: Photon is still required for lobby/control traffic; reconnect/resume is not implemented. The mod has not been validated in live Unity multiplayer. Use identical mod versions/settings and disposable test saves.

Initial installation: install the attached ZIP with Unity Mod Manager and restart the game. Versions before 0.3.0 must be updated manually once to gain the updater.

Validation: builds against Solasta 1.5.97; 176 transport checks and 33 updater checks, including wait-for-exit installation and rollback fixtures.
