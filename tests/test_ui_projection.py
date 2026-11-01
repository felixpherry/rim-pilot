"""Installed adapter wiring guards, not Unity Mono or normal-UI acceptance."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class UiProjectionTests(unittest.TestCase):
    def test_selection_enters_shared_gates_before_labels_or_refs(self):
        source = (ROOT / "mod/Observation/GameUi.cs").read_text()
        self.assertIn("public StepResult Step(ProducerWork<Map> work) => StepResult.Complete", source)
        self.assertIn("result = null; work.ReleaseMemory()", source)
        self.assertIn("raw.Count > UiProjection.MaxSelection", source)
        self.assertIn("SelectedObjectsListForReading", source)
        self.assertIn("work.Generations.MapScope(map)", source)
        gate = source.index("owner.access.Thing(thing, map, game, owner.types, work)")
        self.assertLess(source.index("if (!work.TryCandidate()) return false"), gate)
        self.assertLess(gate, source.index("owner.labels.LabelCost("))
        self.assertIn("if (gate == TargetGateResult.Deferred) return false", source)
        self.assertIn("if (gate != TargetGateResult.Eligible) continue", source)
        self.assertLess(gate, source.index("owner.pawns.LabelShort(pawn)"))
        self.assertLess(gate, source.index("work.Generations.Reference("))
        self.assertIn('owner.access.OwnPawn(ownPawn, game.World?.factionManager?.OfPlayer)', source)
        self.assertIn('work.Generations.Reference(own ? "p" : "t", thing, map)', source)
        self.assertIn("def.thingClass != thing.GetType()", source)
        self.assertIn("version.MoveNext()", source)
        self.assertIn("stack.Count != count || !owner.Fresh()", source)
        self.assertIn("work.TryRetain(Encoding.UTF8.GetByteCount", source)

    def test_exact_window_kinds_never_titles_getters_or_controls(self):
        source = (ROOT / "mod/Observation/GameUi.cs").read_text()
        kinds = source.split("private static string? WindowKind(", 1)[1].split("private sealed class Continuation", 1)[0]
        self.assertIn("Type type = window.GetType()", kinds)
        self.assertEqual(kinds.count("if (type == typeof("), 16)
        self.assertIn("return null", kinds)
        self.assertNotIn("window is ", kinds)
        self.assertIn("count > UiProjection.MaxWindows", source)
        self.assertLess(source.index("work.TryCandidate() || !work.TryNestedFields(1)"), source.index("Window window = stack[i]"))
        code = re.sub(r"//[^\n]*", "", source)
        for forbidden in ("SelectedPawns", "IsDebug", "optionalTitle", ".Title", "GetValue(", "DoWindowContents(",
                          "Widgets.", "Tooltip", "GetInspectString", "ClearSelection(", ".Select(", "WorldSelector"):
            self.assertNotIn(forbidden, code)
        responses = (ROOT / "shared/Protocol/Responses.cs").read_text()
        self.assertIn('new Capability("ui.rendered_controls", CapabilityState.Unsupported, "none")', responses)
        self.assertIn('GameKeyedText.Getter(typeof(Selector), "SelectedObjectsListForReading")', source)
        self.assertIn('GameKeyedText.Getter(typeof(WindowStack), "Item")', source)


if __name__ == "__main__":
    unittest.main()
