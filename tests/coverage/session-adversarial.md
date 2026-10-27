# Session and installation adversarial coverage

These are asset-free Linux x64 host tests. Shared C# runs on .NET, not Unity Mono.
Configured net472 compilation and packaging are separate checks; neither proves game runtime behavior.
No observations, visibility adapters or actual rendering are established by this matrix.

Run `make check` and `make test`. For focused sockets run
`scripts/dotnet run --project tests/JsonIngress -c Release -- transport`.
Python installer tests run with
`python3 -m unittest discover -s tests -p test_install.py`.

| Boundary | Executable evidence | Required assertion |
| --- | --- | --- |
| Strict JSON and framing | `JsonIngress/Program.cs`, `WireTests.cs`, `TransportTests.cs` | Invalid UTF-8, duplicate keys, depth, trailing content, oversized/zero lengths rejected before dispatch; frame length checked before body allocation. Post-auth invalid-length cases send only headers, so a body-read bug cannot pass. |
| Partial frames and EOF | `TransportTests.cs`: partial auth, half-close truncated frame, absolute deadlines | Authenticated and unauthenticated EOF at zero, one, three, four and all-but-final frame bytes releases admission without queued work. Read side stays open; new peer still authenticates. Slow partial progress cannot renew absolute deadline. Terminal errors after disconnect remain best-effort. |
| Authentication and denied routes | `TransportTests.cs`: uniform pre-auth failure, repeated auth/forbidden/unknown methods, bootstrap params, secret correlation ID | Exact token/session/version required; fixed pre-auth errors expose no state; forbidden/raw-input/reflection routes never enter dispatcher; active secret cannot be echoed as correlation ID. |
| Producer/output boundary | `TransportTests.cs`: producer response validates typed shape, slow screenshot reader, socket-close backstop | Unexpected producer fields never serialize; synthetic large capture response hits finite write deadline; native socket closure interrupts pending write even with cancellation token None. Synthetic capture is not PNG/render proof. |
| Queue, admission and rates | `TransportTests.cs`: admission slots, pipelining, bounded pending queue, burst controls, byte buckets | Fixed admission slots, finite backlog/queue/token buckets, no second outstanding dispatch; cancellation prunes queue; denied burst rejected before remaining header/body without weakening rate limit. |
| Receive/watchdog race | `TransportTests.cs`: watchdog never mistakes bytes drained by active receive for peer EOF | Deterministic real-socket receive drains data under receive ownership; watchdog does not mistake it for EOF; true peer EOF still cancels after receive ownership ends. |
| Cancellation isolation | `TransportTests.cs`: disconnect queued/dequeued, request absolute deadline, one disconnected reader | Disconnected/expired work cannot complete late; another admitted request retains live lifetime and exact correlated response. |
| Native private paths | `NativeFilesystemTests.cs`, `test_native_filesystem.py`, `native/` | Installed-header ABI checks; actual UID/mode/type/link metadata; no-follow ancestor/record access; private pre-secret creation under permissive/restrictive umasks; bounded UTF-8 reads/enumeration and fd cleanup. |
| Native publication/removal races | `NativeFilesystemTests.cs`: parallel publishers, mutation after open, staged replacement, quarantine/restore collision | One no-replace winner; changed inode/content/mode/link refuses read/removal; raced objects restored or retained, never adopted; unknown replacement and quarantine both preserved on collision. |
| Discovery validation/budgets | `DiscoveryTests.cs`, `SessionDiscoveryTests.cs` | Literal loopback only; fixed private primary/fallback policy; finite descriptor/entry/record/probe budgets; whole-scan refusal rather than partial success; credential/path/endpoint-free ordinary listing. |
| Duplicate/stale/port reuse | `SessionDiscoveryTests.cs`: conflicting records, invalid/duplicate registry, stale plus live, port reuse, descriptor replacement | Conflicts rejected before probes; uncertain stale records retained; explicit choice required for ambiguity; cached exact identity cannot retarget replacement endpoint; no automatic reconnect. |
| Untrusted server | `SessionDiscoveryTests.cs`: probe rejects, bootstrap response validation | Wrong response identity/version/correlation, malformed/truncated/oversized/slow responses rejected; total deadline enforced; no raw server output or credential echo. |
| Process ownership | `ProcessLifecycleTests.cs` | One constructor bootstrap/session owner; game-thread quitting registration before background start; finite stop grace; cancellation at publication/start boundaries; late startup disposed; fallback/root replacement never resurrects process owner. |
| Dispatch and generation | `ReadDispatcherTests.cs` | Reference and transition-edge invalidation, same-reference remove/re-add, mutable-list witnesses, loading without map enumeration, bounded map count/time/work, wrong-thread refusal, expired/cancelled/stale work never executes. Bootstrap capability gaps remain explicit. |
| Package trust boundary | `test_package.py`, `test_project_commands.py` | Complete fixed path/mode/hash allowlist; traversal/links, unknown files and proprietary-content hashes rejected; trusted source/build inputs checked independently; SDK-free CLI smoke uses scratch registry only. |
| Explicit install/remove | `test_install.py`: target validation, unknown/modified/missing/link cases, receipts | Absolute fixed-basename target required; unknown/modified/missing/special/link files and wrong ownership refuse operation; repeated install/reinstall/remove touches only verified owned files. Scratch saves, ModsConfig, Prefs and unrelated mod sentinels checked unchanged after every test. |
| Installer races/failures | `test_install.py`: quarantine replacement, before-delete modification, collisions, disk full, directory lock | Changed identity/content never authorizes deletion; no-replace collision preserves unknown tree; failures leave untouched or explicitly recoverable state; cooperating concurrent installer refused. |
| Interrupted operations/recovery | `test_install.py`: mutation-boundary recovery, real process exit, repeated interrupted recovery | Resume from journal/publication/quarantine/commit/per-file deletion; recovery itself can die after successive deletions and resume without losing receipt authority; corrupt/unknown recovery state refuses cleanup. Incomplete preparations/detached journals deliberately retained. |

