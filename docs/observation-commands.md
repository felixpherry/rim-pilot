# Typed observation commands

CLI uses the private registry and exact-session authentication described in
[bootstrap commands](bootstrap-commands.md). Choose `--session SESSION` explicitly
when multiple records exist. No command reconnects, retries, silently selects a
replacement, or accepts arbitrary methods/JSON parameters.

These commands are implemented; host tests do **not** establish actual Mono,
normal-player UI equivalence, full-profile availability or runtime acceptance.
Producers can refuse unavailable/unaudited data. Inspect capabilities and errors;
missing fields are not empty results. No actions or raw input exist.

## Scope and reads

`health --session SESSION` supplies optional authorized `data.scope` with root/game
identities. `summary --session SESSION` bootstraps that scope on the **same socket**
then reads summary. UI, events and screenshots can bootstrap identically. Unsafe
or not-ready health without scope cannot bootstrap observations.

To preserve a previously sampled scope, pass `--root ROOT`, optional `--game GAME`,
and optional `--map MAP`. Map requires game; game/map require explicit root.
Explicit stale scope never falls back to health. Map-scoped commands require all
three identities. Obtain eligible map IDs from summary; never substitute internal
game numeric IDs. Returned scope must exactly match requested scope, including map.

```sh
rim-pilot sessions
rim-pilot health --session SESSION
rim-pilot summary --session SESSION
rim-pilot summary --session SESSION --root ROOT --game GAME
rim-pilot pawns.list --session SESSION --root ROOT --game GAME --map MAP --limit 16
rim-pilot pawn.get --session SESSION --root ROOT --game GAME --map MAP --pawn PAWN
rim-pilot things.list --session SESSION --root ROOT --game GAME --map MAP --limit 16
rim-pilot resources --session SESSION --root ROOT --game GAME --map MAP
rim-pilot ui --session SESSION --root ROOT --game GAME
rim-pilot events.read --session SESSION --root ROOT --game GAME --limit 16
rim-pilot screenshot --session SESSION --root ROOT --game GAME --output ./capture.png
```

Successful reads print `{v,session,response}` with validated wire response.
`pawn.get` also checks returned pawn identity. Exit 0 means command success,
64 invalid usage, 69 session/observation/output failure. Valid server errors print
the validated error envelope and fixed stderr code. Rejected/malformed responses
produce no state output. Credentials, rejected values and private paths are never
included in diagnostics.

## Pages and events

`pawns.list`, `things.list` and `events.read` accept `--limit 1..64` (default 64)
and optional `--cursor CURSOR`. Pawn/thing cursor kind is `q_…`; event kind is
`e_…`. Pass response `next` unchanged with the same method, scope and limit.
CLI checks response item count against requested limit, typed cursor shape,
page completeness and exact scope. Cursor ownership/query/lifetime validation
remains server-side; cursors carry no raw offsets. No automatic pagination or
cursor reset conceals expiry/gaps. Handle `cursor_expired`, `event_gap` and
`stale_scope` explicitly by resynchronizing; do not treat them as empty pages.

## Screenshots and local files

`--output` is mandatory for screenshot and forbidden elsewhere. Only CLI sees
that path: wire request contains scope alone. Screenshot selects the larger
bounded response profile only for that method. CLI validates declared bytes,
canonical base64, PNG signature/chunk CRCs/order/exact EOF, IHDR dimensions,
noninterlaced RGB/RGBA8 format, zlib framing/Adler checksum, complete DEFLATE
stream and exact bounded scanline/filter content before creating any output.
Unknown chunks/formats fail closed; current product emits plain RGB8 PNG.

CLI stages mode-0600 bytes in destination directory, flushes, then publishes
without overwrite. Existing files, directories and symlinks are refused; failure
removes only owned stage. Parent directory must already exist. Success JSON keeps
capture metadata but omits `chunks`; complete PNG resides at requested local path.
Pixels/orientation/render provenance and native timing still require isolated
actual-game proof; structurally valid PNG is not proof of a useful safe frame.

