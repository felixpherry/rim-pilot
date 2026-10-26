# Build foundation (Linux x64)

This is a compilation skeleton with shared [wire v1 contracts](../protocol/wire-v1.md), not an operational observation bridge. The mod has no patches or listener; the CLI exits 64 with an explicit not-implemented diagnostic. Installation, packaging and runtime game proof belong to later phases. Repository-owned commands below cover this foundation only.

## Pinned private SDK

Requirements: Linux x64, Python 3.12+ (including safe tar extraction filters), GNU Make, network access for the first SDK/runtime-pack download, and normal .NET Linux native dependencies (including ICU).

Run all commands from the repository root:

```sh
./scripts/dotnet --info
```

`global.json` disables SDK roll-forward. `scripts/sdk.lock.json` pins .NET SDK **10.0.403**, its official download URL and SHA-512 from Microsoft's .NET 10 release metadata. .NET 10 is LTS, active when selected, supported through 2028-11-14. The bootstrap verifies the archive before extraction and checks the installed SDK version on reuse. It refuses an unverified existing SDK directory or a mismatched archive rather than overwriting it. A checksum failure requires explicit removal of the bad cached archive before retrying.

SDK, NuGet and CLI-home caches live under `${XDG_CACHE_HOME:-$HOME/.cache}/rim-pilot/`; no system installation or PATH edits occur. Ordinary published CLI use requires neither SDK nor runtime download. The pin targets Linux x64 only; other platforms fail explicitly. `scripts/dotnet` forwards arguments to that exact local SDK.

## Asset-free compilation

```sh
make check  # Release-build auditor, CLI and contract harness; run Python guards
make test   # Full pure Python/C# suites; publish and SDK-free CLI skeleton smoke
make build  # Publish self-contained CLI only; check redistributable output allowlist
```

These commands never discover or compile against game assets, even when a private reference file already exists. `make check` treats C# warnings as errors. `make test` runs the shared strict-ingress, wire and observation-safety harness, schema/fixture/inventory/build guards, then launches the published CLI with SDK/shared-runtime lookup removed. Its expected exit **64** and not-implemented diagnostic prove only that the self-contained skeleton starts, not that an observation succeeds. Game tests are explicitly reported **NOT RUN**. No separate lint/format command is configured; command roles are defined in [AGENTS.md](../AGENTS.md).

Public CI (`.github/workflows/check.yml`) runs `make check` and `make test` without proprietary assets. It neither runs the mod nor uploads local reference configuration or build outputs. Dependency restores are locked to committed package versions/content hashes. Commands fail on compiler/test/output-boundary errors; they never count missing game assets as game coverage.

The CLI targets `net10.0`, bundles the Linux x64 runtime and native runtime libraries into a single executable, and does not link the mod or game. Bundled native libraries extract into .NET's per-user bundle cache at execution; `DOTNET_BUNDLE_EXTRACT_BASE_DIR` may select a private alternative. This is self-contained .NET deployment, not a statically linked executable: host libc/ICU/native prerequisites still apply.

## Explicit installed-game reference audit

Provide your own installed RimWorld 1.6 and Harmony mod roots; do not copy assemblies into the repository or download game assemblies from NuGet:

```sh
RIMWORLD_DIR=/absolute/path/to/RimWorld \
HARMONY_MOD_DIR=/absolute/path/to/Harmony make build-game
```

Both paths must be explicitly configured and absolute. `build-game` runs a fresh reference audit, compiles the actual net472 target, verifies its explicit output allowlist and rejects any installed-reference content hashes in mod output (also CLI output if present). It then tests a deliberately copy-local private reference configuration: the mod build must fail with `Game/Harmony dependencies must never be copy-local.` before files copy. Temporary configuration is removed, and output is checked again. No game process starts; this is a configured compilation test, not Unity Mono execution or a complete-package pass.

For a custom private reference file, invoke the lower-level tools explicitly:

```sh
./scripts/dotnet run --project tools/ReferenceAudit -c Release -- \
  "$RIMWORLD_DIR" "$HARMONY_MOD_DIR" /absolute/private/game-references.props
./scripts/dotnet build mod/RimPilot.Mod.csproj -c Release \
  -p:GameReferencesFile=/absolute/private/game-references.props
```

`ReferenceAudit` reads PE metadata without loading assemblies or running game code. It checks:

- Installed `Assembly-CSharp` references the actual `mscorlib` 4.0 and `netstandard` 2.1 identities, including public-key tokens. The facade and Harmony must reference installed `mscorlib`.
- Harmony declares package ID `brrainz.harmony`; its `LoadFolders.xml` selects exactly one `0Harmony.dll` for `v1.6`. Conditional/unknown declarations or ambiguous candidates fail closed. Folder names and actual assembly identity are discovered, not inferred from mod metadata or a root `Assemblies/` assumption.
- Every transitive assembly reference from the compile roots resolves to installed managed files (or selected Harmony), with matching name/token and a sufficient version. Version unifications are printed and retained in the private report; they are **not runtime-binding proof**.
- Only explicit installed references enter the generated MSBuild file, all with `Private=false`. No modern SDK framework references or downloaded desktop reference packs substitute for the game's Mono closure.

The generated `.local/game-references.props` and adjacent `.audit.json` contain private absolute paths, identities and hashes. They are ignored and must never be published. A different location can be selected with `-p:GameReferencesFile=/absolute/private/file.props` when building the mod. Re-run the audit after game/Harmony updates. Missing configured references fail with instructions instead of silently building an incompatible target.

The mod uses `net472` compatibility attributes and language/runtime constraints, but its actual reference assemblies are the installed Mono assemblies (including the game's 2.1 facade), not a claim that desktop .NET Framework implements netstandard 2.1. Compilation does not prove Unity Mono loading, patch compatibility or observation safety. Those require the later isolated real-game suite.

## Output boundary

The mod's own assembly/PDB appear in `mod/bin/Release/net472/`, alongside the pinned redistributable Newtonsoft.Json 13.0.3 net45 dependency, its XML documentation and MIT notice. See [JSON ingress](json-ingress.md) for dependency selection, strict parsing limits, explicit DTO mapping and deferred Mono runtime proof. Game, Unity and Harmony assemblies are runtime-provided and non-copy-local. A build target rejects game-reference copy-local items before the copy phase; neither game binaries nor a second Harmony belong in deliverables.

CLI output under `artifacts/cli/linux-x64/` contains the self-contained CLI (including bundled JSON dependency), its PDB and external JSON license notice; dependency XML documentation may also appear. The notice is explicitly excluded from the single-file bundle so repeated publication preserves a readable redistribution notice. `make test` publishes twice to check that boundary. Build caches, private reference configuration and downloaded dependencies stay ignored. No game launch, installation, personal profile/settings update or save access is part of these commands. Later packaging must validate the complete package independently; these foundation checks do not claim an installable M1 pass.
