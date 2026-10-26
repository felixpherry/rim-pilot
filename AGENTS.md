# Project commands and boundaries

Run from repository root on Linux x64; Python 3.12+, GNU Make and normal .NET native prerequisites required. Use `scripts/dotnet`, never an assumed system SDK. SDK/dependencies download into the private user cache on first build.

| Role | Command | Meaning |
| --- | --- | --- |
| check | `make check` | Asset-free Release compilation of reference auditor, CLI and pure contract harness, plus Python repository/fixture tests. Warnings are errors. No mod/game compilation. |
| test | `make test` | Full asset-free Python and shared C# ingress/wire/safety suites, self-contained CLI publish, output allowlist and SDK-free skeleton launch smoke. No game/runtime acceptance. |
| build | `make build` | Publish self-contained Linux x64 CLI; verify explicit redistributable output allowlist. |
| configured mod build | `RIMWORLD_DIR=/absolute/game HARMONY_MOD_DIR=/absolute/harmony make build-game` | Fresh private metadata reference audit, actual net472 mod compile, forced-copy-local rejection and output boundary checks. Never launches game. |
| commit | `git commit` | Stage intended source only; match recent imperative commit style (for example, `Initialize ...`). |
| sync | `git merge main` | Merge local default main into current feature branch only. No push, rebase or main writes. Re-test after nontrivial merge. |

No separate lint/format role configured. Do not invent a successful linter run. `make test` always reports game tests not run; missing proprietary assets do not convert into a runtime pass. See [build details](docs/build.md).

## Scope and safety

Current code is a contract/build skeleton: no listener, runtime observation hooks, installer or action routes. CLI exit 64 is intentional, not an observation success. Contract fixtures and reference metadata cannot establish Unity Mono compatibility or hidden-state safety. Do not write implementation claims into a Record before complete verification.

Keep game/Unity/Harmony references and decompilations private, non-copy-local and outside public deliverables. `.local/`, caches and artifacts are ignored; never stage them. No game assets, saves, credentials or personal installation paths in tracked files.

Do not launch/control a personal game, send personal-display input, rewrite personal settings/mod order/saves or kill processes by name. Later game suites must use explicitly authorized isolated copies with owned-process cleanup. Build/check/test commands do not need game processes.
