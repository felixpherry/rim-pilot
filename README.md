# rim-pilot

A RimWorld mod and command-line interface for external AI agents.

The project is planned to support:

- **Coaching:** inspect player-visible game state and offer advice without changing the game.
- **Autonomous play:** control the game through normal player actions, from new-game menus to colony management, world travel and DLC systems.
- **Co-play:** play alongside a human using separate game clients with independent cameras and controls.

The initial target is **Linux and RimWorld 1.6**, including Royalty, Ideology, Biotech, Anomaly and Odyssey. Agents such as Pi or Claude own the reasoning loop; rim-pilot supplies observation and control rather than a built-in LLM.

## Status

**M1 ("observe safely") is observation only and is not accepted.** The planned coaching, autonomous play and co-play above are planned, not built. There are no gameplay actions and no raw UI input routes.

- **What exists.** A development mod and a self-contained CLI, with packaging and explicit-target install and removal. Authenticated local sessions, and typed read-only commands for sessions, health, capabilities, summary, own pawns, pawn detail, things, resources, UI state, public events and screenshots.
- **What was shown.** On one copied fixture, with the full solo mod profile, in an isolated game instance, the evidence package (build D, below) was run at the main menu, paused and live: summary, own pawns, pawn detail, UI, events and decoded screenshots, plus lifecycle invalidation, graceful quit, forbidden-method denials and developer-mode refusal. Host tests and net472 compilation are separate and are not game proof.
- **Acceptance criteria 2, 3 and 5 are unmet or unverified.** Things and resources refuse `unavailable` on every loaded map tried, while capabilities reports them available. Natural public events, hidden-state, off-camera, fog, second-map and DLC cases were not shown. Screenshot, first-use and bounded-load time budgets are over. Criteria 1, 4 and 6 have supporting evidence, but no final verification pass is claimed here. See the [evidence summary](tests/coverage/observation-adversarial.md#phase-4-real-game-evidence-summary).
- **Delivered package (build E, what `make package` builds at this commit).** Mod assembly SHA-256 `311b9be26167355c0c0a0740119852d08dff34c29ef9ccc463cdb3c23494a89f`, CLI `14f76bdf4285909bb97e2a8c98d99da838d0af5e8f4c1359bc3293468039c2cd`, package tree hash `5d899eadba216e652869ed8ba41922b43f41c5b1582af4b37a744634c6011356`. **The real-game evidence above ran on the earlier build D**, mod `2a98517af6f468d10af97e5d19262d9b07dbf8c34784e27e3aaca1d3738b6366`, CLI `8120201c6e028e5b56f7d0df02a2c151ffbeb59487cea9253328b206d9ded6e6`, tree `603fd5ef8d2ed94934ad9b67932dbcbd1df3a41a72a6f09115c2f13fb1a64d1e`. E differs from D by a small fix round (event cursors now expire after 5 seconds unused instead of 5 seconds from creation, corrected mod description and docs, one unused constant removed); those changes have host-test evidence only and no real-game rerun. The build does not depend on the commit or checkout path; see the [artifact binding](tests/coverage/observation-adversarial.md#artifact-binding).
- **Operation inventory.** Of 110 rows, five narrow rows are marked implemented from this evidence; all others are unverified, including every DLC. See the [inventory](tests/coverage/operations.md).

See [build and test commands](docs/build.md), [package/install/removal instructions](docs/package.md) [bootstrap CLI commands](docs/bootstrap-commands.md), [observation commands](docs/observation-commands.md) and [observation evidence boundaries](tests/coverage/observation-adversarial.md). Asset-free checks and configured net472 compilation are separate from real-game acceptance. CLI exit 64 means usage, not an observation result; bootstrap success does not establish baseline readiness.

RimWorld and its DLCs are required separately. This repository does not include game binaries, assets or saves.
