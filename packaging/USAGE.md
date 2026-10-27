# Bootstrap CLI usage

This development package implements authenticated session discovery, health and
capabilities, not baseline observations. Unity Mono and real-game behavior remain
unverified. No screenshots, event reads, gameplay actions or raw UI input commands
are available. Unsupported/unverified capabilities are not empty game results.

From the package root on Linux x64:

```sh
./cli/rim-pilot sessions
./cli/rim-pilot health --session SESSION
./cli/rim-pilot capabilities --session SESSION
```

Use the exact session identifier returned by `sessions`. More than one registry
record requires explicit choice, even if one appears unavailable. Each command
performs bounded private discovery and authenticates the exact expected session
on its connection. No automatic reconnect, retarget or uncertain stale-record
deletion. Credentials stay in private discovery records, never command arguments
or ordinary output. Game and CLI must use the same private runtime-directory
environment; setting up an isolated copy is separate from packaging.

Commands print JSON to stdout. `health` and `capabilities` wrap a validated wire
response with `v`, authenticated `session` and `response`. `sessions` lists bounded
`session`/`status` entries. Exit 0 means a validated successful bootstrap command;
exit 64 means usage; exit 69 means discovery, connection or server failure. A
`ready` health state means scheduling checks passed, not safe baseline observation
availability. Loading can return `loading`, `not_ready` or an invalidated scope.
Only `auth`, `health` and `capabilities` are advertised available.

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
