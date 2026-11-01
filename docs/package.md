# Development package (Linux x64)

`make package` builds the configured net472 mod and self-contained CLI, then creates
`artifacts/package/rim-pilot-linux-x64/`. Both `RIMWORLD_DIR` and `HARMONY_MOD_DIR`
must be explicit absolute paths, as for `make build-game`. No game is launched.
An existing output path is refused, not overwritten; move or remove only your old
ignored build artifact before building another package.

This is a development artifact, not verified M1 acceptance. It contains the
authenticated bootstrap commands and the typed read-only observation commands
(summary, own pawns, pawn detail, things, resources, UI, public events,
screenshots); see [typed commands](observation-commands.md). Limited exact-release
Unity Mono evidence exists on one copied fixture, summarized with its unmet items in
the [phase-4 evidence summary](../tests/coverage/observation-adversarial.md#phase-4-real-game-evidence-summary):
`things.list` and `resources` refuse `unavailable` on every loaded-map launch
tested (they always refuse when the raw spawned-thing list is over 2,048 members), natural public events and several budgets are unmet or unverified, and
acceptance criteria 2, 3 and 5 remain unmet or unverified. Package verification
and installer runs are host and filesystem checks, not runtime compatibility
checks.

## Layout and dependency ownership

- `mod/RimPilot/`: RimWorld 1.6 About metadata, own assembly, pinned Newtonsoft.Json
  13.0.3 net45 assembly and its MIT license. `rimpilot.bridge` declares dependency
  on, and loads after, `brrainz.harmony`. Install Harmony separately; no second
  Harmony or game/Unity assembly is bundled. Version matches shared build version.
- `cli/rim-pilot`: self-contained Linux x64 executable. No SDK or runtime download
  on ordinary invocation. Host libc/ICU/native prerequisites still apply. .NET
  native libraries extract to its per-user bundle cache. CLI has no game linkage.
- `licenses/`: Newtonsoft.Json MIT notice and the exact bundled .NET runtime's
  license and third-party notices. Runtime version and notice hashes are pinned
  in `packaging/redistributables.json`; changing runtime requires reviewing both.
  These dependency notices do not grant a new license to project-owned code.
- `docs/`: this document and current CLI usage (`docs/usage.md`).
- `ownership.json`: versioned, exact file inventory with logical owner/component,
  SHA-256, byte length, Unix mode, and explicit owned directories. All entries
  belong to this package; dependency authors retain their copyrights/licenses.
  Manifest does not hash itself (recursive hashes are impossible); it is a
  separately reserved package-owned file. No timestamp, private path or credential
  is recorded. Hashes establish integrity against trusted inputs, not authenticity.

## Independent complete-package check

```sh
python3 scripts/project.py verify-package
# Or validate a copied/extracted directory against this checkout's trusted inputs:
python3 scripts/package.py verify --package /absolute/package/directory
```

Verification requires the local build outputs and private reference audit from
`make package`/`make build-game`, plus restored pinned runtime notices. It does not
trust the package's own inventory as an allowlist or provenance authority. It
walks the complete directory against a separate exact path/role/mode policy,
rejects extra or missing files/directories, traversal/absolute/duplicate manifest
paths, symlinked ancestors/files/directories, hardlinked files and special files,
checks hashes against trusted build/source inputs, validates version/dependency
metadata, and rejects installed-reference hashes anywhere in the package. A
renamed proprietary assembly cannot be legitimized by editing `ownership.json`.
Only the two named mod DLLs are allowed; embedded CLI contents derive from the
separately built, game-independent single-file CLI. This is not a generic malware
scanner or a hostile same-user sandbox; untrusted checkout/build tools invalidate
its trust anchors. No archives are extracted by these commands.

## Current use and installation boundary

Run `cli/rim-pilot sessions`, then `cli/rim-pilot health --session SESSION` or
`cli/rim-pilot capabilities --session SESSION`, then the typed observation commands; see `docs/usage.md` in the package.
Credentials remain private discovery data, not command-line arguments.

## Explicit-target mod install, reinstall and removal

Use `scripts/install.py` from the matching trusted source checkout; the installer
is repository tooling, not an executable embedded in this package. Install and
reinstall independently verify the complete package against that checkout's
build/source inputs and private reference audit described above. Uninstall and
recovery need no package, SDK, game references or running game. Python and Linux
filesystem support for no-replace rename, no-follow directory access, locking and
fsync are required; unsupported operations fail closed.

Stop the target game first. Do not change files concurrently. During development,
use only an authorized disposable copy, not a personal running installation.
Choose an existing, current-user-owned Mods directory with no group/other write
permissions. Every ancestor must be a real directory, not a symlink. The explicit
absolute target must end in `/RimPilot`; no game-directory discovery or default
installation target exists. For example, with your own authorized scratch paths:

```sh
python3 scripts/install.py install --package /absolute/package/directory \
  --target /absolute/disposable/Mods/RimPilot
python3 scripts/install.py reinstall --package /absolute/package/directory \
  --target /absolute/disposable/Mods/RimPilot
python3 scripts/install.py uninstall --target /absolute/disposable/Mods/RimPilot
```

Only the packaged `mod/RimPilot/` payload and its `.rim-pilot-owned.json` receipt
are installed. Run the CLI from `cli/rim-pilot` in the retained package; these
commands do not copy it into PATH or alter shell configuration. Keep the package
and its top-level redistribution notices together.

Install refuses any existing target, including an empty directory. Reinstall and
uninstall require the complete canonical receipt and verify every owned file's
SHA-256, length and mode against it, plus exact directories, current-user ownership
and single-link regular-file types. Unknown additions, missing/modified owned
files, modified receipt, links, special files, permissive modes and target/workspace
collisions cause refusal without deleting those objects. Keep unrelated files
outside this dedicated mod directory. An unknown addition inside it blocks the
operation rather than being silently preserved through a partial uninstall.
Hashes are integrity checks, not signatures: an actor who rewrites both receipt
and files can forge them. This is not a hostile same-user sandbox. Installer
operations serialize via an advisory lock on the existing parent directory;
ordinary editors and the game do not honor that lock.

### Interruption and explicit recovery

The installer first builds a private preparation directory beside the target,
then publishes a journal at `.RimPilot.rim-pilot-operation` without overwriting
anything. Existing owned trees move intact into this workspace before replacement
or removal. New trees publish with no-overwrite rename; commit precedes deletion
of old verified files. Files are removed individually, never by unowned recursive
deletion. Journal records carry fixed-path receipts and tree inode/device identities,
so even a same-content replacement directory cannot be adopted as the original.

If an operation fails or the process dies after journal publication, stop competing
writers and inspect the target/workspace. Resume **the recorded operation forward**:

```sh
python3 scripts/install.py recover --target /absolute/disposable/Mods/RimPilot
```

Recovery does not roll back or choose a different package. It verifies intact trees
before publication, and every surviving quarantined path/hash before resuming
partial cleanup. Unknown or changed state, missing ownership proof or a replacement
target blocks recovery and remains for human inspection. Do not fix this by blindly
removing the workspace, inventing receipts or deleting user files. A partially
quarantined operation may temporarily leave the mod absent; do not start the game
until recovery finishes. New operations refuse a published pending workspace.

Crashes before journal publication leave `.RimPilot.rim-pilot-prepare-*` directories
with the target unchanged. Crashes after detaching completed bookkeeping may leave
`.RimPilot.rim-pilot-completed-*`; the operation has already finished. These private
orphans are deliberately not auto-adopted or recursively scavenged. Retain for
inspection; remove manually only after proving they contain no unrelated data.
They do not block a new operation. Writes/renames use fsync ordering, but tests
establish host process-interruption recovery, not actual power-loss durability or
support for every filesystem.

### Human enable, disable and restart

The installer never reads or edits ModsConfig, Prefs, saves, mod order or player
settings, and never launches/stops a game. After installation, a human opens
RimWorld's Mods screen, enables Harmony and Rim-pilot with Harmony first, resolves
load order and restarts. Before removal, a human disables Rim-pilot through that
screen, restarts/quits as requested, then runs uninstall while the target game is
stopped. After reinstall, restart before using the changed assembly. No automatic
configuration edits or running-process detection substitute for these human steps.

Asset-free tests use synthetic package binaries and scratch targets, inject process
exits/filesystem failures across transaction boundaries, and compare unrelated
files plus copied save/ModsConfig/Prefs sentinels byte-for-byte. They do not prove
personal-install safety by touching personal files, or establish Unity Mono/game
acceptance. Separate isolated-menu evidence used the actual package and authorized
copied profile; further game validation must retain that isolation.
