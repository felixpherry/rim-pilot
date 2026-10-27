# Authenticated bootstrap commands

Current implementation offers session discovery, authentication, minimal health and capability gaps. It does **not** offer baseline game observations yet. Host tests and net472 compilation are not Unity Mono/game acceptance. A separately authorized isolated full-profile Unity Mono menu run proved packaged CLI discovery/authentication, health and truthful capabilities; it does not prove pause/live/load/quit behavior or baseline observation safety. See [runtime evidence](../tests/coverage/session-adversarial.md#isolated-menu-runtime-proof).

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

Exit codes: **0** for validated successful command (including a truthful loading health response), **64** for usage, **69** for discovery/connection/validated server failure. A validated server error prints the same wrapper with `ok:false`, fixed known code on stderr and exit 69. No raw exception, rejected input or credential is printed. CLI currently accepts only `sessions`, `health`, `capabilities` plus optional exact `--session SESSION`; there is no raw-method pass-through.

## Health is not observation readiness

`health` and `capabilities` are authenticated bootstrap exceptions. Both use the existing bounded main-thread queue/deadline/cancellation path. They expose no pawn/map/manifest/UI/capture data, and do not depend on unimplemented developer-safety evidence.

- `ready`: this Root.Update postfix passed root/scene/loading/generation scheduling checks. **Not** proof of developer safety, observation availability, normal-player eligibility, completed rendering or Mono compatibility. No observation scope is published by current health producer.
- `loading`: sampling is not ready, root is invalid or an audited transition is active. No loading map collection is accessed.
- A transition may invalidate a queued bootstrap request with `not_ready`/`stale_scope`; queue overload, callback starvation, timeout, disconnect and shutdown can also fail rather than return a status. Caller may explicitly issue a new command; CLI never silently retries. Wire also reserves `bootstrap`/`stopping`, but current producer does not fabricate callbacks in those states.

Capabilities mark **only `auth`, `health`, `capabilities` available**. Baseline `summary`, `pawns.list`, `pawn.get`, `things.list`, `resources`, `ui`, `screenshot`, `events.read` remain `unverified`, with their fixed potential v1 schema identifiers. `ui.rendered_controls` and `specialist.structured` are explicit current `unsupported` gaps (`schema:"none"`). These are implementation status, not baseline completion or empty/no-threat results. Phase-3 audited adapters/safety sampling remain required; ordinary reads return `not_ready`, `stale_scope` or `unavailable`, never fabricated healthy/empty data.

Forbidden action/input/debug names and all unknown methods are rejected by strict request parsing before queue admission, including while loading. No generic game dispatcher, reflection fallback, controls or grants exist. [Wire contract](../protocol/wire-v1.md) and [dispatch boundary](read-dispatch.md) remain authoritative.
