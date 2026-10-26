# rim-pilot

A RimWorld mod and command-line interface for external AI agents.

The project is planned to support:

- **Coaching:** inspect player-visible game state and offer advice without changing the game.
- **Autonomous play:** control the game through normal player actions, from new-game menus to colony management, world travel and DLC systems.
- **Co-play:** play alongside a human using separate game clients with independent cameras and controls.

The initial target is **Linux and RimWorld 1.6**, including Royalty, Ideology, Biotech, Anomaly and Odyssey. Agents such as Pi or Claude own the reasoning loop; rim-pilot supplies observation and control rather than a built-in LLM.

## Status

Contracts and build scaffolding exist; runtime observation is not implemented. There is no installable observation bridge yet. The standalone CLI skeleton exits 64 with a not-implemented diagnostic.

See [build and test commands](docs/build.md) for asset-free checks and separately configured mod compilation. Compilation and pure contract tests are not real-game acceptance.

RimWorld and its DLCs are required separately. This repository does not include game binaries, assets or saves.
