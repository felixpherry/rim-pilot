# Normal-player visibility contract (M1)

Normative eligibility rules for future observation DTOs, not an implemented API or a game-test pass. [Source audit](../docs/visibility-audit.md) distinguishes inspected behavior from remaining proof. Field names below identify semantic slots; wire envelopes, numeric budgets and capability schemas are separate contracts.

## Scope and default denial

M1 is authenticated local **observation only**, at menus, pause and live play. Normal-player inspectability, not camera position, bounds structured facts. Reads must not move camera, select, hover, open tabs, pause, generate maps or change gameplay/UI. Developer mode, known developer-only panels and unsafe lingering debug-visibility flags prohibit observations. [Publication/developer/safe-frame contract](observation-safety.md) and [finite safety manifest](developer-safety-v1.json) define these gates and explicit UI/aggregate absences. Never toggle those settings to make a request succeed.

This applies to Linux/RimWorld 1.6 and all five DLCs. Loading Royalty, Ideology, Biotech, Anomaly or Odyssey does not authorize every DLC getter. Specialist schemas remain explicit gaps, not hidden-state fallbacks. Controls, grants, raw UI input, co-play and full-game qualification belong to later milestones. Screenshots show already rendered screens; this contract does not authorize scraping unopened UI or promising a complete accessibility tree.

Only explicit primitive DTO fields may leave the game thread. No game object serialization, reflection/property enumeration, generic inspect strings, private AI intent, future incidents, debug data or arbitrary custom getters. Unknown fields stay absent. Empty means an audited query found no eligible values; unsupported/unaudited means unknown, never zero or healthy. Capability gaps describe a policy/type family, not the presence of a particular hidden entity or condition.

## Eligibility order

Apply these gates before labels, references, counts, sorting, pagination or events:

1. Authenticate and verify expected session. On the main thread, recheck readiness, cancellation and developer restrictions at execution time. `AnyEventNowOrWaiting`, invalid roots or scene transitions mean not-ready, even when simulation ticks are stopped.
2. Resolve bridge-owned game/root/map generations by object identity. A map must belong to the current loaded game, be initialized and be inspectable through normal player map access. Loaded alone, `IsPlayerHome` alone and a reused numeric ID are not authorization. Do not require the human's current map or switch maps to query another eligible loaded map.
3. Validate target ownership/parent chain, supported type, map membership, position and occupied rectangle. For map things, require an existing directly spawned target on that map. A container's visible position does not expose its contents. Unspawned/contained targets need a separately audited inspection route; none is implied here.
4. Establish a valid, initialized fog source and in-bounds occupied cells **before** reading fog. A false fog result for an invalid cell or uninitialized grid is not evidence of visibility.
5. Match normal map-selection fog semantics: one-cell targets need their cell unfogged; multi-cell targets need **any** valid occupied cell unfogged, not necessarily their origin and not every cell. Require the supported type's normal selectability and player-facing concealment checks too. This reveals the eligible object only, not other contents of its fogged footprint.
6. For pawns, use normal **hidden-from-player** semantics, not psychological invisibility alone. Player-owned invisible pawns can remain inspectable; a debug override allowing hidden pawns must never authorize access. Non-pawn/DLC concealment requires its own audited adapter; unfogged/selectable alone is insufficient.
7. Apply the field/type rules below. Then create bounded primitive results. Revalidate visibility-dependent references at release; discard a snapshot that can no longer satisfy these gates.

Normal off-camera inspection is eligible without UI changes. Hidden, absent and unaudited targets use the same unavailable result, without names, type diagnoses, coordinates or hidden references. No raw scanned count/index, hidden-count difference, cursor position or detailed error may expose rejected candidates. Bound candidates examined as well as results returned. Lister add/remove hashes do not establish stable content or visibility; pagination needs bounded eligible snapshots or stronger proven invalidation.

## Type admission

An adapter's allowlist must identify **exact runtime type, relevant definition, component types, getter/report implementation and normal UI context**. Base-class inheritance, assembly membership, a public C# member, DLC activation or a familiar definition name is not blanket approval. Def replacements and third-party patches can invalidate an audit. Never invoke an unknown getter first and filter its output later.

