# M1 visibility source audit

Contract: [normal-player field/type rules](../protocol/visibility.md).

**Status:** source-supported visibility design, not implemented observation adapters. Mod/CLI bootstrap, host checks and configured compilation now exist; none establishes field visibility or real-game safety. Private inspection covered installed RimWorld 1.6.4871 rev600 and relevant selection, health, needs, report and resource APIs. Source symbols below locate the behavior without distributing proprietary code, local dependencies or private paths. Local decompilation line numbers are intentionally not public API identifiers.

The approved standalone plan and milestone scope require useful off-camera, player-knowable observations with no gameplay/UI changes. M1 does not authorize M2 controls, M3 co-play, M4 complete specialist coverage or M5 qualification. All five DLCs remain in scope; missing scenario/type evidence must remain visible as a gap. These documents establish the visibility boundary only. They neither implement the adapters nor satisfy baseline/acceptance requirements by marking fields unsupported.

## Findings applied to field rules

| Inspected source symbol | Source finding | Contract consequence / remaining proof |
| --- | --- | --- |
| `Verse.FogGrid.IsFogged` | Invalid cells or an uncreated grid can return false. | Validate loaded map, readiness, grid initialization and bounds first; `!Fogged` alone never authorizes a field. Actual lifecycle guard still needs implementation. |
| `RimWorld.ThingSelectionUtility.SelectableByMapClick` | Normal selection checks selectable definitions, player-hidden pawns, spawned parent and any unfogged occupied cell for a multi-cell object. | Match multi-cell visibility without requiring the origin/all cells visible; independently deny unaudited holder traversal and concealment types. Visible parent does not establish visible contents. |
| `Verse.Thing.SpawnedParentOrMe`, `MapHeld`, `PositionHeld` | Held objects can resolve their parent's map/position without being directly spawned. | Do not treat held coordinates as proof that a target is exposed. Direct map things and own-pawn gear need separate contexts. |
| `RimWorld.InvisibilityUtility.IsHiddenFromPlayer`, `IsPsychologicallyInvisible` | Player-owned invisible pawns can remain visible to the player; psychological invisibility is not the normal inspection rule. Debug hidden-pawn display bypasses concealment. | Preserve eligible own-pawn inspection; reject unsafe debug visibility. Do not generalize pawn semantics to every Anomaly or modded entity. |
| `RimWorld.HealthCardUtility.VisibleHediffs`, `VisibleHediffGroupsInOrder` | Normal card treats missing ancestors separately from ordinary visible conditions and applies its blood-loss display policy. Debug display can bypass filtering. | DTO health rows must match normal context, not a raw hediff list or `Visible`-only filter. Blood-loss caller context and actual row adapters still need verification. |
| `Verse.HediffSet.GetMissingPartsCommonAncestors`, `CacheMissingPartsCommonAncestors` | Common-ancestor traversal stops at missing parts and skips branches under directly added parts. Getter can populate a cache. | Reproduce the normal projection read-only; do not invoke cache construction or expose missing descendants. Compare replacement-part cases with normal card. |
| `Verse.Hediff.Visible`, `Label`; `HediffWithComps.Visible`, `LabelBase`, `LabelInBrackets` | Virtual visibility/labels can involve stages and component callbacks. | Audit exact getter/component closure before calling; no blanket approval of derived health types, custom labels or public severity fields. |
| `Verse.SummaryHealthHandler.SummaryHealthPercent` | Traverses conditions without the normal visible-row boundary and updates lazy cached values. | Exclude raw summary-health percentages and unaudited totals. A filtered list does not make an unfiltered aggregate safe. |
| `RimWorld.Pawn_NeedsTracker.AllNeeds`; `RimWorld.Need.ShowOnNeedList`, `CurLevel`, `MaxLevel`, `DrawOnGUI`, `GetTipString` | All needs are not all shown needs; virtual level/display paths and tooltip explanations expose more than the baseline bar. | Require normal-card inclusion plus audited shown-need getters; emit label/finite fraction only. Actual card caller/subclass coverage remains unverified. |
| `RimWorld.PawnUtility.ShouldDisplayJobReport`, `ShouldDisplayLordReport`; `Verse.Pawn.GetJobReport`, `GetInspectString` | Inspect UI gates job/lord reports separately; job resolution itself can prefer a lord provider. Generic inspect text invokes many other systems. | Audit provider resolution and target-label interpolation, emit allowed current public text only; no generic inspect call, queue, duty or target dump. |
| `RimWorld.ResourceCounter.UpdateResourceCounts`, `ShouldCount`, `ResourceCounterTick`, `GetCount` | Normal counts use haul destinations, freshness, minified inner objects and a periodic cache; unknown count lookup can insert/log. | Choose explicitly labeled eligible direct-stack units, not exact resource-bar totals; filter before summing and never refresh/mutate the normal cache. |
| `Verse.ListerThings.StateHashOfGroup`, `Add`, `Remove` | Add/remove bookkeeping does not track fog, concealment, stack or field changes. | Cannot certify a complete stable result with lister hash alone. Bound scanning, keep only eligible DTOs and revalidate/expire stale observations. |

