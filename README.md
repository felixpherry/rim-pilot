# rim-pilot

A RimWorld mod and command-line interface for external AI agents.

The project is planned to support:

- **Coaching:** inspect player-visible game state and offer advice without changing the game.
- **Autonomous play:** control the game through normal player actions, from new-game menus to colony management, world travel and DLC systems.
- **Co-play:** play alongside a human using separate game clients with independent cameras and controls.

The initial target is **Linux and RimWorld 1.6**, including Royalty, Ideology, Biotech, Anomaly and Odyssey. Agents such as Pi or Claude own the reasoning loop; rim-pilot supplies observation and control rather than a built-in LLM.

## Status

Development mod/CLI packaging and explicit-target install/removal exist. Bounded private transport, main-thread lifecycle/dispatch and CLI `sessions`, `health`, `capabilities` are implemented. A separately authorized isolated full-profile Unity Mono menu run proved packaged session discovery, authentication, health and capabilities. Baseline game observations, events and screenshots remain unavailable; pause/live/load/quit behavior and observation safety remain unproven. No gameplay or raw UI input routes exist.

See [build and test commands](docs/build.md), [package/install/removal instructions](docs/package.md) and [bootstrap CLI commands](docs/bootstrap-commands.md). Asset-free checks and configured net472 compilation are separate from real-game acceptance. CLI exit 64 means usage, not an observation result; bootstrap success does not establish baseline readiness.

RimWorld and its DLCs are required separately. This repository does not include game binaries, assets or saves.