| Type family | Admission rule | Default-denied expansion |
| --- | --- | --- |
| `Verse.Map` and spawned `Verse.Thing` | Loaded/player-inspectable map gates above; audited direct-spawn type/definition and label path. | World sites not loaded/accessible, map generation, arbitrary `ThingWithComps` getters, holder traversal, minified inner objects. |
| `Verse.Pawn` | Own colony pawn means `Faction.OfPlayer`, normal inspection eligibility and audited race/definition behavior. Includes supported colonists, animals, mechs and colony mutants; being undrafted, awake or mentally stable is not an ownership gate. | Prisoners, visitors, enemies and wild pawns are not own pawns merely because a player hosts/selects them. Their detail requires a separate audit. Caravans/transport/contained pawns are not silently mapped to a loaded-map position. |
| `Verse.Hediff`, `HediffWithComps`, missing-part types | Exact visible-row and label implementations plus all invoked comps must be audited. Missing parts follow their separate normal-card rule below. | Assuming `Hediff.Visible` makes every subclass/label/severity/tool-tip getter safe; unknown discovery/invisibility comps. |
| `RimWorld.Need` subclasses | Exact `ShowOnNeedList`, level/max and label/draw behavior must be audited against the normal needs card. | Every `AllNeeds` entry, custom tooltips, enabling-gene/condition explanations, private thought lists. |
| Job/lord report providers | Exact active driver/lord report implementation and every interpolated target label must be audited under the normal report gates. | Queued jobs, target objects/IDs, duties, paths, AI plans, arbitrary `GetInspectString` calls. |
| Equipped/worn things | Separate own-pawn gear inspection context; exact item/label paths audited. This is not general permission to enumerate holders. | Inventory/container contents and custom comp descriptions without an inspection audit. |

These are implementation admission rules, not a claim that the adapter registry exists. Required baseline fields still need implementation and normal-UI comparison; an explicit gap is honest reporting, not completion of that requirement.

## Field-level allowlist

All strings preserve localization/Unicode and obey protocol bounds. References are opaque bridge identities scoped to current generations, never raw engine IDs. Every list contains eligible entries only; omission must not reveal how many hidden entries were discarded.

| DTO field | Public type and normal-player source | Excluded data / extra gate |
| --- | --- | --- |
| Map `ref`, `label`, `size` | Scoped reference; audited player-facing map name; integer width/height for an eligible loaded map. | No file/save paths, hidden site contents, world-generation state or guessed labels from `ToString`. An unaudited name stays absent. |
| Thing `ref`, `kind`, `label`, `mapRef`, `position` | Scoped references; public kind/definition label and normal inspectable object label; integer x/z cell coordinates after object eligibility. | No internal class names as labels, hidden parent/inner refs or arbitrary inspect text. Visibility of one multi-cell target does not authorize a cell dump. |
| Thing `stackCount` | Nonnegative integer units of an eligible directly exposed stack; exact stack semantics audited. | No count from hidden/contained stacks or synthetic zero on audit failure. |
| Own pawn `ref`, `label`, `mapRef`, `position` | Same target gates; normal pawn name/label through an audited implementation. | No world-pawn search, unknown subclass label calls, secret identity/biography inference. |
| Own pawn `status` | Explicit booleans such as downed, dead or drafted only when matching normal inspection and audited state access. | No inferred diagnoses, mental AI state dump, raw summary-health percentage or hidden-condition-derived estimates. |
| Own pawn `health[]` | Normal health-card row label and body-part label (or explicit whole-body scope), after the health rules below. | No raw severity, immunity, age, cause, hidden diagnosis, severity thresholds, capacities, pain/bleeding totals or tooltips without their own audit. |
| Own pawn `needs[]` | Audited shown need label and finite `levelFraction = CurLevel / MaxLevel`, with positive finite max, matching the normal displayed bar. | `ShowOnNeedList` must be true and the normal card must admit that need. No raw `AllNeeds`, NaN/infinity, hidden need count, threshold/decay/internal source dump. |
| Own pawn `gear[]` | Audited equipped/worn item labels in the normal gear inspection context. | No generic inventory/holder traversal or private comp state. |
| Own pawn `currentJobReport` | Bounded localized text that normal inspect UI permits, following the independent job/lord gates below. | No raw job definition/report template, internal targets, future queue or unrestricted combined inspect string. |
| Resources `countBasis`, `mapRef`, `entries[]` | Required basis `eligible-observed-stack-units`; per-public-kind labels and nonnegative integer sums of eligible direct stacks from one consistent observation. | Not total colony wealth, all map resources or exact normal resource-bar counts. Never aggregate lister contents before eligibility. |

