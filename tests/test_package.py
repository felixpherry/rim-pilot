"""Complete-package checks are independent of manifest claims and raw output checks."""
import hashlib
import json
import os
from pathlib import Path
import runpy
import stat
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
PACKAGE = runpy.run_path(str(ROOT / "scripts/package.py"), run_name="package_test")


class PackageTests(unittest.TestCase):
    def setUp(self):
        sandbox = tempfile.TemporaryDirectory()
        self.addCleanup(sandbox.cleanup)
        self.root = Path(sandbox.name) / "source"
        self.root.mkdir()
        self.output = Path(sandbox.name) / "output"
        self.audit = self.root / "audit.json"
        self.private = b"private game or Harmony bytes"
        self.audit.write_text(json.dumps({"References": [{"Sha256": self.hash(self.private)}]}))
        for relative, (source, _, _) in PACKAGE["FILES"].items():
            if source is not None:
                self.put(source, (ROOT / source).read_bytes() if not source.endswith((".dll", "rim-pilot"))
                         else relative.encode())
        for relative in ("Directory.Build.props", "cli/RimPilot.Cli.csproj"):
            self.put(relative, (ROOT / relative).read_bytes())
        self.pins = json.loads((ROOT / "packaging/redistributables.json").read_text())
        cache = self.root / "cache"
        runtime = self.pins["runtime"]
        for name in runtime["notices"]:
            content = name.encode()
            runtime["notices"][name] = self.hash(content)
            self.put(f"cache/{runtime['package']}/{runtime['version']}/{name}", content)
        self.pins["newtonsoft"]["net45Sha256"] = self.hash(
            (self.root / "mod/bin/Release/net472/Newtonsoft.Json.dll").read_bytes())
        self.put("packaging/redistributables.json", json.dumps(self.pins).encode())
        self.put("cli/obj/project.assets.json", json.dumps(
            {"project": {"restore": {"packagesPath": str(cache)}}}).encode())
        PACKAGE["build"](self.output, self.root, self.audit)

    @staticmethod
    def hash(content):
        return hashlib.sha256(content).hexdigest()

    def put(self, relative, content):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    def verify(self, folder=None):
        return PACKAGE["verify"](folder or self.output, self.root, self.audit)

    def manifest(self):
        return json.loads((self.output / "ownership.json").read_text())

    def save_manifest(self, manifest):
        (self.output / "ownership.json").write_text(json.dumps(manifest))

    def test_exact_layout_versions_dependency_ownership_and_modes(self):
        manifest = self.verify()
        self.assertEqual(manifest["version"], "0.1.0")
        self.assertEqual(manifest["owner"], "rim-pilot")
        self.assertEqual({item["path"] for item in manifest["files"]}, set(PACKAGE["FILES"]))
        self.assertEqual(len(manifest["files"]), 10)
        self.assertNotIn("ownership.json", {item["path"] for item in manifest["files"]})
        about = ET.parse(self.output / "mod/RimPilot/About/About.xml")
        self.assertEqual(about.findtext("modDependencies/li/packageId"), "brrainz.harmony")
        self.assertEqual(about.findtext("loadAfter/li"), "brrainz.harmony")
        self.assertEqual(stat.S_IMODE((self.output / "cli/rim-pilot").stat().st_mode), 0o755)
        self.assertFalse(any("/home/" in str(item) or "token" in str(item) for item in manifest["files"]))

    def test_build_refuses_existing_output_without_modifying_it(self):
        before = (self.output / "ownership.json").read_bytes()
        with self.assertRaisesRegex(RuntimeError, "refusing overwrite"):
            PACKAGE["build"](self.output, self.root, self.audit)
        self.assertEqual((self.output / "ownership.json").read_bytes(), before)

    def test_unknown_content_rejected_even_when_added_to_manifest(self):
        for relative in ("mod/RimPilot/Assemblies/0Harmony.dll", "cli/UnityEngine.dll",
                         "docs/private-proof.png", "Saves/secret.rws", "credentials.json",
                         "mod/RimPilot/Assemblies/renamed.dll"):
            with self.subTest(relative=relative):
                path = self.output / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(self.private)
                manifest = self.manifest()
                manifest["files"].append({"path": relative, "sha256": self.hash(self.private)})
                self.save_manifest(manifest)
                with self.assertRaisesRegex(RuntimeError, "allowlist"):
                    self.verify()
                path.unlink()
                if path.parent.name == "Saves":
                    path.parent.rmdir()
                manifest["files"].pop()
                self.save_manifest(manifest)

    def test_unlisted_empty_directory_rejected(self):
        (self.output / "hidden-proof").mkdir()
        with self.assertRaisesRegex(RuntimeError, "directory allowlist"):
            self.verify()

    def test_missing_required_file_or_license_rejected(self):
        for relative in ("mod/RimPilot/About/About.xml", "licenses/dotnet/THIRD-PARTY-NOTICES.TXT"):
            path = self.output / relative
            content = path.read_bytes()
            path.unlink()
            with self.assertRaisesRegex(RuntimeError, "allowlist missing"):
                self.verify()
            path.write_bytes(content)

    def test_modified_file_with_self_consistent_hash_still_rejected(self):
        path = self.output / "cli/rim-pilot"
        path.write_bytes(b"untrusted executable")
        manifest = self.manifest()
        for item in manifest["files"]:
            if item["path"] == "cli/rim-pilot":
                item.update(sha256=self.hash(path.read_bytes()), size=path.stat().st_size)
        self.save_manifest(manifest)
        with self.assertRaisesRegex(RuntimeError, "trusted build/source"):
            self.verify()

    def test_renamed_proprietary_bytes_rejected_independent_of_manifest(self):
        path = self.output / "mod/RimPilot/Assemblies/RimPilot.Mod.dll"
        path.write_bytes(self.private)
        with self.assertRaisesRegex(RuntimeError, "game/Harmony content.*complete package"):
            self.verify()

    def test_poisoned_trusted_build_output_rejected_by_reference_hash(self):
        path = self.root / "mod/bin/Release/net472/RimPilot.Mod.dll"
        path.write_bytes(self.private)
        with self.assertRaisesRegex(RuntimeError, "game/Harmony content.*inputs"):
            self.verify()

    def test_manifest_traversal_absolute_alias_backslash_and_duplicate_rejected(self):
        original = self.manifest()
        for value in ("../outside", "/outside", "docs/../outside", "docs//package.md",
                      "docs/./package.md", "docs\\package.md", "", None):
            with self.subTest(value=value):
                manifest = json.loads(json.dumps(original))
                manifest["files"][0]["path"] = value
                self.save_manifest(manifest)
                with self.assertRaisesRegex(RuntimeError, "relative path"):
                    self.verify()
        manifest = original
        manifest["files"].append(manifest["files"][0])
        self.save_manifest(manifest)
        with self.assertRaisesRegex(RuntimeError, "Duplicate/unknown manifest"):
            self.verify()

    def test_manifest_directory_traversal_rejected(self):
        manifest = self.manifest()
        manifest["directories"].append("../outside")
        self.save_manifest(manifest)
        with self.assertRaisesRegex(RuntimeError, "relative path"):
            self.verify()

    def test_manifest_hash_owner_component_mode_version_and_extra_keys_rejected(self):
        original = self.manifest()
        for key, value in (("sha256", "0" * 64), ("owner", "user"), ("component", "harmony"),
                           ("mode", "0777"), ("size", 0)):
            manifest = json.loads(json.dumps(original))
            manifest["files"][0][key] = value
            self.save_manifest(manifest)
            with self.subTest(key=key), self.assertRaisesRegex(RuntimeError, "manifest differs"):
                self.verify()
        for key, value in (("version", "9.9.9"), ("unexpected", True), ("format", True)):
            manifest = json.loads(json.dumps(original))
            manifest[key] = value
            self.save_manifest(manifest)
            with self.subTest(key=key), self.assertRaisesRegex(RuntimeError, "manifest differs"):
                self.verify()

    def test_duplicate_json_keys_and_oversized_manifest_rejected(self):
        path = self.output / "ownership.json"
        path.write_text('{"format":1,"format":1}')
        with self.assertRaisesRegex(RuntimeError, "Duplicate JSON"):
            self.verify()
        path.write_bytes(b" " * (PACKAGE["MAX_MANIFEST"] + 1))
        with self.assertRaisesRegex(RuntimeError, "bounded regular"):
            self.verify()

    def test_file_symlink_hardlink_fifo_and_directory_link_rejected(self):
        path = self.output / "docs/package.md"
        content = path.read_bytes()
        for kind in ("symlink", "hardlink", "fifo"):
            path.unlink()
            if kind == "symlink":
                path.symlink_to(self.root / "docs/package.md")
            elif kind == "hardlink":
                os.link(self.root / "docs/package.md", path)
            else:
                os.mkfifo(path)
            with self.subTest(kind=kind), self.assertRaisesRegex(RuntimeError, "links/hardlinks/special"):
                self.verify()
            path.unlink()
            path.write_bytes(content)
        docs = self.output / "docs"
        docs.rename(self.output.parent / "saved-docs")
        docs.symlink_to(self.output.parent / "saved-docs", target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "links/hardlinks/special"):
            self.verify()

    def test_root_or_ancestor_symlink_rejected(self):
        link = self.output.parent / "linked-output"
        link.symlink_to(self.output, target_is_directory=True)
        with self.assertRaises(OSError):
            self.verify(link)
        self.output.rename(self.output.parent / "moved")
        self.output.symlink_to(self.output.parent / "moved", target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "traversal forbidden"):
            self.verify(self.output / "../output")

    def test_build_parent_link_refused_before_any_external_directory_creation(self):
        outside = self.output.parent / "outside"
        outside.mkdir()
        link = self.output.parent / "linked-parent"
        link.symlink_to(outside, target_is_directory=True)
        with self.assertRaises(OSError):
            PACKAGE["build"](link / "must-not-create" / "package", self.root, self.audit)
        self.assertEqual(list(outside.iterdir()), [])

    def test_atomic_publication_collision_preserves_concurrent_target(self):
        output = self.output.parent / "concurrent-package"
        real_verify = PACKAGE["build"].__globals__["verify"]
        def collide(folder, root, audit):
            real_verify(folder, root, audit)
            output.mkdir()
        with patch.dict(PACKAGE["build"].__globals__, {"verify": collide}):
            with self.assertRaisesRegex(RuntimeError, "no-overwrite.*failed"):
                PACKAGE["build"](output, self.root, self.audit)
        self.assertTrue(output.is_dir())
        self.assertEqual(list(output.iterdir()), [])
        self.assertEqual(list(self.output.parent.glob(".package-*")), [])

    def test_wrong_directory_and_executable_modes_rejected(self):
        for relative, mode in (("docs", 0o777), ("cli/rim-pilot", 0o644), ("ownership.json", 0o600)):
            path = self.output / relative
            original = stat.S_IMODE(path.stat().st_mode)
            path.chmod(mode)
            with self.subTest(relative=relative), self.assertRaisesRegex(RuntimeError, "mode differs"):
                self.verify()
            path.chmod(original)

    def test_unknown_mode_under_restrictive_umask_is_normalized(self):
        previous = os.umask(0o077)
        try:
            output = self.output.parent / "private-build"
            PACKAGE["build"](output, self.root, self.audit)
            self.verify(output)
        finally:
            os.umask(previous)

    def test_runtime_notice_hash_runtime_version_and_json_pin_enforced(self):
        pins = self.root / "packaging/redistributables.json"
        original = pins.read_bytes()
        for component, key in (("runtime", "version"), ("newtonsoft", "net45Sha256")):
            config = json.loads(original)
            config[component][key] = "wrong"
            pins.write_text(json.dumps(config))
            with self.subTest(component=component), self.assertRaisesRegex(RuntimeError, "differs"):
                self.verify()
            pins.write_bytes(original)
        runtime = self.pins["runtime"]
        path = self.root / f"cache/{runtime['package']}/{runtime['version']}/LICENSE.TXT"
        path.write_text("different notice")
        with self.assertRaisesRegex(RuntimeError, "notice hash differs"):
            self.verify()

    def test_about_version_dependency_and_load_order_enforced(self):
        about = self.root / "mod/About/About.xml"
        original = about.read_text()
        for old, new in (("<modVersion>0.1.0", "<modVersion>0.2.0"),
                         ("<packageId>brrainz.harmony", "<packageId>wrong.harmony"),
                         ("<li>brrainz.harmony", "<li>wrong.harmony")):
            about.write_text(original.replace(old, new))
            with self.subTest(old=old), self.assertRaisesRegex(RuntimeError, "metadata differs"):
                self.verify()
            about.write_text(original)

    def test_source_link_or_missing_audit_is_never_accepted(self):
        source = self.root / "artifacts/cli/linux-x64/rim-pilot"
        source.unlink()
        source.symlink_to(self.output / "cli/rim-pilot")
        with self.assertRaises(OSError):
            self.verify()
        source.unlink()
        source.write_bytes(b"restored")
        self.audit.unlink()
        with self.assertRaises(OSError):
            self.verify()

    def test_failed_validation_publishes_no_package(self):
        output = self.output.parent / "failed-package"
        verify = PACKAGE["build"].__globals__["verify"]
        with patch.dict(PACKAGE["build"].__globals__, {"verify": lambda *_: PACKAGE["fail"]("injected failure")}):
            with self.assertRaisesRegex(RuntimeError, "injected failure"):
                PACKAGE["build"](output, self.root, self.audit)
        self.assertFalse(output.exists())
        self.assertEqual(list(self.output.parent.glob(".package-*")), [])
        self.assertIs(PACKAGE["build"].__globals__["verify"], verify)


class PackageCommandTests(unittest.TestCase):
    def test_configured_package_build_order_and_stop_on_failure(self):
        commands = runpy.run_path(str(ROOT / "scripts/project.py"), run_name="package_commands")
        build = commands["package"]
        calls = []
        def game():
            calls.append("game")
        def cli():
            calls.append("cli")
        with patch.dict(build.__globals__, {"build_game": game, "build": cli,
                                          "run": lambda *_: calls.append("package")}):
            build()
        self.assertEqual(calls, ["game", "cli", "package"])
        with patch.dict(build.__globals__, {"build_game": lambda: PACKAGE["fail"]("configured failure"),
                                          "build": unittest.mock.Mock(), "run": unittest.mock.Mock()}) as state:
            with self.assertRaisesRegex(RuntimeError, "configured failure"):
                build()
            state["build"].assert_not_called()
            state["run"].assert_not_called()


if __name__ == "__main__":
    unittest.main()