## Required implementation checks

All rows below are **not run against product**. They define minimum follow-up checks, not an executable test suite or a completed test inventory.

| Case | Required observable result |
| --- | --- |
| Menu, loading, map removed/replaced, uninitialized grid, out-of-bounds target | No accidental permission from false fog; explicit not-ready/stale/unavailable as appropriate, with no hidden-target details. Reused engine IDs never resolve across generations. |
| Off-camera known object or own pawn on another normally accessible loaded map | Same eligible fields without changing human camera, map, selection, windows or paused state. Loading/accessibility is checked without exploring/generating a map. |
| Multi-cell origin fogged, another occupied cell unfogged; then all occupied cells fogged | First may qualify through normal selection; second unavailable. No unrelated fogged cell contents appear. Invalid rectangles fail closed. |
| Own psychologically invisible pawn; non-own concealed pawn; unsafe hidden-pawn debug flag | Own pawn remains inspectable if otherwise eligible; concealed target absent across output channels; unsafe debug state refuses observation rather than broadening access. |
| Visible casket/container/minified wrapper with hidden or unaudited contents | Wrapper visibility does not leak inner target labels, fields or counts. Own-pawn gear route does not authorize generic holder traversal. |
| Same public health rows, different hidden conditions | Health objects, counts, summaries, errors and events reveal no hidden-condition difference; no raw summary getter call. Public downed/drafted state can legitimately differ with normal play. |
| Missing ancestor plus missing children; branch replaced by added part; blood-loss contexts | Rows match normal health card without child duplication or lazy cache mutation. Unknown row/component paths stay absent with honest capability gaps. |
| Need in `AllNeeds` with false `ShowOnNeedList`; custom level/display/tooltip getter | No hidden need or derived source explanation. Unknown getter never invoked for filtering. Audited shown needs preserve localized label and finite normal bar fraction. |
| Mental-state/mutant job gates, lord-preferred job report, unknown driver or hidden target label | Only independently permitted current public report text; no queued reports, AI targets or fallback to generic inspect strings. |
| Eligible loose/storage stacks, non-fresh stack, minified contents and hidden stack | Labeled direct-stack basis includes only eligible direct resource stacks, including eligible loose/non-fresh ones; no inner/hidden units. No cache refresh or unknown-definition lookup side effects. |
| Stack/fog/concealment changes during scan or page read, many rejected candidates | Finite traversal/memory; consistent eligible result or explicit expiry/incompleteness. No raw hidden count/index in totals, pages, errors or events. |
| Unsupported DLC/modded class, component, patched getter or missing fixture | No inherited blanket permission and no invented pass. Record exact family/scenario gap; retain required baseline work until implemented and verified. |

Pure tests should cover gate order and output side channels, not merely repeat a mock fog rule. Later isolated normal-UI comparisons must exercise implemented adapters with the supported full solo profile. Prior source/metadata/parser probes and baseline isolated launches establish feasibility only. No game was launched and no save/settings or runtime state was changed for this contract task.

## Other phase-1 audit findings

SDK/runtime compatibility, Harmony discovery, hardened JSON ingress, wire/identity/cursor budgets, accepted-public notification hooks, developer-surface enumeration and completed-safe-frame capture remain separate implementation tasks. They are not implemented by these documents. Their visibility obligations are prerequisites or consumers of this field boundary; this audit does not approve arbitrary window internals, developer observations or unaudited screenshot timing.
