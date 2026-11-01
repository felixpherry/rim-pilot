"""Installed-source-backed wiring guards, not executable Verse/Mono acceptance.

Audited symbols and host/runtime evidence boundary: coverage/observation-adversarial.md.
No proprietary source fixtures are required or distributed by these tests.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def source(name):
    return (ROOT / name).read_text()


class ObservationRegressionTests(unittest.TestCase):
    def test_target_gate_does_not_replace_inspectability_with_not_fogged(self):
        text = source("mod/Observation/GameTargetAccess.cs")
        gate = text[text.index("internal TargetGateResult Thing("):text.index("internal bool OwnPawn(")]
        for prerequisite in ("!types.Admits(thing)", "!MapAccessible(map)",
                             "!TargetGate.DirectSpawnHolder(map.spawnedThings, thing.holdingOwner)", "!def.selectable",
                             "TargetGate.Pawn(", "work.TryNestedFields(cells)"):
            self.assertLess(gate.index(prerequisite), gate.index("TargetGate.Footprint("))
        self.assertNotIn("SpawnedParentOrMe", gate)
        self.assertNotIn("IsHiddenFromPlayer(", gate)
        self.assertNotIn("IsFogged(", gate)

    def test_direct_map_container_is_witnessed_including_excluded_members(self):
        text = source("mod/Observation/GameTargetAccess.cs")
        self.assertIn("Map = map, MapHolder = map.spawnedThings", text)
        self.assertIn("!ReferenceEquals(witness.Map.spawnedThings, witness.MapHolder)", text)
        adapter = source("mod/Observation/GameListSource.cs")
        self.assertEqual(2, adapter.count("access.Capture(thing, Player, map)"))
        self.assertNotIn("thing.holdingOwner != null) return TargetGateResult.Unavailable", text)

    def test_expired_first_roster_page_rebuilds_not_rebases(self):
        text = source("mod/Observation/GameOwnPawns.cs")
        self.assertIn("if (revalidated == Revalidated.Expired) return RestartRelease(work)", text)
        self.assertIn("if (paging) return work.Fail(WireError.CursorExpired)", text)
        self.assertIn("return work.Failure.HasValue ? false : work.RestartStep()", text)
        self.assertIn("if (++restarts > MaxRestarts)", text)
        self.assertIn("source = null; pass = null; result = null", text)

    def test_roster_projection_yields_before_whole_vector_revalidation(self):
        text = source("mod/Observation/GameOwnPawns.cs")
        step = text[text.index("public StepResult Step("):text.index("private bool Bind(")]
        build = step.index("if (!Build(work)) return StepResult.Continue;")
        revalidate = step.index("switch (work.Generations.Snapshots.Revalidate")
        self.assertIn("return StepResult.Continue;", step[build + len("if (!Build(work)) return StepResult.Continue;"):revalidate])

    def test_ui_selection_retention_yield_rebuilds_whole_result(self):
        text = source("mod/Observation/GameUi.cs")
        self.assertIn("if (id == null) return work.Fail(WireError.Unavailable)", text)
        self.assertIn("if (!Retain(work, selection, UiProjection.Selection(label!, id, own))) return false", text)
        self.assertNotIn("id == null || !Retain(", text)
        self.assertIn("result = null; work.ReleaseMemory();", text)
        self.assertIn("if (!eligible && work.Yielded) return", source("mod/Bridge/ReadDispatcher.cs"))

    def test_summary_counts_share_eligibility_not_raw_list_length(self):
        text = source("mod/Observation/GameSummary.cs")
        self.assertIn("owner.access.OwnPawn(pawn, source.Player)", text)
        self.assertIn("witnessed && gate == TargetGateResult.Eligible) count.Count++", text)
        self.assertIn("count.Done && !count.Omitted ? count.Count : (int?)null", text)
        self.assertIn("!count.Consistent(work.Frame!)", text)
        self.assertLess(text.index("work.TryCandidate()"), text.index("Pawn pawn = source.List[count.Index]"))

    def test_hidden_health_labels_only_materialize_after_mirrored_visibility(self):
        text = source("mod/Observation/GameHealth.cs")
        self.assertIn("hediff is Hediff_MissingPart || concealed.Contains(hediff) || !Visible(hediff, context)) continue", text)
        self.assertLess(text.index("!Visible(hediff, context)) continue"), text.index("entries.Add(Entry(hediff, context, parts))"))
        self.assertIn("kind == null", text)
        self.assertIn("CompKinded(comp.GetType()) == null", text)
        self.assertNotIn("hediff.Visible", text)
        self.assertNotIn("hediff.LabelCap", text)
        self.assertNotIn(".SummaryHealth", text)
        self.assertNotIn(".GetMissingPartsCommonAncestors(", text)

    def test_hidden_health_conditions_never_change_field_presence(self):
        # P4-13-R1: refusing the family for an unadmitted class BEFORE the visibility filter made
        # family presence a hidden-condition oracle (LungRotExposure, PsychicBondTorn).
        text = source("mod/Observation/GameHealth.cs")
        start = text.index("// Admission plus the added/missing indexes")
        end = text.index("List<Hediff_MissingPart>? ancestors", start)
        admission = text[start:end]
        # Concealment is decided before admission and before any comp is looked at.
        self.assertIn("HealthProjection.Fate(", admission)
        self.assertLess(admission.index("HealthProjection.Fate("), admission.index("fate == HealthFate.Refuse"))
        self.assertLess(admission.index("HealthFate.Skip"), admission.index("CompKinded(comp.GetType()) == null"))
        self.assertIn("concealed.Add(hediff)", admission)
        # Exact audited mirrors of the two classes that override visibility.
        self.assertIn("Kind.RotStink", text)
        self.assertIn("Kind.Never", text)
        self.assertIn("HealthProjection.RotStinkHidden(", text)
        self.assertIn('Getter(typeof(Hediff_RotStinkExposure), "Visible")', text)
        self.assertIn('Getter(typeof(Hediff_PsychicBondTorn), "Visible")', text)
        # A concealed condition is skipped before any label or entry is built.
        self.assertIn("concealed.Contains(hediff) ||", text)
        self.assertLess(text.index("concealed.Contains(hediff) ||"), text.index("entries.Add(Entry(hediff, context, parts))"))
        # Visible-state-driven unknowns stay refused: the shape gate requires the base members.
        self.assertIn("ConcealableShape(", text)
        for member in ('"Visible"', '"CurStage"', '"CurStageIndex"', '"Severity"'):
            self.assertIn(member, text[text.index("private bool ConcealableShape"):])

    def test_only_exact_noncombat_wait_uses_audited_base_report(self):
        text = source("mod/Observation/GameJobs.cs")
        self.assertIn('M(typeof(JobDriver_Wait), "GetReport")', text)
        self.assertIn('JobProjection.WaitUsesBase(driver.GetType() == typeof(JobDriver_Wait)', text)
        self.assertIn('owner == typeof(JobDriver_Wait), current.def == JobDefOf.Wait_Combat)', text)
        self.assertNotIn('pawn.WorkTagIsDisabled(', text)
        self.assertNotIn('driver.GetReport(', text)

    def test_gear_immutable_stat_cache_is_read_not_rejected_or_filled(self):
        text = source("mod/Observation/GameGear.cs")
        self.assertIn('("immutableStatCache")', text)
        self.assertIn("if (stat.immutable)", text)
        self.assertIn("permanent.TryGetValue(thing, out float cached)", text)
        self.assertIn("GearProjection.CachedHitPoints(cached)", text)
        self.assertNotIn("|| stat.immutable)", text)
        self.assertNotIn("permanent[", text)
        self.assertNotIn("stat.Worker", text)
        self.assertIn("worker == null || worker.GetType() != typeof(StatWorker)", text)

    def test_plain_weapon_art_comp_has_exact_no_transform_branch(self):
        text = source("mod/Observation/GameGear.cs")
        self.assertIn("type == typeof(CompArt) && label == typeof(CompArt) ? CompKind.Art", text)
        self.assertIn('GameKeyedText.Getter(typeof(CompArt), "Active")', text)
        self.assertIn('Method(typeof(CompArt), "TransformLabel", typeof(string))', text)
        self.assertIn('GameKeyedText.Getter(typeof(Thing), "StyleSourcePrecept")', text)
        self.assertIn('GameKeyedText.Getter(typeof(CompStyleable), "SourcePrecept")', text)
        self.assertIn("case CompKind.Art:", text)
        self.assertIn("sourcePrecept(thing.compStyleable) != null) throw Refuse()", text)
        self.assertIn('DeclMethod(type, "GetStatOffset") == typeof(ThingComp)', text)
        self.assertNotIn("comp.Title", text)
        self.assertNotIn("comp.Active", text)

    def test_visible_disappearing_health_uses_bounded_timer_mirror(self):
        text = source("mod/Observation/GameHealth.cs")
        self.assertIn("HealthProjection.DisappearingTime(timer.ticksToDisappear", text)
        for member in ("EffectiveTicksToDisappear", "TicksLostPerTick", "ToStringSecondsFromTicks", "ToStringTicksToPeriod", "TicksToSeconds"):
            self.assertIn('"' + member + '"', text)
        self.assertNotIn("disappears && !disappears.showRemainingTime ? null : throw Refuse()", text)
        self.assertNotIn("timer.CompLabelInBracketsExtra", text)

    def test_job_targets_keep_selectability_and_no_generic_label_fallback(self):
        gate = source("mod/Observation/GameTargetAccess.cs")
        self.assertIn("if (def == null || !def.selectable) return TargetGateResult.Unavailable", gate)
        text = source("mod/Observation/GameJobs.cs")
        start = text.index("Thing thing = target.Thing;")
        end = text.index("if (target.Thing == null) args[i]", start)
        target = text[start:end]
        self.assertLess(target.index("access.Thing("), target.index("gear.LabelShort("))
        self.assertIn("if (gate != TargetGateResult.Eligible) throw Refuse()", target)
        self.assertIn("else throw Refuse()", target)
        for call in ("thing.Label", "thing.GetInspectString", "jobQueue", "filth.Label", "filth.sources", "filth.thickness"):
            self.assertNotIn(call, text)

    def test_cleaning_public_target_path_is_exact_and_separate_from_selection(self):
        text = source("mod/Observation/GameJobs.cs")
        self.assertIn("driver.GetType() == typeof(JobDriver_CleanFilth) && i == 0", text)
        self.assertIn("thing.GetType() == typeof(Filth)", text)
        self.assertIn("access.ReportFilth(thing, map, game, work)", text)
        self.assertIn("JobProjection.PlainLegacyTarget(thing.def.label)", text)
        self.assertIn('template.IndexOf("{" + name, StringComparison.Ordinal) >= 0', text)
        gate = source("mod/Observation/GameTargetAccess.cs")
        start = gate.index("internal TargetGateResult ReportFilth(")
        end = gate.index("// Own colony pawn", start)
        report = gate[start:end]
        for prerequisite in ("thing.GetType() != typeof(Filth)", "def.GetType() != typeof(ThingDef)",
                             "def.thingClass != typeof(Filth)", "def.category != ThingCategory.Filth", "def.useHitPoints"):
            self.assertIn(prerequisite, report)
        self.assertIn("DirectFootprint(thing, map, game, work)", report)
        self.assertNotIn("Reference(", report)
        self.assertIn("if (def == null || !def.selectable) return TargetGateResult.Unavailable", gate)

    def test_detail_regates_resolved_reference_before_every_field_family(self):
        text = source("mod/Observation/GameOwnPawns.cs")
        detail = text[text.index("work.Generations.ResolveEntity(request.Scope"):]
        gate = detail.index("owner.access.Thing(pawn, map, game, owner.pawns, work)")
        for reader in ("owner.reader.Read(pawn", "owner.needs.Read(pawn", "owner.health.Read(pawn",
                       "owner.gear.Read(pawn", "owner.jobs.Read(pawn"):
            self.assertLess(gate, detail.index(reader))
        ui = source("mod/Observation/GameUi.cs")
        self.assertLess(ui.index("owner.access.Thing("), ui.index("work.Generations.Reference("))

    def test_all_baseline_routes_are_registered_not_always_unavailable(self):
        text = source("mod/Bridge/ModBootstrap.cs")
        for route in ("Summary", "Pawns", "Pawn", "Things", "Resources", "Ui", "Events", "Screenshot"):
            self.assertIn(f"routes.Add((ReadMethod.{route},", text)
        dispatcher = source("mod/Bridge/ReadDispatcher.cs")
        self.assertIn("Responses.BootstrapCapabilities(m => producers[(int)m] != null)", dispatcher)
        self.assertIn("ui.rendered_controls", source("shared/Protocol/Responses.cs"))

    def test_full_harness_runs_every_observation_suite_not_only_focused_modes(self):
        text = source("tests/JsonIngress/Program.cs")
        full = text[text.index("int wireFailed ="):]
        for test in (ROOT / "tests/JsonIngress").glob("*Tests.cs"):
            # Every test class has a Run entry, including event suites previously only in admission mode.
            self.assertIn(test.stem + ".Run(", full, test.name)


if __name__ == "__main__":
    unittest.main()
