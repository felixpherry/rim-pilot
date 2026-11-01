"""Asset-free SDK/bootstrap and project-boundary checks (no game execution)."""
import hashlib
import json
from pathlib import Path
import runpy
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent


class BuildFoundationTests(unittest.TestCase):
    def setUp(self):
        namespace = runpy.run_path(str(ROOT / "scripts/dotnet"), run_name="sdk_test")
        self.bootstrap = namespace["main"]
        self.sandbox = tempfile.TemporaryDirectory()
        self.addCleanup(self.sandbox.cleanup)
        self.root = Path(self.sandbox.name)
        (self.root / "scripts").mkdir()
        self.cache = self.root / "cache"
        self.lock = {
            "version": "10.0.403",
            "rid": "linux-x64",
            "url": "https://invalid.example/sdk.tar.gz",
            "sha512": hashlib.sha512(b"expected archive").hexdigest(),
        }
        self.write_config()
        self.root_patch = patch.dict(self.bootstrap.__globals__, ROOT=self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)
        self.env_patch = patch.dict("os.environ", {"XDG_CACHE_HOME": str(self.cache)})
        self.env_patch.start()
        self.addCleanup(self.env_patch.stop)
        self.platform_patch = patch("platform.system", return_value="Linux")
        self.platform_patch.start()
        self.addCleanup(self.platform_patch.stop)
        self.arch_patch = patch("platform.machine", return_value="x86_64")
        self.arch_patch.start()
        self.addCleanup(self.arch_patch.stop)

    def write_config(self):
        (self.root / "scripts/sdk.lock.json").write_text(json.dumps(self.lock))
        (self.root / "global.json").write_text(json.dumps({"sdk": {"version": self.lock["version"]}}))

    def test_checked_in_pin_is_exact_and_consistent(self):
        lock = json.loads((ROOT / "scripts/sdk.lock.json").read_text())
        sdk = json.loads((ROOT / "global.json").read_text())["sdk"]
        self.assertEqual(sdk["version"], lock["version"])
        self.assertEqual(sdk["rollForward"], "disable")
        self.assertFalse(sdk["allowPrerelease"])
        self.assertRegex(lock["sha512"], r"^[0-9a-f]{128}$")
        self.assertIn(f"/Sdk/{lock['version']}/", lock["url"])
        self.assertTrue(lock["url"].startswith("https://builds.dotnet.microsoft.com/"))

    def test_version_disagreement_fails_before_download(self):
        (self.root / "global.json").write_text('{"sdk":{"version":"0.0.0"}}')
        with patch("urllib.request.urlopen") as download:
            with self.assertRaisesRegex(RuntimeError, "disagree"):
                self.bootstrap()
            download.assert_not_called()

    def test_wrong_checksum_never_extracts(self):
        cache = self.cache / "rim-pilot"
        cache.mkdir(parents=True)
        (cache / f"dotnet-sdk-{self.lock['version']}-linux-x64.tar.gz").write_bytes(b"corrupt archive")
        with patch("tarfile.open") as extract, patch("urllib.request.urlopen") as download:
            with self.assertRaisesRegex(RuntimeError, "SHA-512 mismatch"):
                self.bootstrap()
            extract.assert_not_called()
            download.assert_not_called()
        self.assertFalse((cache / "dotnet" / self.lock["version"]).exists())

    def test_unverified_install_is_not_overwritten(self):
        sdk = self.cache / "rim-pilot" / "dotnet" / self.lock["version"]
        sdk.mkdir(parents=True)
        sentinel = sdk / "owned-by-someone-else"
        sentinel.write_text("unchanged")
        with self.assertRaisesRegex(RuntimeError, "Unverified SDK"):
            self.bootstrap()
        self.assertEqual(sentinel.read_text(), "unchanged")

    def test_relative_cache_is_rejected(self):
        with patch.dict("os.environ", {"XDG_CACHE_HOME": "relative"}):
            with self.assertRaisesRegex(RuntimeError, "must be absolute"):
                self.bootstrap()

    def test_wrong_architecture_is_rejected(self):
        with patch("platform.machine", return_value="aarch64"):
            with self.assertRaisesRegex(RuntimeError, "Linux x64 only"):
                self.bootstrap()

    def test_cli_has_no_game_project_or_binary_dependency(self):
        project = ET.parse(ROOT / "cli/RimPilot.Cli.csproj")
        self.assertEqual(project.findtext(".//SelfContained"), "true")
        self.assertEqual(project.findtext(".//PublishSingleFile"), "true")
        self.assertEqual(project.findtext(".//RuntimeIdentifier"), "linux-x64")
        for element in ("Reference", "ProjectReference"):
            self.assertEqual(project.findall(f".//{element}"), [])
        self.assertEqual([item.get("Project") for item in project.findall(".//Import")],
                         ["../shared/Json/JsonDependency.props", "../shared/Protocol/Protocol.props"])

    def test_mod_does_not_restore_desktop_reference_packs(self):
        project = ET.parse(ROOT / "mod/RimPilot.Mod.csproj")
        self.assertEqual(project.findtext(".//TargetFramework"), "net472")
        self.assertEqual(project.findtext(".//AutomaticallyUseReferenceAssemblyPackages"), "false")
        self.assertEqual(project.findtext(".//NoStdLib"), "true")
        for package in project.findall(".//PackageReference"):
            self.assertFalse(package.get("Include", "").startswith("Microsoft.NETFramework.ReferenceAssemblies"))
        self.assertIsNotNone(project.find(".//Target[@Name='RejectProprietaryCopyLocal']"))


if __name__ == "__main__":
    unittest.main()