## Validation snapshot

Phase-2 adversarial audit: `make check` passed with 95 Python cases and zero-warning
Release builds. `make test` passed with 95 Python and 735 C# cases, self-contained
CLI publish/output guards and SDK-free scratch-registry smoke. Focused transport:
75/75; focused installer: 28/28. Added 14 socket cases and one repeated-recovery case.

Configured `make package` passed fresh reference audit, actual net472 compilation
with zero warnings/errors, forced-copy-local rejection, output allowlists and
complete-package validation; `make verify-package` passed independently. Initial
package invocation correctly refused existing output; previous package was retained
privately before rebuilding. Exact rebuilt package also passed CLI-driven scratch
install/reinstall/uninstall with copied save/settings/unrelated-mod sentinels unchanged.
Those audit results are host/build/package evidence. Separate actual menu evidence follows.

## Isolated menu runtime proof

A later explicitly authorized serial recipe run used a verified complete development
package, the real installer against a disposable target, copied full solo profile
(with only Rim-pilot appended) and copied authorized save. Normal menu readiness
was screenshot-checked and inspected on RimWorld 1.6.4871 with Harmony 2.4.1.
The actual game and packaged CLI shared verified private HOME/XDG values, including
the same 0700 runtime directory; the registry and descriptor were 0700/0600.

Packaged `sessions` authenticated exactly one live identity. Exact-session `health`
returned `ready`; `capabilities` advertised only auth/health/capabilities available,
eight baseline routes unverified and two specialist gaps unsupported. Twelve
explicit diagnostic command samples plus final health/capabilities succeeded;
these were separate invocations, not CLI reconnect/retry. Missing auth, wrong token
and wrong session produced identical `authentication_failed` errors and closed peers.
No historical probe, test endpoint, colony load or product screenshot route was used.

The run exposed real menu starvation: installed normal-menu code assigns null to
`Current.Game` every GUI pass. The original unconditional transition invalidation
rejected queued commands continuously. The scoped null-to-null exception is covered
by a host regression that fails under the original behavior, while non-null,
same-reference and skipped-prefix transitions retain conservative invalidation.
The rebuilt product then passed actual menu commands. Temporary diagnostics were
removed before that build. See [dispatch audit](../../docs/read-dispatch.md).

Private proof retains package/installed hashes, command JSON, normal-menu capture,
runtime logs and cleanup results outside the repository. PID/start-time-owned
cleanup removed scratch and owned processes; original authorized save, configuration
and source-mod hashes compared equal before and after. This is recipe cleanup proof,
not delivery of Unity's quitting callback or product-owned registry removal.

Final phase-2 validation after the menu fix: `make test` passed **95 Python and
740 C#** cases (dispatcher 48/48); configured `make package` and independent
`make verify-package` passed with zero-warning/error net472 compilation. The exact
final package was then rerun serially through the guarded normal-menu proof with
the same command/auth results and unchanged-original/owned-cleanup checks.

## Evidence limits

- The menu proof establishes exercised native binding, startup and menu dispatch under this full profile, not every Harmony detour or the entire host adversarial matrix on Mono. Pause/live/load/root changes, quitting callbacks, native cancellation/deadline stress and repeated worker/descriptor cleanup remain NOT RUN.
- Shutdown races and fd checks are finite stress cases, not exhaustive scheduler proofs. Rate-error delivery is best-effort on actual disconnect, timeout or network failure.
- Installer tests use scratch fixtures and cooperative-writer locks, including actual child-process exit. They do not prove power-loss durability, arbitrary filesystem support or a sandbox against hostile same-UID writers.
- No game launch, personal-install modification, baseline observation readiness or hidden-state safety follows from these tests. Real-game evidence remains separate.