## Install, removal and recovery

Short form only; [package, install and recovery](package.md) has the exact commands and
refusals. Build the package from a trusted checkout, then, with the game **stopped** and an
explicit authorized `.../RimPilot` target, run `scripts/install.py install`, later
`reinstall` or `uninstall`. If a run dies midway, `recover` resumes the recorded operation
forward; it never rolls back and never deletes unknown files.

- **The installer touches only its own mod folder.** It never edits mod order, preferences,
  saves or settings and never starts or stops a game.
- **A human turns the mod on and off.** Enable Harmony first, then Rim-pilot, in the game's
  Mods screen and restart. Disable it and quit before removal.
- **The CLI is not installed.** Run it from the retained package (`cli/rim-pilot`). It needs
  the same private runtime directory as the game.
- **Installer runs are host checks.** They show file handling on scratch targets, not game
  behavior.

## Example agent session (synthetic)

Shapes only. Identifiers are made-up repeated digits, labels are invented, nothing here came
from a real game, and the same fields are checked against the
[wire contract](../protocol/wire-v1.md). Real output is one compact line per command; lines
are wrapped here for reading. In a real session copy the identifiers from the previous
reply.

**1. Find the session.** `rim-pilot sessions` lists the live records. Pass the exact
session to everything after.

```json
{"v":1,"sessions":[{"v":1,"session":"s_11111111111111111111111111111111","status":"live"}]}
```

**2. Check health.** `ready` means scheduling checks passed, not that observation works.
The `scope` is what later commands need. A loading game returns `loading` and no scope; wait
and ask again, the CLI never retries for you.

```json
{"v":1,"session":"s_11111111111111111111111111111111",
 "response":{"v":1,"id":"health","ok":true,"data":{"state":"ready",
  "scope":{"session":"s_11111111111111111111111111111111","root":"r_22222222222222222222222222222222",
           "game":"g_33333333333333333333333333333333"}}}}
```

**3. Read the summary.** `rim-pilot summary --session SESSION` reuses the same socket and
scope, then returns the loaded maps. Pick a map identifier here; never invent one.

```json
{"v":1,"session":"s_11111111111111111111111111111111",
 "response":{"v":1,"id":"summary","ok":true,"data":{
  "sample":{"scope":{"session":"s_11111111111111111111111111111111","root":"r_22222222222222222222222222222222",
                     "game":"g_33333333333333333333333333333333"},
            "capture":"c_88888888888888888888888888888888","frame":20,"phase":"root_update_postfix","tick":4800},
  "scene":"playing","build":"example-build","paused":true,
  "maps":[{"map":"m_44444444444444444444444444444444","label":"Example Colony","width":250,"height":250,"eligibleOwnPawns":3}]}}}
```

**4. List your pawns, two at a time.** Add the map to the scope. The reply is incomplete, so
`next` is present. Pass it back unchanged with the same method, scope and limit.

```sh
rim-pilot pawns.list --session SESSION --root ROOT --game GAME --map MAP --limit 2
rim-pilot pawns.list --session SESSION --root ROOT --game GAME --map MAP --limit 2 --cursor NEXT
```

```json
{"v":1,"session":"s_11111111111111111111111111111111",
 "response":{"v":1,"id":"pawns","ok":true,"data":{
  "sample":{"scope":{"session":"s_11111111111111111111111111111111","root":"r_22222222222222222222222222222222",
                     "game":"g_33333333333333333333333333333333","map":"m_44444444444444444444444444444444"},
            "capture":"c_88888888888888888888888888888888","frame":21,"phase":"root_update_postfix","tick":4800},
  "snapshot":"q_66666666666666666666666666666666","basis":"eligible_observed_snapshot",
  "items":[{"pawn":"p_55555555555555555555555555555555","label":"Pawn A"},
           {"pawn":"p_555555555555555555555555555555aa","label":"Pawn B"}],
  "complete":false,"next":"q_99999999999999999999999999999999"}}}
```

