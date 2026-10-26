"""Asset-free safety-manifest guards, not game-state sampling or runtime proof."""
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = json.loads((ROOT / "protocol/developer-safety-v1.json").read_text())


class ObservationSafetyContractTests(unittest.TestCase):
    def test_known_developer_panels_explicit(self):
        panels = MANIFEST["knownDeveloperPanelTypes"]
        self.assertEqual(len(panels), len(set(panels)))
        self.assertEqual(set(panels), {
            "LudeonTK.Dialog_Debug", "LudeonTK.EditWindow_DebugInspector",
            "LudeonTK.EditWindow_TweakValues", "LudeonTK.EditWindow_Log", "Verse.Dialog_DevPalette",
        })

    def test_debug_baseline_is_finite_static_fields_not_getters(self):
        families = MANIFEST["normalStaticBooleanValues"]
        self.assertEqual(set(families), {"Verse.DebugSettings", "Verse.DebugViewSettings"})
        self.assertEqual(sum(map(len, families.values())), 120)
        for fields in families.values():
            for name, value in fields.items():
                with self.subTest(field=name):
                    self.assertRegex(name, r"^[A-Za-z][A-Za-z0-9]*$")
                    self.assertIs(type(value), bool)
                    self.assertNotIn(name, {"DevMode", "ShowDevGizmos"})
        self.assertIn("Static named reads only", MANIFEST["note"])

    def test_high_risk_visibility_and_render_baselines(self):
        families = MANIFEST["normalStaticBooleanValues"]
        settings = families["Verse.DebugSettings"]
        self.assertEqual(settings, dict.fromkeys([
            "godMode", "devPalette", "showHiddenPawns", "showHiddenInfo",
            "showLocomotionUrgency", "searchIgnoresRestrictions",
        ], False))
        view = families["Verse.DebugViewSettings"]
        for name in ["drawFog", "drawThingsDynamic", "drawThingsPrinted", "drawDarknessOverlay",
                     "drawTerrain", "drawTerrainCurtain", "drawWorldObjects"]:
            self.assertIs(view[name], True)
        for name in ["drawPawnDebug", "drawLords", "drawDuties", "drawDeepResources",
                     "drawAttackTargetScores", "writeCellContents", "writeStoryteller",
                     "writeMapGameConditions", "showAllRoomStats", "showFloatMenuWorkGivers"]:
            self.assertIs(view[name], False)

    def test_capture_producer_requires_completed_stamp_not_raw_frame_claim(self):
        source = (ROOT / "shared/Protocol/Responses.cs").read_text()
        self.assertRegex(source, r"JObject Capture\(CompletedRenderStamp completed,")
        self.assertNotRegex(source, r"JObject Capture\(Scope")
        code = re.sub(r"//[^\n]*", "", (ROOT / "shared/Protocol/ObservationSafety.cs").read_text())
        self.assertNotRegex(code, r"\b(?:UnityEngine|Verse|System\.Reflection|dynamic)\b")
        self.assertIn("private CompletedRenderStamp(RenderEvidence rendered)", code)


if __name__ == "__main__":
    unittest.main()
