# Exact primitive projection admission

**Building blocks, not observation routes. No baseline family is complete until real-UI proof.** `mod/Observation/` supplies exact admission and primitive limits for adapters. The shown-needs family now has its own reader in pawn detail ([visibility-audit.md#shown-needs](visibility-audit.md#shown-needs)); it replaced the earlier rest/mood-only building block. Host tests and net472 compilation are not Unity Mono, normal-UI or full-profile runtime proof.

## Gate order and ownership

Trusted game-thread producer must first establish authentication, readiness, developer safety, current scope, accessible loaded map and normal own-pawn/needs-card context. Admission does not supply those witnesses. Future producers must share one session/update deadline and candidate/nested-field/byte budget, preserve cancellation and use `ObservationEpochs.Release` after fresh whole-result eligibility/safety checks. A primitive reader is not permission to publish a partial family or reuse an old lease.

`AdmissionRecipe.Check` then requires:

1. Exact runtime type, exact definition type and the admitted definition **object reference**. Neither assembly membership, inheritance, a matching definition name nor `Def.Equals` grants permission.
2. Exact ordered component types, cardinality and component-definition references. Extra/derived/reordered/replaced components require a new audit. Evidence contains actual component objects; no component getter is evaluated to discover whether it is safe.
3. A nonempty finite explicit method closure, fixed when the recipe is built: exact getter/report implementations, normal UI inclusion, label helpers/constructors and relevant visibility/provider dependencies. A derived provider cannot select a familiar base method as its audit. The infrastructure performs no virtual invocation, arbitrary property enumeration, reflection serialization or report evaluation.
4. Unpatched status for **every** method through installed Harmony metadata. The audited patch allowlist is currently empty. All six installed categories count: prefixes, postfixes, transpilers, finalizers, inner prefixes and inner postfixes. No owner, game/mod assembly, priority or patch name is blanket-trusted. Failures/missing evidence refuse admission. Patch methods/factories are never resolved or executed for inspection.

Only then may a recipe-specific reader evaluate audited fields/getters; release still belongs to its producer. Definition/configuration fields that affect a reader must be checked explicitly, not treated as immutable because their reference matches. Recipe arrays are copied and capped at 16 components/32 methods. Primitive labels cap at 512 UTF-16 units/2048 UTF-8 bytes, reject invalid UTF-16 and never truncate. A shared local sub-budget caps operations/bytes and remains exhausted after failure; this is not the later shared producer scheduler.

## Source-audited initial projection

Installed source: RimWorld 1.6.4871 rev600. Symbol anchors describe behavior without distributing proprietary source.

| Symbols | Read/mutation audit and admission consequence |
| --- | --- |
| Needs (`Need` subclasses, `NeedsCardUtility`, `Need_Food.MaxLevel`/MaxNutrition stat closure) | Audited and implemented by `GameNeedsReader`; see [visibility-audit.md#shown-needs](visibility-audit.md#shown-needs). Drawing, tooltips, instant mood, need intervals, `Def.LabelCap` and stat workers are never invoked. |

`NeedBarPrimitive` contains only a bounded string and finite fraction. Invalid current/maximum, zero maximum, negative or overfull levels are unverified, never synthesized healthy/zero values. Actual zero/full valid bars remain values. No pawn, definition, component, target, delegate or exception object enters this primitive.

## Visibility and other field closures

The source audit in [visibility-audit.md](visibility-audit.md) still applies. `ThingSelectionUtility.SelectableByMapClick` permits any unfogged footprint cell; uninitialized/out-of-bounds fog can return false. Hidden-from-player semantics preserve own invisible pawns, unlike psychological invisibility. None of this authorizes an arbitrary non-pawn/DLC concealment provider or held contents. Exact type/component/provider admission precedes labels/counts/references; map/fog/concealment adapters remain subsequent tasks.

Normal labels are not harmless generic reads: `ThingWithComps.Label` invokes transforms, `GenLabel.ThingLabel` can read max-HP/stat paths and presentation caches, and pawn labels have separate name/identity closure. No generic thing/gear/pawn label fallback is installed. Health visibility/labels invoke stages/comps; missing ancestors use a cache-building projection, and normal live health shows blood loss. Keep equivalent nonmutating health rows and explicit component admission as required work. Public job/lord gates and provider/target label closures are independent; queued targets or generic inspect strings never substitute for an unaudited report.

## Full solo profile patch scrutiny

Private inspection covered the enabled solo profile without excluding a mod: Harmony, core plus all five DLCs, BuilderTweaksReplica, NicePlantsMenu (including its shipped Multiplayer API library), StrictNoInsects and Undo. Installed selected 1.6 assemblies were decompiled and their patch registration/target code inspected. Observed targets include designator/input/build/zone hooks, plant command input, insect/spawn/incident filtering and Undo's `UIRoot_Play.UIRootOnGUI` postfix. No needs getter/card/definition-label patch was found in those inspected registration paths. This is a source finding, **not a runtime patch inventory** or universal safety claim. Broader GUI hook ordering remains an epoch/render proof obligation.

`GameProjectionPatchAudit` checks actual current metadata for every method in each recipe, including other mods, later-installed patches and this bridge's own patches. New patch presence refuses the recipe even if its owner appeared in the source audit. Installed `Harmony.GetPatchInfo` and `Patches` source expose all six categories and copy collections; `Patch.GetMethod` can invoke a factory, so this implementation never calls it. GetPatchInfo's internal copying has no bounded pre-copy count API. Finite closure length and before/after budget checks do not bound that internal allocation/latency or serialize concurrent detours/direct field writers. Producer integration and isolated Mono checks must address staging, final revalidation and useful work bounds; do not assert sandboxing against uncooperative in-process code.

## Gaps are not empty results

`ProjectionResult` is private: Value, Excluded (known normal-context omission), Unverified and BudgetExceeded. Do not serialize rejection causes, hidden types/counts or a per-target capability diagnosis. A family gap is policy/profile-level metadata established independently of encountered hidden candidates, never inferred from filtered populations. Unknown target still uses uniform unavailable handling. Partial eligible prefixes are not complete lists/totals.

`ObservationPolicy` keeps every approved baseline family **Unverified** until its producer is completed. Only best-effort rendered controls remain **Unsupported** under existing policy. Neither status is an audited empty result. Required pending work includes session/map summaries, own-pawn identity/status across colonists, animals, mechs and mutants, food/mood/other shown needs, visible health, equipped/worn gear, public current job/lord reports, eligible things/resources, selection/known windows and public notifications. All existing later adapter tasks remain unchecked. This infrastructure neither narrows baseline to supported primitive examples nor changes acceptance criteria.

## Verification boundary

Host C# tests exercise exact type/definition/component/provider/patch refusal, every patch category, no getter/equality invocation during admission, fresh patch checks, finite operations/deadlines/bytes, Unicode, primitive fractions, normal mood-pane exception and policy gap versus empty semantics. Python source guards retain all installed patch categories, before/after getter checks and no new route or generic formatter. Configured net472 compilation checks actual installed signatures. Normal UI comparisons, actual Harmony state/delegate binding, full-profile Mono behavior, hidden-state equivalence and useful bounded producers remain later tests. No game launched for this unit.
