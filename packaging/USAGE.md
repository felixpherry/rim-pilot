# CLI usage

This development package is read-only observation (milestone M1): authenticated
session discovery, health and capabilities, plus typed commands for summary, own
pawns, pawn detail, things, resources, UI state, public events and screenshots.
There are no gameplay actions or raw UI input commands. It is not verified M1
acceptance. Limited exact-release Unity Mono evidence exists on one copied fixture
(one map, four own pawns, no fog, invisibility, second map or DLC scenario); the
checkout's `tests/coverage/observation-adversarial.md` summarizes what was and was
not shown. Known gaps: `things.list` and `resources` returned the fixed `unavailable`
refusal on every loaded-map launch tested (the raw spawned-thing source exceeded its cap);
natural public events, scaled UI, capture-time and first-use cost budgets are unmet
or unverified. Unsupported/unverified capabilities are not empty game results.

From the package root on Linux x64:

```sh
./cli/rim-pilot sessions
./cli/rim-pilot health --session SESSION
./cli/rim-pilot capabilities --session SESSION
./cli/rim-pilot summary --session SESSION
```

Other typed commands (`pawns.list`, `pawn.get`, `things.list`, `resources`, `ui`,
`events.read`, `screenshot --output FILE.png`) take explicit scope identities from
`health` or `summary`; the checkout's `docs/observation-commands.md` has the exact
flags, pages, cursors and local PNG output rules.

Use the exact session identifier returned by `sessions`. More than one registry
record requires explicit choice, even if one appears unavailable. Each command
performs bounded private discovery and authenticates the exact expected session
on its connection. No automatic reconnect, retarget or uncertain stale-record
deletion. Credentials stay in private discovery records, never command arguments
or ordinary output. Game and CLI must use the same private runtime-directory
environment; setting up an isolated copy is separate from packaging.

Commands print JSON to stdout. Every command except `sessions` wraps a validated wire
response with `v`, authenticated `session` and `response`. `sessions` lists bounded
`session`/`status` entries. Exit 0 means a validated successful bootstrap command;
exit 64 means usage; exit 69 means discovery, connection or server failure. A
`ready` health state means scheduling checks passed, not safe baseline observation
availability. Loading can return `loading`, `not_ready` or an invalidated scope.
`capabilities` marks a route `available` when its audited producer is registered.
That does **not** mean the route works on a given game: `things.list` and `resources`
are reported available yet refused as `unavailable` on every loaded-map launch tested, and
capabilities cannot say so. Treat a refusal as unknown, never as an empty result.

Explicit-target install/reinstall/uninstall/recover commands exist in
`scripts/install.py` in the matching trusted source checkout; they are not
embedded in this package and do not install the CLI into PATH. See
`docs/package.md` for exact commands, ownership verification and interruption
recovery. Stop the authorized target game before changing mod files. Install
Harmony separately, then have a human enable Rim-pilot after Harmony and restart.
Disable through the Mods screen and quit before removal; restart after reinstall.
No command edits ModsConfig, Prefs, settings, saves or mod order, or launches/stops
a game. During development, test only authorized disposable copies, never a
personal running game. Keep package dependency notices with the retained CLI.
