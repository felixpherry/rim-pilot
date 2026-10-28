"""Source/manifest parity guards; installed-source audit documented separately, no game proof."""
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
            "LudeonTK.Window_Dev", "LudeonTK.Dialog_Debug", "LudeonTK.EditWindow_DebugInspector",
            "LudeonTK.EditWindow_TweakValues", "LudeonTK.EditWindow_Log", "LudeonTK.Dialog_DevPalette",
            "Verse.Dialog_DebugSetSeverity", "Verse.Dialog_DebugSetHediffRemaining",
            "Verse.Dialog_PawnTableTest", "RimWorld.Dialog_DebugRenderTree",
            "Verse.Dialog_CameraConfig", "Verse.Dialog_CameraConfigList",
        })
        source = (ROOT / "mod/Bridge/GameDeveloperSafety.cs").read_text()
        self.assertEqual(set(re.findall(r"window is ([\w.]+)", source)), set(panels))
        audit = (ROOT / "docs/developer-safety-audit.md").read_text()
        for panel in panels:
            self.assertIn(f"`{panel}`", audit)

    def test_debug_baseline_is_finite_static_fields_not_getters(self):
        families = MANIFEST["normalStaticBooleanValues"]
        self.assertEqual(set(families), {"Verse.DebugSettings", "Verse.DebugViewSettings",
                                         "RimWorld.HealthCardUtility", "Verse.DebugGenerationSettings"})
        self.assertEqual(sum(map(len, families.values())), 183)
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
        for name in ["godMode", "devPalette", "showHiddenPawns", "showHiddenInfo",
                     "showLocomotionUrgency", "searchIgnoresRestrictions", "activateAllIdeoRoles",
                     "allowUndraftedMechOrders", "editableGlowerColors", "ignoreGravshipRange"]:
            self.assertIs(settings[name], False)
        self.assertIs(settings["anomalyDarkeningFX"], True)
        self.assertEqual(families["RimWorld.HealthCardUtility"], {
            "showAllHediffs": False, "showHediffsDebugInfo": False,
        })
        self.assertEqual(MANIFEST["normalStaticNullFields"], {
            "LudeonTK.DebugTools": ["curTool", "curMeasureTool"],
        })
        view = families["Verse.DebugViewSettings"]
        for name in ["drawFog", "drawThingsDynamic", "drawThingsPrinted", "drawDarknessOverlay",
                     "drawTerrain", "drawTerrainCurtain", "drawWorldObjects"]:
            self.assertIs(view[name], True)
        for name in ["drawPawnDebug", "drawLords", "drawDuties", "drawDeepResources",
                     "drawAttackTargetScores", "writeCellContents", "writeStoryteller",
                     "writeMapGameConditions", "showAllRoomStats", "showFloatMenuWorkGivers"]:
            self.assertIs(view[name], False)

    def test_every_manifest_field_has_exact_bounded_named_reader(self):
        source = (ROOT / "mod/Bridge/GameDeveloperSafety.cs").read_text()
        readers = re.findall(r"case (\d+): return ([\w.!()]+) == (true|false|null);", source)
        expected = []
        for family, fields in MANIFEST["normalStaticBooleanValues"].items():
            for field, normal in fields.items():
                expression = f"{family}.{field}"
                if family == "RimWorld.HealthCardUtility":
                    expression = f"{field}!()"
                    self.assertIn(f'BindHealthField("{field}")', source)
                expected.append((str(len(expected)), expression, str(normal).lower()))
        for family, fields in MANIFEST["normalStaticNullFields"].items():
            for field in fields:
                expected.append((str(len(expected)), f"{family}.{field}", "null"))
        self.assertEqual(readers, expected)
        self.assertIn(f"StaticReadCount => {len(expected)};", source)
        self.assertIn(f'VersionControl.CurrentVersionStringWithRev == "{MANIFEST["auditedGameVersion"]}"', source)
        self.assertLessEqual(len(expected), MANIFEST["limits"]["staticReads"])
        core = (ROOT / "mod/Bridge/DeveloperSafetySampler.cs").read_text()
        self.assertIn(f'MaxWindows = {MANIFEST["limits"]["windows"]}', core)
        self.assertIn(f'MaxStaticReads = {MANIFEST["limits"]["staticReads"]}', core)
        self.assertIn(f'RequiredStaticReads = {len(expected)}', core)
        code = re.sub(r"//[^\n]*", "", source)
        self.assertNotRegex(code, r"\.(?:IsDebug|GetValue|SetValue|GetProperties|GetFields|Title)\b")
        self.assertNotRegex(code, r"(?:DebugSettings|DebugViewSettings|Prefs)\.\w+\s*=(?!=)")
        self.assertIn("BindingFlags.DeclaredOnly", code)
        self.assertIn("field.FieldType != typeof(bool)", code)

    def test_capture_producer_requires_completed_stamp_not_raw_frame_claim(self):
        source = (ROOT / "shared/Protocol/Responses.cs").read_text()
        self.assertRegex(source, r"JObject Capture\(CompletedRenderStamp completed,")
        self.assertNotRegex(source, r"JObject Capture\(Scope")
        code = re.sub(r"//[^\n]*", "", (ROOT / "shared/Protocol/ObservationSafety.cs").read_text())
        self.assertNotRegex(code, r"\b(?:UnityEngine|Verse|System\.Reflection|dynamic)\b")
        self.assertIn("private CompletedRenderStamp(RenderEvidence rendered)", code)


if __name__ == "__main__":
    unittest.main()
