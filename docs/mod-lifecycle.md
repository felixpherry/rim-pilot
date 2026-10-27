# Process bootstrap and game-thread initialization

The mod wires private loopback authentication and bounded main-thread read dispatch, not game observations. Readiness/reference generations have an adapter and pure tests; authenticated [health/capabilities](bootstrap-commands.md) report scheduling readiness and capability gaps. Observation safety adapters remain unimplemented. Ordinary observation reads receive explicit not-ready/stale/unavailable errors rather than invented baseline data. Authentication alone is not a healthy observation bridge. See [dispatch and transition boundaries](read-dispatch.md).

## Thread boundary

`RimPilotMod` calls `ModBootstrap.Bootstrap` from its constructor. Installed game source shows `Root.Start` queues asynchronous play-data loading, which loads active mods and invokes their constructors. The constructor therefore must not sample `Current`, `Find`, `Prefs`, any game collection or Unity object, nor register Unity events.

The process-static, BCL-only `ProcessLifecycle` attempts bootstrap once, including under concurrent/repeated constructors. It registers `AppDomain.ProcessExit` and `DomainUnload` fallbacks, then installs explicit Harmony prefix/postfix hooks on the declared `Verse.Root.Update` method plus a finite set of audited transition prefix/finalizer brackets. Metadata lookup and Harmony registration do not read game state. Installed Harmony's `PatchProcessor.Patch` serializes patch updates under its shared lock; this source audit is not proof of full-profile detour compatibility.

Installed `Root_Entry.Update` and `Root_Play.Update` both call the base update from Unity's frame loop. The prefix's first successful game-thread callback registers `Application.quitting` **before** starting one background session-owner thread. Subsequent prefix callbacks do not repeat initialization; the postfix handles bounded dispatch. No root object, scene component or per-root destruction handler owns the session. Menu/play root replacement cannot restart the listener or rotate its identity. The callback records its managed thread identity; the adapter exposes the active transport only on that thread.

Registry selection, private filesystem operations, listener startup and transport disposal run on the background owner, never the frame thread. Startup polls cancellation before opening the registry and around publication/worker creation. It reuses the shared descriptor/filesystem/transport contracts; there is no second authentication protocol or fallback public registry.

## Failure and shutdown

Bootstrap, quitting-registration or startup failure is terminal for this process. There is no per-frame retry, alternate endpoint or automatic restart. Partially installed dispatch hooks cannot enable observations after bootstrap failure because initialization remains gated off; installed transition observers only invalidate private pending work. Exception details and credentials are not logged. The mod never changes settings to make initialization work.

Unity quitting and both BCL fallbacks share an idempotent stop callback. First stop hides the active transport immediately, signals cancellation and wakes the single owner. It waits at most **1,000 ms** for that owner; repeated stops do not join again. The owner closes transport sockets, cancels pending reads, drains transport workers within their existing **1,000 ms** budget and removes only the unchanged descriptor authorized by its native ownership receipt. Cleanup does not call Unity/Verse APIs or depend on `Root.Shutdown`. The Unity subscription intentionally lasts for the process; fallback threads do not unsubscribe through Unity.

Native filesystem calls cannot be forcibly cancelled. An in-flight startup/publication or cleanup can exceed the outer shutdown grace; the background owner retains eventual cleanup duty, but abrupt process death can leave an uncertain private record. This is not a forced-kill/power-loss cleanup guarantee. Discovery must retain uncertain records and authenticate exact identities, never adopt or delete them based on timeout/PID. No observation data is produced during this interval.

## Verification boundary

`tests/JsonIngress/ProcessLifecycleTests.cs` compiles the actual BCL lifecycle source into the asset-free harness. It covers constructor concurrency, game-thread registration order, off-thread startup/disposal, repeated frame/root callbacks, terminal failures, concurrent quit/fallback, startup cancellation, bounded slow-start/cleanup and host-native descriptor lifetime. Fake lifecycle resources do not establish Unity callback behavior. Real .NET sockets/filesystem cases are host tests, not Mono tests.

Configured `make build-game` compiles the actual Harmony/Unity adapter for net472 and checks proprietary-reference/output boundaries. A separate authorized isolated full-profile Unity Mono run proved asynchronous startup through normal-menu authenticated health/capabilities. It does not prove every hook, pause/load/root replacement, quitting-event delivery, native socket interruption or product-owned descriptor cleanup. The recipe's PID/start-time-owned process cleanup and scratch removal are separate evidence, not a quitting-hook pass. See [runtime evidence](../tests/coverage/session-adversarial.md#isolated-menu-runtime-proof).