The last page has `complete:true` and no `next`. `complete` means the end of that frozen
list, not the whole game. There is no total.

**5. Read one pawn.** Needs, health rows, gear and the current job appear only when the
game's own cards show them. An animal has no gear key; absent means not offered, not empty.

```json
{"v":1,"session":"s_11111111111111111111111111111111",
 "response":{"v":1,"id":"pawn","ok":true,"data":{
  "sample":{"scope":{"session":"s_11111111111111111111111111111111","root":"r_22222222222222222222222222222222",
                     "game":"g_33333333333333333333333333333333","map":"m_44444444444444444444444444444444"},
            "capture":"c_88888888888888888888888888888888","frame":22,"phase":"root_update_postfix","tick":4800},
  "pawn":{"pawn":"p_55555555555555555555555555555555","label":"Pawn A","status":["awake"],
          "needs":[{"label":"Food","level":0.5}],"health":[{"label":"Bruise","part":"Left arm"}],
          "gear":[{"label":"Jacket","slot":"apparel"}],"job":"Standing"}}}}
```

**6. Read public events.** Only fixed lifecycle items and accepted public messages and
letters appear. Keep `next` for the next read. Handle `cursor_expired` and `event_gap` by
reading the summary again and starting a fresh event read.

```json
{"v":1,"session":"s_11111111111111111111111111111111",
 "response":{"v":1,"id":"events","ok":true,"data":{
  "scope":{"session":"s_11111111111111111111111111111111","root":"r_22222222222222222222222222222222",
           "game":"g_33333333333333333333333333333333"},
  "items":[{"kind":"lifecycle","text":"observation_ready"}],
  "next":"e_77777777777777777777777777777777"}}}
```

**7. Save a screenshot.** `rim-pilot screenshot --session SESSION --root ROOT --game GAME --output ./capture.png`
writes the PNG locally and prints the metadata without the image bytes. The path stays on
your machine.

```json
{"v":1,"session":"s_11111111111111111111111111111111",
 "response":{"v":1,"id":"screenshot","ok":true,"data":{
  "scope":{"session":"s_11111111111111111111111111111111","root":"r_22222222222222222222222222222222",
           "game":"g_33333333333333333333333333333333"},
  "capture":"c_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","frame":23,"phase":"completed_render",
  "width":1280,"height":720,"scale":1,"encoding":"png_base64_chunks","bytes":68000}}}
```

**8. A refusal.** Some reads fail by design. A refusal prints the error envelope, a fixed
code on stderr and exits 69. It is never an empty result. Real example from the final
package: `things.list` and `resources` return this on every loaded map tried.

```json
{"v":1,"session":"s_11111111111111111111111111111111",
 "response":{"v":1,"id":"things","ok":false,"error":{"code":"unavailable"}}}
```

## What `available` means

`capabilities` reports `available` when a route's producer was registered at startup. That is
all it says. It does **not** say the route works on the map in front of you, and it carries
no reason text for the two routes that do not.

- **Final package, menu launch.** Available: `auth`, `health`, `capabilities`, `summary`,
  `pawns.list`, `pawn.get`, `things.list`, `resources`, `ui`, `screenshot`, `events.read`,
  `screenshot.map_view` (with a fixed reason text about its weaker paused and live proof).
  Unsupported: `ui.rendered_controls`, `specialist.structured`.
- **`things.list` and `resources` are reported available but refuse `unavailable`.** On every
  loaded map tried, including the final package, the raw list of spawned things was larger
  than the cap, so the route refuses before reading anything. Capabilities cannot say so.
  This is a known overstatement, left as is.
- **Treat a refusal as unknown.** Never read it as no things, no resources or no threats. The
  refusal can itself depend on what is on the map, hidden things included, so do not draw
  conclusions from when it appears.