### Health rows and missing parts

Use the normal card with hidden-hediff/debug display disabled, independently of which tab the human currently has open. For non-missing conditions, require an audited visible predicate and the normal card's blood-loss display rule. Resolve the relevant normal inspection context's `showBloodLoss` policy; do not invent a client flag to reveal a suppressed row. Visibility alone does not authorize every member of that condition.

Missing-part rows use the normal card's **common missing ancestors** semantics, not a flat `Visible` filter: stop below an included missing ancestor and skip branches covered by directly added parts, matching the inspected implementation. Do not emit every missing child, double-count losses or reveal raw anatomy/hidden conditions via totals. Derive the eligible projection without invoking lazy cache-building helpers just to obtain it. If equivalence cannot be established, report that field family unsupported, not an empty healthy pawn.

Row labels may include public stage text only through audited label/component implementations. Do not call `SummaryHealthPercent`, raw health aggregates, debug/full health lists or health/need tooltip builders as shortcuts. A numerical value shown somewhere in UI still needs its own read-only getter audit; this contract authorizes only the listed baseline row fields.

### Shown needs and public current jobs

For needs, enumerate bounded candidates without adding/removing needs or initializing them on demand. Audit subclass overrides and actual normal-card inclusion in addition to the base `ShowOnNeedList` gate. An excluded need contributes neither an entry nor a count/error/capability hint about that pawn's hidden state. A supported empty needs list and an unsupported needs family must remain distinct.

For reports, independently honor `ShouldDisplayLordReport` and `ShouldDisplayJobReport`. Normal job-report resolution can prefer `LordJob.GetJobReport` over the active driver, even though the inspect UI gates lord summary text separately. Audit that full resolution path; a permitted job gate does not authorize every lord field. Combine only the allowed normal report fragments. If an interpolated target/provider is unaudited, omit the report with a family-level capability limitation. Never call generic pawn inspect text or fall back to queued-job reports/private target dumps.

### Labeled resource counts

M1 chooses **eligible observed stack units**, not the normal resource readout cache. Sum `stackCount` only after the direct-stack type/definition, resource-kind, map/fog/concealment and label gates. Group eligible stacks by an audited public resource kind, not private runtime type. Counts cover eligible directly spawned resource stacks regardless of stockpile membership or freshness; contained/minified inner resources are outside this basis. State that basis with every count result.

Normal resource-bar logic instead traverses haul destinations, unwraps minified things, excludes non-fresh contents and uses a periodically refreshed cache. Do not rename map-lister totals as resource-bar values, force `UpdateResourceCounts`, or call `GetCount` on unknown definitions (which can mutate/log). Do not publish cached all-resource aggregates unless separately audited against the same visibility boundary. Incomplete traversal is not a complete total: fail/expire or explicitly mark an eligible subset; never report a lower bound as an exact colony count.

## Proof obligations

Before claiming implementation, test positive off-camera/own-invisible/multi-cell cases and negative invalid-map/fog/hidden-condition/custom-type/contained cases from the [audit matrix](../docs/visibility-audit.md#required-implementation-checks). Compare supported fields to normal UI in an isolated authorized fixture. Apply the same boundary to totals, pages, errors, UI metadata and public-event references, not only entity serialization. No prior launch, source audit, metadata probe or pure model test counts as product or real-game acceptance.
