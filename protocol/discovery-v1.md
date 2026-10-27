# Private discovery v1

Status: shared executable descriptor/budget/directory contracts, [native Linux x64 storage boundary](../docs/native-filesystem.md), bounded listener publication and CLI `sessions` with authenticated probes. [Process bootstrap](../docs/mod-lifecycle.md) and authenticated CLI [health/capabilities](../docs/bootstrap-commands.md) are wired; baseline observations remain unavailable. Asset-free CLI/native/loopback tests and net472 compilation are not Unity Mono/game evidence.

## Descriptor

`discovery-v1.schema.json` describes one private UTF-8 JSON object with exactly `v`, `session`, `host`, `port`, `token`. Protocol version is integer `1`; session is `s_` plus 32 lowercase random hex digits; token is 64 lowercase random hex digits. Host is exactly the literal `127.0.0.1`, port is integer 1–65535. No DNS, URL, IPv6, numeric-IP aliases, remote endpoint, PID, timestamp, process argument or filesystem reference. This v1 chooses IPv4 loopback only; listener must bind that same address.

Record basename is exactly `<session>.json`, ordinal/case-sensitive. Filename and descriptor session must match. Reject missing/unknown fields, duplicate or escaped-duplicate keys, coercion, null, fractional numbers, trailing content, comments, invalid UTF-8 and unsupported versions. `DiscoveryDescriptor.Read` uses existing strict ingress plus explicit primitive mapping. Neither schema metadata nor parsing verifies filesystem privacy or liveness.

Native reader rejects lengths above 1024 before allocating content, reads at most 1024 bytes plus one overflow sentinel, and rejects growth/unsafe metadata. Private encoder emits only fixed validated fields. `LinuxPrivateDirectory.Publish` writes secrets only after native private creation is verified.

## Finite discovery work

| Budget, per invocation | Ceiling |
| --- | ---: |
| Descriptor content | 1024 UTF-8 bytes |
| Directory entries examined, including unrelated/invalid names | 64 |
| Record read attempts, including rejected/unreadable records | 16 |
| Aggregate reserved record content | 16384 bytes |
| Authenticated liveness probe attempts, including failures | 16 |
| Concurrent probes | 1 |
| One connect/auth probe, combined absolute deadline | 1000 ms |
| All probes, combined absolute monotonic deadline | 5000 ms |
| XDG runtime base input | 1024 UTF-16 code units; each component ≤255 UTF-8 bytes |

`DiscoveryBudget` reserves attempts before work. Enumeration permits one end/overflow lookahead beyond 64 entries, never another record read; do not materialize an unbounded directory listing or sort it first. Stop on first exhausted budget and fail whole discovery, not a partial session list or implicit first-instance selection. Reserve a full descriptor budget for every attempted record read, even rejected records. Never retry probes or replenish budgets on failure. Native record operations must be nonblocking regular-file operations; do not follow FIFOs/devices/links. Filesystem/kernel scheduling latency is not a hard real-time guarantee.

Probe timeout is min(1000 ms, remaining total allowance). Caller uses one monotonic clock starting before first probe; serial admission, finite framed auth I/O and owned socket closure must enforce it. `BeginProbe` only reserves counts/allowance; it does not establish native I/O deadlines or stop a running probe. Do not accept partial probe results when total time expires. Probe exact descriptor session and token, validate authenticated response session/version, never reconnect/retarget. Connection failure/PID absence is not proof permitting stale-file deletion. Duplicate/conflicting records fail closed, not overwrite by dictionary key. CLI scans and validates the complete bounded record set before probing; matching sessions or matching loopback ports conflict even when credentials differ. Invalid JSON record names/content, unsafe files and record/entry overflow reject the entire scan. Non-JSON entries (including native private workspaces) are counted but not adopted or removed.

## CLI discovery and exact selection