- **Specialist screens are pixels only.** Research, bills, work tables, policies, world map,
  caravans, trade, quests and everything DLC-specific have no structured route. Use a
  screenshot of a screen the player already has open. See the
  [operation inventory](../tests/coverage/operations.md) for every area, DLC included, and
  which rows have any game evidence.

## Visibility policy in brief

Full rules are in the [visibility contract](../protocol/visibility.md) and
[safety manifest](../protocol/observation-safety.md). Coverage of these rules in the real
game is limited to one fixture; see the [phase-4 evidence summary](../tests/coverage/observation-adversarial.md#phase-4-real-game-evidence-summary).

- **Only what a normal player could inspect.** The camera does not matter, but fog,
  concealment and hidden pawns do. Hidden, unknown and stale targets all return the same
  `unavailable`.
- **Own colony pawns only.** Prisoners, visitors, enemies and wild pawns have no detail.
- **Shown rows only.** Needs, health rows, gear and the job report follow the normal cards.
  No raw health numbers, hidden conditions, queued jobs or private targets.
- **Nothing changes in the game.** Reads do not move the camera, select, open tabs or pause.
- **Developer mode blocks everything.** Developer mode and the debug-log panel make every
  state route refuse with `observation_refused`. Never turn them on to make a read work.
  Other panels and lingering debug flags are untested.
- **Unsupported is not empty.** A missing field or family means unknown, never zero or healthy.

## Errors, cursors and limits

The full tables are in the [wire contract](../protocol/wire-v1.md); this is the part an agent
needs most.

| Code | What to do |
| --- | --- |
| `authentication_failed` | Fix session or credentials. Never retarget silently. |
| `not_ready`, `observation_refused` | Loading, or safety refusal. Wait or stop; do not toggle settings. |
| `unavailable` | Unknown, hidden, stale or unsupported target, or a route that refuses. Treat as unknown. |
| `stale_scope` | Root or game changed. Read health or summary again. |
| `cursor_expired` | Cursor lived over 5 seconds, was evicted, or was reused with another method, scope or limit. Start the query again. |
| `event_gap` | Event history was lost. Read the summary, then start a fresh event read. |
| `busy`, `rate_limited`, `timeout` | Back off. Nothing was queued. |
| `forbidden_method`, `unsupported_method` | No such route exists; there are no actions. |

- **Pages hold 1 to 64 items** (default 64). Cursors can be reused until they expire.
- **Limits per session:** 4 connections, 16 pending requests, 8 requests a second per
  connection (burst 16), 4 snapshots kept for 5 seconds each, 2,048 records per snapshot.
- **Screenshots:** at most 1 per second and 1 at a time. Up to 1920x1080 is allowed in the
  wire format, but a 1920x1080 capture refused on a real launch and only 1280x720 was shown to
  work. Each capture costs about 50 to 116 ms of game-thread work, far above the 2 ms ordinary
  budget; that budget is unmet.
- **Errors say nothing extra.** No counts, positions or reasons are included.

## Evidence status and missing coverage

This page documents commands that exist. It is not an acceptance claim.

- **Shown on the final package:** menu, paused and live reads of summary, own pawns, pawn
  detail, UI, events and decoded screenshots, forbidden-method denials, developer-mode refusal,
  lifecycle invalidation and graceful quit from the main menu, on one copied fixture.
- **Not met or not shown:** `things.list` and `resources` always refuse; natural public events
  never appeared; hidden-state, off-camera, fog, invisibility, second-map and every DLC case
  were not tried; capture and first-use time budgets are over; bounded load is unmet.
- **Acceptance criteria 2, 3 and 5 are unmet or unverified.** Details and the unmet register
  are in the [phase-4 evidence summary](../tests/coverage/observation-adversarial.md#phase-4-real-game-evidence-summary),
  which this page links to rather than repeats.
