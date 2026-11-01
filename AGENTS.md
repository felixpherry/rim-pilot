# Project commands and boundaries

Run from repository root on Linux x64; Python 3.12+, GNU Make and normal .NET native prerequisites required. Use `scripts/dotnet`, never an assumed system SDK. SDK/dependencies download into the private user cache on first build.

| Role | Command | Meaning |
| --- | --- | --- |
| check | `make check` | Asset-free Release compilation of reference auditor, CLI and pure contract harness, plus Python repository/fixture tests. Warnings are errors. No mod/game compilation. |
| test | `make test` | Full asset-free Python/shared C# contract, transport, discovery, native-filesystem and lifecycle/dispatch suites; scratch installer/package tests, self-contained CLI publish, output allowlist and SDK-free usage/empty-registry smoke. No game/runtime acceptance. |
| build | `make build` | Publish self-contained Linux x64 CLI; verify explicit redistributable output allowlist. |
| configured mod build | `RIMWORLD_DIR=/absolute/game HARMONY_MOD_DIR=/absolute/harmony make build-game` | Fresh private metadata reference audit, actual net472 mod compile, forced-copy-local rejection and output boundary checks. Never launches game. |
| package | `RIMWORLD_DIR=/absolute/game HARMONY_MOD_DIR=/absolute/harmony make package` | Configured mod build plus CLI publish and complete development package with independent allowlist/hash validation. No install or game launch. |
| package check | `make verify-package` | Verify existing complete package against trusted local build/source inputs and private reference audit. |
| commit | `git commit` | Stage intended source only; match recent imperative commit style (for example, `Initialize ...`). |
| sync | `git merge main` | Merge local default main into current feature branch only. No push, rebase or main writes. Re-test after nontrivial merge. |

No separate lint/format role configured. Do not invent a successful linter run. `make test` always reports game tests not run; missing proprietary assets do not convert into a runtime pass. See [build details](docs/build.md).

## Scope and safety

Current code implements private bounded transport, process lifecycle/main-thread dispatch, typed bootstrap/observation CLI commands, source-audited baseline producers, bounded events/capture, development packaging and explicit-target install/removal. Observation adapters have host/compile evidence plus limited exact-release Mono evidence on one copied fixture (see the [phase-4 evidence summary](tests/coverage/observation-adversarial.md#phase-4-real-game-evidence-summary)); things/resources refusal above the raw-source cap, natural events, scaled/offscreen UI, capture-budget and first-use-cost divergences remain unmet or unverified. No action routes exist. CLI exit 64 means usage; bootstrap success is not observation readiness. Host tests and configured net472 compilation cannot establish Unity Mono compatibility or hidden-state safety. Do not write implementation claims into a Record before complete verification.

Installer commands require explicit absolute `/RimPilot` targets; see [installation/removal and recovery](docs/package.md). Tests use scratch targets only. Installer never edits ModsConfig, Prefs, saves or mod order; human enable/disable/restart remains separate.

Keep game/Unity/Harmony references and decompilations private, non-copy-local and outside public deliverables. `.local/`, caches and artifacts are ignored; never stage them. No game assets, saves, credentials or personal installation paths in tracked files.

Do not launch/control a personal game, send personal-display input, rewrite personal settings/mod order/saves or kill processes by name. Later game suites must use explicitly authorized isolated copies with owned-process cleanup. Build/check/test commands do not need game processes.
