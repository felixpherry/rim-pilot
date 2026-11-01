"""Asset-free pin/license/shared-source guards; behavioral tests live in JsonIngress."""
import json
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent


class JsonDependencyTests(unittest.TestCase):
    def test_exact_version_net45_asset_and_redistribution_notice(self):
        project = ET.parse(ROOT / "shared/Json/JsonDependency.props")
        package, = project.findall(".//PackageReference")
        self.assertEqual(package.get("Include"), "Newtonsoft.Json")
        self.assertEqual(package.get("Version"), "[13.0.3]")
        self.assertEqual(package.get("ExcludeAssets"), "all")
        self.assertEqual(project.findtext(".//Reference/HintPath"),
                         "$(PkgNewtonsoft_Json)/lib/net45/Newtonsoft.Json.dll")
        self.assertEqual(project.findtext(".//Reference/Private"), "true")
        self.assertEqual(project.findtext(".//RestoreLockedMode"), "true")
        notice, = project.findall(".//None")
        self.assertEqual(notice.get("CopyToOutputDirectory"), "PreserveNewest")
        self.assertEqual(notice.get("CopyToPublishDirectory"), "PreserveNewest")
        self.assertEqual(notice.get("ExcludeFromSingleFile"), "true")
        license_text = (ROOT / "licenses/Newtonsoft.Json.LICENSE.md").read_text()
        self.assertIn("Copyright (c) 2007 James Newton-King", license_text)
        self.assertIn("THE SOFTWARE IS PROVIDED \"AS IS\"", license_text)

    def test_both_endpoints_and_tests_link_identical_ingress(self):
        for relative in ("mod/RimPilot.Mod.csproj", "cli/RimPilot.Cli.csproj",
                         "tests/JsonIngress/JsonIngress.csproj"):
            project = ROOT / relative
            imports = ET.parse(project).findall(".//Import")
            resolved = [(project.parent / item.get("Project")).resolve()
                        for item in imports if "$" not in item.get("Project")]
            self.assertIn(ROOT / "shared/Json/JsonDependency.props", resolved)
        shared = ET.parse(ROOT / "shared/Json/JsonDependency.props")
        self.assertEqual(shared.find(".//Compile").get("Include"), "$(MSBuildThisFileDirectory)*.cs")

    def test_lock_content_hash_agrees_across_runtime_targets(self):
        hashes = set()
        for relative in ("mod", "cli", "tests/JsonIngress"):
            lock = json.loads((ROOT / relative / "packages.lock.json").read_text())
            found = False
            for dependencies in lock["dependencies"].values():
                if "Newtonsoft.Json" in dependencies:
                    found = True
                    package = dependencies["Newtonsoft.Json"]
                    self.assertEqual(package["resolved"], "13.0.3")
                    self.assertEqual(package["requested"], "[13.0.3, 13.0.3]")
                    hashes.add(package["contentHash"])
            self.assertTrue(found)
        self.assertEqual(len(hashes), 1)
        self.assertEqual(hashes.pop(),
                         "HrC5BXdl00IP9zeV+0Z848QWPAoCr9P3bDEZguI+gkLcBKAOxix/tLEAAHC+UvDNPv4a2d18lOReHMOagPa+zQ==")

    def test_shared_ingress_has_no_object_deserialization_or_reflection(self):
        for source in (ROOT / "shared/Json").glob("*.cs"):
            text = re.sub(r"//[^\n]*", "", source.read_text())
            self.assertNotRegex(text, r"\b(?:Deserialize\w*|ToObject|FromObject|SerializeObject)\s*(?:<|\()")
            self.assertNotRegex(text, r"\b(?:JsonSerializer|TypeNameHandling|Activator)\b")
            self.assertNotIn("System.Reflection", text)


if __name__ == "__main__":
    unittest.main()
