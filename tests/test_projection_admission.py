"""Source guards supplement host admission tests; no game/runtime proof."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent.parent


class ProjectionAdmissionTests(unittest.TestCase):
    def test_all_six_installed_harmony_categories_checked_without_owner_bypass(self):
        source = (ROOT / "mod/Observation/GameProjectionAdmission.cs").read_text()
        for category in ("Prefixes", "Postfixes", "Transpilers", "Finalizers", "InnerPrefixes", "InnerPostfixes"):
            self.assertIn(f"info.{category}.Count", source)
        self.assertIn("Harmony.GetPatchInfo(method)", source)
        for forbidden in ("info.Owners", ".PatchMethod", ".GetMethod(original)", "method.Invoke("):
            # Comments may explain prohibited APIs; executable statements must not use them.
            executable = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("//"))
            self.assertNotIn(forbidden, executable)

    def test_needs_reader_mirrors_normal_tab_without_mutating_getters(self):
        source = (ROOT / "mod/Observation/GameNeeds.cs").read_text()
        executable = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("//"))
        # Separate mood pane is the exact tracker.mood object, not every hidden need.
        self.assertIn("ReferenceEquals(tracker.mood, need)", source)
        self.assertNotIn("NeedDefOf.Mood", executable)
        for caller in ('"DoNeedsMoodAndThoughts"', '"DoMoodAndThoughts"', '"UpdateDisplayNeeds"', '"SortInDisplayOrder"',
                       'Getter(typeof(ITab_Pawn_Needs), "IsVisible")', "DrawOnGui(typeof(Need_Mood))"):
            self.assertIn(caller, source)
        # Food stays in the family through the cache read or field mirror, never a stat worker call.
        self.assertIn("Kind.Food", source)
        self.assertIn("NeedsProjection.CacheFresh", source)
        for forbidden in (".DrawOnGUI(", ".GetTipString(", ".CurInstantLevel", ".NeedInterval(", ".GetStatValue(",
                          ".LabelCap", ".CapitalizeFirst(", ".MaxLevel", ".CurLevel", ".CurLifeStage", "pawn.BodySize",
                          "GenesListForReading", ".GetValue(", ".Worker", ".GetRole(", "NeedsCardUtility.", ".TryGetNeed(",
                          ".ShowOnNeedList", ".RaceProps", "cache[", "cache.Add", "cache.Remove", ".SetInitialLevel"):
            self.assertNotIn(forbidden, executable)
        # Patch checks precede any read; unknown classes are refused before any getter.
        self.assertLess(source.index("!Fresh()"), source.index("pawn.needs"))
        self.assertLess(source.index("kinds.TryGetValue(need.GetType()"), source.index("Shown(kind, need"))

    def test_health_reader_mirrors_normal_card_without_mutating_getters(self):
        source = (ROOT / "mod/Observation/GameHealth.cs").read_text()
        executable = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("//"))
        # Normal live-pawn caller and card are patch-checked, never called.
        for caller in ('Method(typeof(ITab_Pawn_Health), "FillTab")', '"ShowBloodLoss"', '"VisibleHediffs"',
                       '"DrawHediffRow"', '"CacheMissingPartsCommonAncestors"', '"GetListPriority"'):
            self.assertIn(caller, source)
        # Missing parts come from the pure common-ancestor walk, compared with any built cache.
        self.assertIn("HealthProjection.CommonAncestors", source)
        self.assertIn("cachedAncestors(set)", source)
        for forbidden in (".LabelCap", ".LabelBase", ".LabelInBrackets", ".UIGroupKey", ".CurStage", ".Visible",
                          ".Translate(", ".GetMissingPartsCommonAncestors(", "SummaryHealthPercent", ".GetTooltip(",
                          "HealthCardUtility.", ".IsFresh", ".FullyImmune", ".IsTended", ".IsPermanent", ".Severity)", ".Severity;", ".Severity ",
                          ".BleedRate", ".PainOffset", "showAllHediffs", ".DirtyCache(", ".LoadData(", "keyedReplacements[",
                          # Formatted labels and level/addiction/dependency/hangover overrides are mirrored.
                          "GrammarResolverSimple.Formatted(", "().Formatted(", ".Worker", ".ActiveLanguageWorker", ".LabelShort", ".Active", ".LinkedGene",
                          ".GenesListForReading", ".IsGhoul", ".AgeBiologicalYears", ".TryGetNeed(", ".CurCategory",
                          ".CurLevel", ".HasHediff(", ".ToStringPercent(", ".Label;", ".Label)"):
            self.assertNotIn(forbidden, executable)
        # Patch checks precede any read; every hediff/comp class is admitted before any label.
        self.assertLess(source.index("!Fresh()"), source.index("pawn.health"))
        self.assertLess(source.index("kind == null"), source.index("entries.Add("))
        self.assertLess(source.index("HealthProjection.Fate("), source.index("entries.Add("))

    def test_gear_reader_mirrors_gear_tab_without_label_or_stat_getters(self):
        source = (ROOT / "mod/Observation/GameGear.cs").read_text()
        executable = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("//"))
        for caller in ('"IsVisible"', '"FillTab"', '"DrawThingRow"', '"LabelExtras"', '"NewThingLabel"', '"GetValueUnfinalized"'):
            self.assertIn(caller, source)
        # Labels, quality, style, hit points and stats are mirrored; inventory contents never read.
        for forbidden in (".LabelCap", ".LabelNoCount", ".Label;", "thing.MaxHitPoints", ".HitPoints", ".GetStatValue(", ".TryGetQuality(",
                          ".GetLabel(", ".StyleDef", ".TransformLabel(", ".GetTooltip(", "GenLabel.", ".CurLifeStage", ".IsAnomalyEntity",
                          ".IsDrone", ".FleshType", ".Quality;", ".WornByCorpse", ".LabelAsStuff", ".GetValue(", ".InnerListForReading",
                          "innerContainer[", "foreach (Thing item in", ".Translate("):
            self.assertNotIn(forbidden, executable)
        self.assertLess(source.index("!Fresh()"), source.index("pawn.ageTracker"))
        self.assertLess(source.index("work.TryNestedFields(cost)"), source.index("LabelCap(thing, game)"))

    def test_jobs_mirror_exact_providers_and_never_fallback_from_unknown_lord(self):
        source = (ROOT / "mod/Observation/GameJobs.cs").read_text()
        executable = "\n".join(line for line in source.splitlines() if not line.lstrip().startswith("//"))
        for forbidden in (".GetReport(", ".GetJobReport(", ".GetInspectString(", ".GetJobReportOverride(",
                          ".BehaviorForRole(", ".RoleForPawn(", ".Translate(", ".LabelShort;", ".jobQueue", "MakeNewToils", "SetupToils"):
            self.assertNotIn(forbidden, executable)
        for required in ('"ShouldDisplayLordReport"', '"ShouldDisplayJobReport"', '"GetJobReportOverride"',
                         '"ReportStringProcessed"', '"TryResolveSymbol"', "JobProjection.JobShown", "JobProjection.LordShown",
                         "gate != TargetGateResult.Eligible", "gear.LabelCost", "queue.Count > MaxQueue", "members.Count > 128"):
            self.assertIn(required, source)
        self.assertLess(source.index("Preferred(provider, pawn, work"), source.index("Driver(pawn, map, game, work"))
        self.assertIn("if (report == null)", source)  # Empty preferred report must not fall back.
        self.assertIn('!= typeof(LordJob_Ritual)) throw Refuse()', source)
        detail = (ROOT / "mod/Observation/GameOwnPawns.cs").read_text()
        self.assertIn("if (read != FamilyRead.Value) { job = null; lord = null; }", detail)
        self.assertIn("shownNeeds, rows, items, job, lord", detail)

    def test_admission_does_not_enable_routes_or_publish_object_graphs(self):
        dispatch = (ROOT / "mod/Bridge/ReadDispatcher.cs").read_text()
        bootstrap = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        self.assertNotIn("GameNeedsReader", dispatch)
        # Needs only reach the wire through pawn detail; a failed binding leaves it omitted.
        self.assertIn("new GameOwnPawnProducer(access, reader, needs, health, gear, jobs)", bootstrap)
        self.assertNotIn("GameGearReader", dispatch)
        self.assertNotIn("GameHealthReader", dispatch)
        self.assertNotIn("GameNeedsReader", (ROOT / "mod/Observation/GameSummary.cs").read_text())
        primitive = (ROOT / "mod/Observation/PrimitiveProjection.cs").read_text()
        self.assertNotIn("using Verse", primitive)
        self.assertNotIn("using Newtonsoft", primitive)
        self.assertNotIn("object ", primitive)
        needs = (ROOT / "mod/Observation/NeedsProjection.cs").read_text()
        self.assertNotIn("using Verse", needs)
        self.assertNotIn("using RimWorld", needs)

    def test_audit_retains_required_pending_baseline(self):
        audit = (ROOT / "docs/projection-admission.md").read_text()
        for family in ("food", "mood", "health", "gear", "job", "lord", "animals", "mechs", "mutants"):
            self.assertIn(family, audit)
        self.assertIn("No baseline family is complete", audit)
        self.assertIn("Unverified", audit)
        self.assertIn("Unsupported", audit)
        self.assertIn("empty", audit)


if __name__ == "__main__":
    unittest.main()
