# Finite developer-safety sampler audit

This audits installed RimWorld 1.6.4871 rev600 source/metadata, not game execution or universal mod-debug detection. Private decompilations and assembly hashes stay outside public deliverables. `GameDeveloperSafety` implements named reads; `DeveloperSafetySampler` checks their completion under the dispatcher's existing shared update deadline. Registered state-bearing routes require this sampler plus fresh epoch and field/release admission; sampling alone cannot authorize them. The audited cached `RimWorld.VersionControl.CurrentVersionStringWithRev` getter must match manifest version `1.6.4871 rev600`; another build is incomplete/closed, not silently admitted by matching field names. This version guard is not a patch or assembly-integrity guarantee. Instantaneous safe evidence is not an epoch, render stamp, observation admission, or proof about full-profile patches.

## Known windows

Installed `Window_Dev` descends directly from `Verse.Window`. All its descendants are refused using type membership, without invoking virtual `IsDebug`, titles, content, or other window getters. Metadata ancestry was checked across all installed Window-derived types; source inspection established these developer families:

| Manifest type | Installed-source evidence |
| --- | --- |
| `LudeonTK.Window_Dev` | Common developer-window base; also covers Window_DebugTable, Dialog_OptionLister/debug option listers, Window_DevListing/noise tools, celestial/infection/music tools, EditWindow and DefEditor. |
| `LudeonTK.Dialog_Debug` | Direct Window_Dev subclass, debug actions/settings/output. |
| `LudeonTK.EditWindow_DebugInspector` | EditWindow -> Window_Dev. |
| `LudeonTK.EditWindow_TweakValues` | EditWindow -> Window_Dev. |
| `LudeonTK.EditWindow_Log` | EditWindow -> Window_Dev. |
| `LudeonTK.Dialog_DevPalette` | Direct Window_Dev subclass; no Verse type with this name. |
| `Verse.Dialog_DebugSetSeverity` | Direct ordinary Window subclass, developer health editing. |
| `Verse.Dialog_DebugSetHediffRemaining` | Direct ordinary Window subclass, developer health editing. |
| `Verse.Dialog_PawnTableTest` | Direct ordinary Window subclass, pawn-table test surface. |
| `RimWorld.Dialog_DebugRenderTree` | Direct ordinary Window subclass, pawn render-tree debugging. |
| `Verse.Dialog_CameraConfig` | Direct ordinary Window subclass, developer camera configuration. |
| `Verse.Dialog_CameraConfigList` | Dialog_FileList subclass; covers camera configuration load/save subclasses. |

The original four named LudeonTK descendants and palette remain explicit for manifest continuity, though Window_Dev subsumes them. Unknown ordinary windows are not permission to inspect their internals; unknown third-party developer windows cannot be generically identified safely.

Installed `WindowStack.Count` and its indexer are nonvirtual direct List count/index reads. `Windows` creates a read-only wrapper; sampler does not call it, copy the list, enumerate unbounded windows, or call any member on window objects. At most 64 indexed windows are examined. Missing/null entries, count changes, exceptions, loading, missing roots or exhausted deadline mean unknown/closed. Sampling runs only on its creating game thread, after bootstrap root/loading gates and inside the existing transition read fence. Uncooperative external mutation and patched accessor behavior still require later admission/compatibility audit.

## Named static baselines

- `Verse.DebugSettings`: all 46 installed mutable static booleans, with installed initializer values. Includes persistent hidden-info/pawn switches, search restrictions, god mode/palette, expanded role/build/order affordances, concealment rendering, gameplay cheats and diagnostics. This deliberately conservative policy also refuses nonnormal unrelated developer settings; it does not reset them.
- `Verse.DebugViewSettings`: all 133 installed mutable static booleans, with installed initializer values. Normal render layers remain true, debug overlays/write/log/save switches remain false. Logging and single-threaded/debug speed settings are included conservatively rather than guessed harmless under later screenshots/UI reads.
- `Verse.DebugGenerationSettings`: both prefab-copy booleans, false.
- `RimWorld.HealthCardUtility.showAllHediffs` and `showHediffsDebugInfo`: private persistent static booleans, false. `VisibleHediffs` admits normally hidden rows when the former is set; health-row tooltip construction uses the latter. Developer health buttons toggle them. `Prefs.DevMode` setter clears god mode/removes palette, not these flags.
- `LudeonTK.DebugTools.curTool` and `curMeasureTool`: both null. Installed `DebugToolsOnGUI` invokes active tool drawing independently of a DevMode check; tools must not survive as an assumed-safe ordinary overlay.

That is 183 booleans plus two null checks, exactly 185 indexed reads, below the 256 static-read ceiling. Every read has a literal compiled switch case; Python guards compare all cases, values and indices with the manifest. No runtime field enumeration or manifest-controlled getter selection occurs. `Prefs.DevMode` is separately read before and after sampling. Its installed getter returns unsafe true when preferences data is absent and otherwise reads its backing value; sampler never invokes its setter or Apply.

Public fields use direct reads. The two private health fields use **exact metadata binding only** (`DeclaredOnly`, nonpublic static, required Boolean type, nonliteral) to installed Harmony's cached static-field reference delegates. Binding happens once during metadata-only bootstrap, before registering the update hook; failure leaves the audit incomplete. Binding does not invoke either field delegate or read any game state. No `FieldInfo.GetValue`, arbitrary field read, reflection endpoint, property traversal, health aggregate, card drawing, or write occurs. HealthCardUtility is marked StaticConstructorOnStartup; its normal startup initialization includes content lookup, so field reads are gated out during loading. Delegate construction stays outside frame sampling. First-use JIT is not interruptible; an elapsed sampling deadline cannot produce safe evidence or further dispatched work. Finite managed work/deadline checks are not a hard wall-clock preemption guarantee. Actual Mono delegate binding and latency remain unverified.

## Test boundary and follow-up

Host tests exercise every indexed baseline mismatch/failure, thread refusal before source access, bounded windows/static reads, null entries, loading, source failures, shared deadline exhaustion and recovery without cached safe state. Source/manifest guards are not installed-source evidence by themselves; evidence above came from private installed-source/metadata inspection, and configured net472 compilation checks actual named public members/types. No game assembly is executed for these tests.

Private [epoch/boundary interfaces](observation-epochs.md) now revoke prior leases across unsafe/unknown samples and unobserved callback intervals, with refusal precedence and atomic primitive release checks. They do not prove an uninterrupted safe render. Later actual isolated Mono/normal-UI tests must prove private binding, persistent health/tool/window refusal and full-profile behavior. No baseline field, event or screenshot becomes available through this sampler alone.