- `rim-pilot sessions` returns one JSON object with `v: 1` and `sessions`; each item contains only `v`, `session` and `status` (`live` or `unavailable`). A live status means a bounded fresh connection authenticated the descriptor's exact session/token and returned a strictly validated matching auth response. It is not game readiness or observation success. Probes close immediately after each listing result.
- `rim-pilot sessions --session s_<32 lowercase hex digits>` verifies only that exact record after complete registry validation. Unknown/invalid identities return fixed `session_not_found`, without echoing arguments. No credential, endpoint or runtime-path command arguments exist.
- The connection-selection API permits omitted selection only when exactly one valid descriptor exists. Multiple descriptors require explicit identity even if one recently probed unavailable: a busy/slow instance is not proven dead. It transfers the same authenticated socket to later command routing, never probe-then-reconnect. Discovery objects permit one operation only. Expired/disconnected connections cannot be revived; a new explicit invocation is required.
- A conflicting endpoint, malformed record, exhausted budget or unsafe registry fails the whole invocation; no partial output or guessed first session. An individual failed probe reports only `unavailable`: timeout, refusal, wrong session/token, port reuse and malformed response do not establish staleness. CLI never deletes records, including uncertain ownership/staleness or interrupted native workspaces. The publisher alone may remove its unchanged owned record using its native receipt.
- Exit `0`: at least one listed live session, or successful explicit verification. Exit `69`: no live session or fixed discovery/selection failure. A completed all-unavailable/empty listing still emits its full JSON, then fixed `no_live_session` on stderr. Fatal scan/budget/selection failures emit no partial JSON. Exit `64`: invalid command syntax. No observations or bootstrap commands are implemented by this unit.
- `sessions` liveness is advisory, not a lease. Later commands must authenticate their selected exact identity anew for that invocation and retain that same socket throughout it. Never replace a failed explicit target with another listed instance, re-read a changed record to reconnect, or infer process identity from PID/port.

## Identical runtime policy

Both mod and CLI import the same `shared/Protocol/Discovery.cs`; neither has a separate fallback algorithm. Supply native effective UID (`geteuid`), not `$UID`, login name, HOME or PID. Supply `XDG_RUNTIME_DIR` verbatim, with no tilde/environment expansion, relative-path resolution or path normalization.

1. Nonempty canonical absolute XDG base is candidate only: reject root, trailing slash, empty/`.`/`..` components, controls, backslashes, invalid Unicode and path/component ceilings. Registry is `<XDG_RUNTIME_DIR>/rim-pilot/v1`.
2. Native verifier must anchor/traverse without following links and verify XDG base belongs to effective UID and has mode 0700, with directory types/ancestor safety. Verify or privately create owned mode-0700 registry components before use. Existing unsafe metadata or refusal makes this candidate unusable, not permission to chmod/adopt it.
3. Missing, malformed or unverifiable XDG candidate selects fixed fallback `/tmp/rim-pilot-<effective-uid>/v1`. Ignore `TMPDIR`, `TMP`, `TEMP`, HOME and working directory. Native verifier must anchor genuine root-owned sticky `/tmp` (mode 01777), then no-follow verify/create user-owned mode-0700 fallback base and `v1`. Never repair/adopt an unsafe existing fallback.
4. If fallback cannot be verified, fail closed with fixed `RuntimeUnavailable`. No public-directory or managed create-then-chmod fallback. Do not scan both registries, merge them, or switch directories after selecting one for an operation. Mod and CLI must run with matching UID/runtime environment; changed environment does not silently retarget an existing client.

`RuntimeDirectoryPolicy.Select` is pure policy with a required native verifier callback. `LinuxPrivateDirectory.Open` supplies the native verifier and retains its exact fd chain; production callers must use that entry point rather than a managed filesystem check or permissive callback. Callback handles expected native failures with fixed codes/false; policy strips ordinary I/O/security exception text. Callers must not log native exceptions or candidate paths.

## Output and credentials

`PublicJson()` contains only `v` and public `session`; neither token nor runtime path, descriptor basename, endpoint or raw filesystem/parser diagnostic belongs in ordinary listing. `EncodePrivate()` is explicitly private persistence, never ordinary stdout/logging. Descriptor/runtime objects have fixed path/token-free `ToString()`; discovery exceptions retain only fixed code/message, no input/inner exception. Do not log private DTO properties or serialized private bytes. Auth uses private token over loopback, not command-line switches; no token/private-runtime-path values in process arguments. Runtime environment coordinates discovery; explicit CLI targeting uses public session ID only. General errors must use fixed error codes, not rejected values or native exception details.
