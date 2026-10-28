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
