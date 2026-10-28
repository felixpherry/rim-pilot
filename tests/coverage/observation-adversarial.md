# Safe observation: implementation and evidence boundary

Phase 3 implements producers and client commands. The matrix and tables below are
**host/source/net472 evidence only**. Earlier [menu runtime proof](session-adversarial.md#isolated-menu-runtime-proof)
covered authentication/health/capabilities before these adapters existed. Phase-4
exact-release Mono evidence on one copied fixture is summarized separately, with every
unmet item, in [Phase-4 real-game evidence summary](#phase-4-real-game-evidence-summary).
It does not establish M1 acceptance. All 106 operation-inventory rows, including every
DLC scenario, retain their unverified game-evidence status.

## Source basis

Private inspection of installed RimWorld 1.6.4871 rev600 supports the rules in
[visibility audit](../../docs/visibility-audit.md), not a generic assumption that
unfogged means public. Final regression audit re-read these installed symbols:

- `Verse.FogGrid.IsFogged`: invalid cells/uncreated grids return false. Bounds,
  geometry and initialization must precede fog access.
- `RimWorld.ThingSelectionUtility.SelectableByMapClick`: selectable definition,
  hidden-pawn check, spawned parent and any unfogged occupied cell. Bridge deliberately
  restricts this to direct spawn; parent visibility never authorizes contents.
- `RimWorld.InvisibilityUtility.IsHiddenFromPlayer`: own-player exception and
  invisibility-component player visibility. Bridge refuses unaudited reveal/cache
  paths conservatively and ignores no debug override: unsafe debug state refuses.
- `RimWorld.HealthCardUtility.VisibleHediffs`: missing common ancestors separate
  from normal visible nonmissing rows; blood-loss caller context matters. Bridge
  mirrors fields rather than call virtual getters or populate health caches.

Decompilations and installed assembly inputs remain private. Source-wiring tests
assert the adapter boundary without redistributing proprietary fixtures; they do
not execute those adapters or prove full-profile patch compatibility.

## Executable host/source matrix

Paths below are under `tests/JsonIngress/` unless prefixed `tests/`.

| Boundary | Positive and negative regressions | Remaining actual-game proof |
| --- | --- | --- |
| Admission/getters | `ProjectionAdmissionTests`: exact provider/definition/component identity, six patch categories, poisoned unknown getter, budget and release revocation. `tests/test_projection_admission.py`: normal caller closure and nonmutating needs/health/gear/job mirrors. | Full-profile binding, getter equivalence, language/type coverage and measured cost. |
| Fog/concealment/reference | `TargetGateTests`: uninitialized/invalid/overflow geometry, any-unfogged footprint, own invisible pawn, concealed nonplayer, prepaid bounded scan, reference kind/owner/eviction. `ReadDispatcherTests`: noncurrent maps, inaccessible/unknown uniform errors and generation rotation. | Off-camera/secondary-map/DLC concealment and no UI changes. |
| Counts and output channels | `ObservationRegressionTests`: hidden population leaves frozen page items, totals, summary count, UI and allocated reference count unchanged; hidden labels throw if evaluated. `ThingProjectionTests`: eligible stack grouping/overflow, excluded-member revisions. `SummaryProjectionTests`: unknown count absent, not zero. | Fixture composition uses host primitives, not Verse readers; hidden-density timing/availability and exact public count basis still need game tests. |
| Pawn families | `PawnProjectionTests`: needs order/food stat arithmetic, hidden-health row equivalence, missing ancestor/replacement/grouping, gear and independent job/lord fields. Source guards check hidden filtering before health labels and target re-gating before every detail family. | Compare every supported row to normal UI; count omissions under full profile. Unknown hidden hediff/need types can still affect whole-family presence; this is an open risk, not a hidden-output acceptance pass. |
| Snapshot ownership | `SnapshotStoreTests`: immutable values, reusable query-bound cursors, whole-vector release proof, early-member change between slices, expiry/churn/eviction/cancellation. `ThingProjectionTests`: excluded decisions and stack changes latch invalidation. | Live throughput, hook completeness and all-decision stability. |
| Public events | `PublicEventRingTests`, `PublicEventWitnessTests`: accepted-only retention, dedup/history/removal, immutable independent readers, finite bytes/cursors/gaps, unsafe resets. `ObservationRegressionTests`: rejected attempts do not affect sequence/bytes/payload. `tests/test_public_events.py`: canonical hooks only, no archive scans/target projection. | Accepted/delayed/rejected/duplicate/historical events under real Mono and full profile; no dismissal/timer/selection mutations. |
| Selection/windows | `UiProjectionTests`, `tests/test_ui_projection.py`: eligible refs/labels only, exact ordinary kinds, immutable bounded results; titles/custom internals/rendered controls absent. | Normal selection/window comparison, world view, overlapping/scaled/custom UI; no control clipping/occlusion claim. |
| Render/capture | `RenderProvenanceTests`, `ScreenshotCaptureTests`: newer whole-clear frame, balanced callbacks, hold-then-later-fence release, discard on epoch/scope/size-scale/unsafe/refusal/timeout/cancel/transition while held, prompt refusal for unwitnessed frames, irreversible unsafe interval, geometry/scope/exception/disconnect/deadline refusal, one flight/cadence, PNG bounds/orientation and resource ownership. Source guards prohibit UI/render mutation. | Useful actual pixels/orientation/fence ordering/cadence and native/total latency, memory and cleanup. Host graphics stubs prove no real GPU property. |
| Client/transport | `ObservationClientTests`: every typed route through real host sockets/dispatcher with synthetic producers, exact scope/identity, no reconnect, cursor bounds, complete PNG/DEFLATE/CRC/EOF and safe local output collisions. | Actual packaged CLI against exact-session Mono product at menu/pause/live. |
| Read-only surface | `WireTests`: forbidden input/action/debug methods. `ObservationRegressionTests`: dotted/alternate mutation/reflection names remain unsupported. `TransportTests`: 45 authenticated real-host-socket denial/recovery cases (all 15 explicit forbidden names, 20 dotted/case/Unicode/control/empty aliases, action-field smuggling through all 10 observation routes). Every smuggling base request first passes typed validation; each negative requires exact secret-safe error, no queue admission, closed-peer worker release and a subsequent healthy read with no latent intent. A private parser mutant that discards the action field fails all 10 smuggling tests at the no-admission assertion. Source guards: no generic inspect/stat/health cache/UI mutations. | Actual Mono wire denials and repeated reads while paused preserve tick, camera, selection, windows, orders and original data. Host transport success does not prove these game invariants. Live simulation is not frozen by observation. |

`make check` compiles the asset-free harness and runs Python source/fixture tests.
`make test` runs **all** C# suites, including event ring/witness suites (previously
reachable only through focused `admission` mode), scratch installer/package tests,
CLI publish/output allowlist and SDK-free smoke. A wiring regression checks that
all test suites remain in the full harness. Configured `make build-game` separately
compiles actual net472 against private references; it never executes the game.

## Runtime gaps retained

- `available` means producer registered, not successful full-profile observation.
  Unknown getter/patch/type coverage can refuse a route or omit a pawn family.
  Missing fields do not mean empty/healthy/no threats. See detailed family limits
  in the visibility audit and [typed commands](../../docs/observation-commands.md).
- Resource basis is eligible observed direct-spawn stack units, **not** the normal
  resource readout, stored contents, wealth or a whole-colony stock claim.
- Screenshots use conservative exact stored-PNG preflight: 1280×720 fits current
  byte ceiling; 1920×1080 refuses without scaling/cropping. Capture's separate
  100-ms cooperative stage deadline is not a native-call bound or the ordinary
  2-ms shared scheduling guarantee. That divergence needs measured runtime resolution.
- Rendered controls and specialist structured schemas remain unsupported. Screenshots
  cover only already-rendered screens if safe capture succeeds; no automatic setup,
  input injection, hover or hidden control scraping.
- Phase 4 serially exercised the exact built product at menu, paused authorized copied
  fixture and live play using guarded isolation; results and gaps are in the
  [Phase-4 real-game evidence summary](#phase-4-real-game-evidence-summary). Source/host/compile
  passes cannot satisfy any missing Mono/UI/pixel/timing/hidden-state acceptance criterion.

## Phase-4 real-game evidence summary

This section consolidates the phase-4 verdicts. It is a public-safe summary: no private
proof locations, credentials, saves, game content or personal paths. Detailed per-run
proof is kept privately; this file records only what was concluded and what was **not**.
It makes **no acceptance claim**: acceptance criteria 2, 3 and 5 stay unmet or
unverified for the reasons below, no operation-inventory row is promoted, and no
project-record claim follows.

### Evidence classes (never merged)

| Class | What it is | What it can show |
| --- | --- | --- |
| Host / net472 | Asset-free C# and Python suites, configured net472 compilation, source and installed-code audits | Contract, logic and compile properties only. No Unity Mono or game behavior. |
| Diagnostic | Scratch-only builds with extra timing/gauge instrumentation, never in the release package | Where time and limits go. **Not** release proof. |
| Exact-release Mono | The packaged release mod and CLI run in an isolated, guarded RimWorld 1.6.4871 instance with the full solo mod profile and a copied authorized fixture, driven only through normal UI by the harness | Behavior of that artifact on that fixture and renderer. One fixture: one map, four own pawns (three colonists, one animal), no fog, invisibility, secondary map or DLC-specific scenario. |

### Artifact binding

Evidence below ran on several successive release builds of the mod, not only on the final
one. The earliest runs (menu baseline, first roster, needs) predate build A and are not
re-bound to any later artifact. Only the last run in the table used the final mod assembly.

| Build | Mod assembly SHA-256 (prefix) | Exact-release evidence on it | Source change that distinguishes it |
| --- | --- | --- | --- |
| A | dd4aef42 | menu capture series, paused health and gear/job reproofs, UI cases, live baseline and natural-event poll, developer safety, load/menu lifecycle, quit/restart, capture-budget measurement, ingress/auth/read-only/cursor | later of the earlier fixes |
| B | d3b13d0e | bounded-load and snapshot-eviction runs, UI overlay reruns | no-op developer-mode setting write no longer counted as an unobserved safety interval |
| C (final) | 21b52054f2c146949c43cdcce31d732c4ba7a658176797bb294242207ac6cf94 | one paused health/roster regression reproof | health family presence no longer depends on hidden conditions |

- **Final CLI SHA-256:** 1e5cdb4929c7f35d4b7dd08d4779b76afb23567cdeacf870b5fdfe3deb0949d0.
- **Final bundled JSON library SHA-256:** e1e27af7b07eeedf5ce71a9255f0422816a6fc5849a483c6714e1b472044fa9d.
- **Fresh rebuild reproduced build C bit for bit** from the committed source, and independent package verification passed. It checks the package against local build outputs and the redistribution allowlist; it is not a runtime check.
- **Build assignment of earlier runs is derived** from retained package snapshots and timestamps, not recorded by each run. Treat it as best-effort.
- **Final-artifact gap:** apart from the one health regression run, behavior on builds A and B has **not** been re-proven on build C. A compact serial guarded re-confirmation on the final artifact is still required before any verification line is ticked.
- **Private proof retention gap:** raw proof for the earliest runs (menu bootstrap, first roster passes, needs, first UI runs) is no longer retained. Their verdicts rest on records made at the time and on later runs that re-covered the same behavior.

### Per-scenario verdicts

Verdict words: **pass** (stated scope only), **partial**, **unmet**, **audit only**, **host only**.

| Scenario | Verdict | Exact-release Mono evidence | Not shown |
| --- | --- | --- | --- |
| Main menu baseline | pass | Discovery, authentication, health, capabilities, scoped summary (scene, build, DLC and mod manifest, no game or map), UI, events and a complete decoded 1280x720 product PNG compared with the normal menu. | Early raw proof not retained. |
| Paused roster (own pawns) | pass | Scoped health and summary, two complete limit-2 roster pages with identical rows and no duplicate references, selected UI, before/after tick, pointer, selection and windows unchanged, idle and read-control images byte-identical. | Slaves, mechs, mutants, unnamed animals, secondary maps, positive dead/downed/drafted flags, queued-order variants. |
| Paused needs | pass, flagged | Recorded as passed in an early batch run. | The raw proof of that run is gone and was never independently reopened. Later runs show needs present on every detail read but no retained value-by-value comparison. Review. |
| Paused health | pass (this fixture) | Four rows compared with the normal health tab: one timed condition row, three no-condition rows. Reconfirmed on the final build. | Blood loss, injuries, missing parts, non-English text; hidden-condition path (see below). |
| Paused gear and job | pass (this fixture) | Equipped and worn labels, order and quality match for all three colonists, the animal's gear tab absent and family omitted explicitly; four current public job reports match normal inspection. | Lord text, styled, unique, custom or specialized providers. |
| Things and resources | **unmet** | Both routes return the fixed `unavailable` refusal on every launch, because the raw spawned-thing source exceeds its complete-source cap before traversal. | Any item, stack, total, position, off-camera, page or footprint comparison. |
| UI (selection and windows) | partial | Eligible selection, exact ordinary window kinds, absent rendered controls, titles and custom internals, truthful unsupported rendered-controls capability, complete useful product PNGs for map, gear, menu, Options, Research and mod-settings overlays (after a proven defect repair). | Scaled UI (game rejects 1.25x at 1280x720), partially offscreen window (nothing draggable through normal UI), custom or mod screens (none seen). |
| Live baseline | partial | Summary 3/3, roster 3/3, UI 3/3, pawn detail 12/12, map PNG 3/3 (decoded, complete, upright; game clock and pawns visibly advanced). | Things and resources; sample-by-sample normal-UI comparison (live state moves); slave, mech, mutant, secondary-map and DLC variants. |
| Public events | **unmet** | Six live polls and two paused readers worked; only fixed lifecycle items appeared; no pre-load archive replay. | Any natural message or letter versus normal history, removal availability, delayed, rejected, duplicate or overflow cases. |
| Hidden output and five DLCs | **audit only** | No new launch. Fresh installed-source audit, host negatives, unknown and issued-then-ineligible references share one error. A static defect (hidden-condition-driven health family presence) was found and repaired. | Real-game hidden-condition path, fog, invisibility, secondary map, every DLC mechanic, availability and timing side channels (see register). |
| Ingress and authentication | pass | 24/24 checks: uniform pre-auth failure, strict JSON and frame errors, wrong identities, valid Unicode, healthy-peer recovery each time. | A first run kept as failed harness evidence (wrong close expectation). |
| Read-only surface | pass | 15 explicit forbidden methods, 20 aliases, 10 action-smuggling routes denied with secret-safe errors; 17 CLI invocations exit with usage error; repeated reads leave tick, pointer, selection and windows unchanged. | Nontrivial queued-order variants. |
| Developer safety | partial | Dev mode on, dev mode on with the debug-log panel, and dev mode off with the panel still open each refuse all eight state routes; clean recovery after the panel closes. | Lingering static debug flags, all other known panels, first frames after a toggle, third-party developer windows. |
| Cursors | partial | Reusable same-snapshot pawn pages, wrong method or limit and 5.2-second expiry give the same expiry error, event cursor expiry (never a gap), snapshot-capacity eviction at 4 in 3/3 launches. | Thing pages, second-map binding, source churn, real retained-history loss and event gap, event-cursor capacity eviction. |
| Load and menu lifecycle | pass, flagged | Every old identity rejected after return to menu and after reload, never accepted again; fresh scope works. | One reload-window read exited with no readable reply (accepted harness correction, below). |
| Quit and restart | pass (main menu), flagged | Graceful UI quit removed the product's own descriptor and exited on its own; replacement session cannot be retargeted by old identity or record. | Quit from a loaded colony, abrupt-death leftovers (host only). Accepted harness corrections, below. |
| Bounded load | **unmet** | Transport quotas, worker and snapshot-build limits and eviction behave as documented; healthy-peer recovery after every flood. | See first-use overrun and resource notes below. |
| Capture budget | **unmet** | 36/36 release captures complete, upright, fresh at 1280x720; a real 1920x1080 launch refuses with a size error and writes nothing. | See capture hitch notes below. |
| Installer | pass (host/filesystem) | Real installer, eight invocations on a stopped-game scratch target; refusals preserve inventory; originals unchanged. Not Mono, game or shutdown evidence. | Must be redone on the final package at final verification. |

### Unmet and unverified register

- **Things and resources refuse above the raw-source cap.** The orchestrator kept the refusal and implements no larger scan. Immutable results do not prove completeness of excluded decisions, raw-list and fog counters miss plain-field changes, and the synchronous comparison is not a proven scheduling bound even below the cap. Counts and totals are never partial. Acceptance criteria 2, 3 and 5 stay unmet for this behavior.
- **Hidden-driven availability.** Because the raw count includes fogged and hidden things, the refusal itself can vary with hidden state. Not measured.
- **Scaled and offscreen UI.** Not attained on the fixture. **Comparisons in live play** are historical samples, not atomic.
- **Natural public events.** None occurred; nothing compares to normal history.
- **Cursor and event gap.** No real history loss; the gap code was never exercised, and expiry is never substituted for it.
- **Hidden-condition health in the real game.** The repair is host and source evidence only; the fixture cannot produce the condition without edits or debug tools.
- **Developer-safety untested flags and panels** listed above.
- **First-use cost.** Single ordinary steps overran the 2-ms shared allowance on first use of a route (diagnostic: up to 9.37 ms spent in one update, three frames; repeated cycles stayed under 2 ms). The cooperative budget cannot interrupt one step. Cause is consistent with first-use initialization but unproven.
- **Capture hitch.** Capture runs about 50 to 116 ms of synchronous game-thread work on capture frames (native readback 12-20 ms, encode 32-76 ms, response build 15-116 ms), far above the 2-ms ordinary allowance, at up to about one capture per second. The 100-ms cooperative deadline refused one of 36 diagnostic captures after release.
- **Capture memory and scale.** Resident memory rose in steps over 12 captures per scene with no plateau proof; native and GC reclamation unmeasured. UI scale other than 1 and scale change during capture not attained. A longer capture series and a diagnostic confirming no native allocation on refusal remain open.
- **Load resources.** Resident memory grew about 34 MB during the first finite load cycle and then plateaued; that is not reclamation and not a bound. Internal limit gauges exist only on the diagnostic build. Byte-flood saturation is unreachable on Mono (large requests are latency bound), so byte-quota rejection is host proven only.
- **Intermittent client refusals.** A CLI no-reply with a fixed session error appeared during map load and once after restart (cause unproven). A single paused second-page refusal once was not reproduced in later launches; cause unproven.
- **Developer-visible limits.** Unaudited and modded types can omit a route or family; availability is not readiness.

### Orchestrator decisions carried

- **Screenshot hitch is documented, not fixed.** All budgets and deadlines unchanged; the capture-budget scenario stays unmet.
- **First-use overrun is documented, not relabeled.** The bounded-load scenario stays unmet.
- **Map-view screenshots use a weaker documented proof** (frame advanced, fence passed, safety, scope and size clean across hold and release). Whole-frame provenance holds for menu capture only.
- **Fence release is held then released** on a later frame; nothing is released on the fence alone.
- **Boundary samples have their own fixed allowance** so producer work cannot silently expire retained results.
- **Things and resources keep the raw-cap refusal** (no larger scan, no narrower replacement query without an explicit contract).
- **Everything is a divergence or limitation, not a waiver and not a record claim.**

### Post-hoc harness corrections accepted (flag for review)

- **Load/menu lifecycle and quit/restart cases were ticked after harness-check corrections.** The original case verdicts stay failed. Corrected expectations are in the external recipe, with offline re-evaluation of retained samples. Review that the corrections did not weaken any check.
- **Reload-window no-reply** was accepted as an expected fail-closed refusal from the CLI's short discovery allowance during a map-load stall. The product made no wrong observation; why the auth reply sometimes exceeds that allowance is unproven.
- **Quit/restart findings** that failed on wrong harness expectations (the registry also holds the product's private workspace; a re-pointed old record makes the CLI refuse the whole scan) were corrected after proof from retained data.
