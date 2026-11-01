# Authenticated bootstrap commands

This page covers the bootstrap commands (discovery, authentication, health, capabilities). Typed baseline observation commands (summary, pawns, things, resources, UI, events, screenshots) are implemented on the same client; see [observation commands](observation-commands.md). Host tests and net472 compilation are not Unity Mono/game acceptance. Limited exact-release Unity Mono evidence exists on one copied fixture; see the [phase-4 evidence summary](../tests/coverage/observation-adversarial.md#phase-4-real-game-evidence-summary) for what was shown and what is unmet, including `things.list` and `resources` refusing `unavailable` on every loaded-map launch tested. Earlier [menu runtime evidence](../tests/coverage/session-adversarial.md#isolated-menu-runtime-proof) covered only packaged discovery/authentication, health and capabilities at the menu.

```sh
rim-pilot sessions
rim-pilot health --session SESSION
rim-pilot capabilities --session SESSION
```

Each command performs one bounded private-registry scan. `health`/`capabilities` may omit `--session` only when exactly one descriptor exists; even an unavailable second descriptor requires explicit choice. No token/path/endpoint appears in ordinary output or arguments. Discovery proves the expected session/token, then transfers that **same socket** to the command. No reconnect, retry, replacement-session adoption or uncertain-record deletion occurs. See [discovery](../protocol/discovery-v1.md).

## Output and validation

`sessions` retains its `{v,sessions:[{v,session,status}]}` output. Bootstrap commands print one compact JSON object:

```json
{"v":1,"session":"s_11111111111111111111111111111111","response":{"v":1,"id":"health","ok":true,"data":{"state":"ready"}}}
```

This is a synthetic shape example, not game evidence. Outer `session` identifies the authenticated target; `response` is the validated wire envelope. Before stdout, CLI enforces ordinary frame ceiling, strict UTF-8/JSON/quotas, exact protocol/correlation ID, method-specific DTO fields/types, unique capability names, known states/errors and matching session if health supplies an optional scope. No arbitrary response serialization fallback. Malformed/truncated/oversized/wrong-method/wrong-session responses produce no stdout and fixed `session_unavailable` diagnostics. Absolute socket-close deadlines bound header/body reads, including partial progress. Server receives no filesystem path or arbitrary command arguments.

Exit codes: **0** for validated successful command (including a truthful loading health response), **64** for usage, **69** for discovery/connection/validated server failure. A validated server error prints the same wrapper with `ok:false`, fixed known code on stderr and exit 69. No raw exception, rejected input or credential is printed. Typed observation commands now extend this exact-session client; see [observation commands](observation-commands.md) for scope, pages, events and validated local PNG output. There is no raw-method pass-through. Producer/runtime coverage remains separate from client command support.

## Health is not observation readiness

`health` and `capabilities` are authenticated bootstrap exceptions. Both use the existing bounded main-thread queue/deadline/cancellation path. They expose no pawn/map/manifest/UI/capture data, and do not require developer-safety authorization.

- `ready`: this complete derived-update callback passed root/scene/loading/generation scheduling checks. **Not** proof of developer safety, observation availability, normal-player eligibility, completed rendering or Mono compatibility. While observation is currently authorized (safe developer evidence, sampled ready frame, no boundary/refusal), health adds the root/game `scope` (never a map): the cold-start handle for `summary`. Unsafe, unknown, loading or refused states publish no scope.
- `loading`: sampling is not ready, root is invalid or an audited transition is active. No loading map collection is accessed.
- A transition may invalidate a queued bootstrap request with `not_ready`/`stale_scope`; queue overload, callback starvation, timeout, disconnect and shutdown can also fail rather than return a status. Caller may explicitly issue a new command; CLI never silently retries. Wire also reserves `bootstrap`/`stopping`, but current producer does not fabricate callbacks in those states.

Capabilities mark `auth`, `health`, `capabilities` available, plus `summary`, `pawns.list`, `pawn.get`, `things.list`, `resources`, `ui`, `events.read` and `screenshot` only when their source-audited producers bound at startup ([summary audit](visibility-audit.md#summary), [own pawns](visibility-audit.md#own-pawns), [things/resources](visibility-audit.md#things-and-observed-resources)). Pawn detail adds audited needs, health, gear and job/lord families independently; unverified families stay absent. Things/resources cover their documented exact-type eligible projection, not every specialized/custom type or normal resource-bar totals. Failed producer binding leaves the affected route `unverified`, with its fixed potential v1 schema identifier. `available` means registered implementation, not proven useful/safe actual-game behavior; `things.list` and `resources` always refuse `unavailable` when the raw spawned-thing list exceeds 2,048 members (every real map tried) and capabilities does not say so. `ui.rendered_controls` and `specialist.structured` remain `unsupported` gaps (`schema:"none"`). `screenshot.map_view` follows screenshot producer binding (`available` or `unverified`, `schema:"screenshot-v1"`). Its fixed `reason` documents the user-approved weaker paused/live proof: newer frame, passed capture GPU fence, uninterrupted safety/readiness/scope/size/scale across hold-then-release. Map images show what the game displayed under depth-only primary clear, not replacement of every primary pixel; whole-frame provenance holds for menu capture only. Unsafe state, scope/size/scale change, timeout and all other refusals remain. No input, forced clear or external fallback. Paused and live map-view PNGs decoded as complete 1280x720 images on one copied fixture (capture-time budget unmet); world/specialist capture remains unverified. Registration never promises scene-wide screenshot availability. These are implementation status, not baseline completion or empty/no-threat results. Finite safety sampling/private epochs guard every ordinary route; reads may return `observation_refused`, `not_ready`, `stale_scope` or `unavailable`, never fabricated healthy/empty data.

Forbidden action/input/debug names and all unknown methods are rejected by strict request parsing before queue admission, including while loading. No generic game dispatcher, reflection fallback, controls or grants exist. [Wire contract](../protocol/wire-v1.md) and [dispatch boundary](read-dispatch.md) remain authoritative.
