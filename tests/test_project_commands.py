"""Asset-free command isolation, failure propagation and redistributable boundaries."""
import hashlib
import json
import os
from pathlib import Path
import runpy
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent.parent
COMMANDS = runpy.run_path(str(ROOT / "scripts/project.py"), run_name="command_test")
USAGE = ("rim-pilot: usage: {sessions|health|capabilities|summary|pawns.list|pawn.get|things.list|resources|ui|events.read|screenshot} "
         "[--session SESSION] [--root ROOT --game GAME --map MAP] [--pawn PAWN] "
         "[--limit 1..64 --cursor CURSOR] [--output LOCAL_PATH]\n")


class ProjectCommandTests(unittest.TestCase):
    def setUp(self):
        self.sandbox = tempfile.TemporaryDirectory()
        self.addCleanup(self.sandbox.cleanup)
        self.root = Path(self.sandbox.name)
        self.output = self.root / "output"
        self.output.mkdir()
        self.verify = COMMANDS["verify_output"]

    def populate(self, kind):
        required, optional = COMMANDS["OUTPUTS"][kind]
        for relative in required | optional:
            target = self.output / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(relative)

    def test_each_explicit_output_profile_accepts_only_own_files(self):
        for kind in ("mod", "cli"):
            with self.subTest(kind=kind):
                folder = self.root / kind
                self.output = folder
                folder.mkdir()
                self.populate(kind)
                self.verify(folder, kind)
                (folder / "Assembly-CSharp.dll").write_bytes(b"not distributable")
                with self.assertRaisesRegex(RuntimeError, "allowlist"):
                    self.verify(folder, kind)

    def test_missing_redistribution_notice_rejected(self):
        self.populate("cli")
        (self.output / COMMANDS["NOTICE"]).unlink()
        with self.assertRaisesRegex(RuntimeError, "allowlist"):
            self.verify(self.output, "cli")

    def test_optional_debug_symbols_can_be_absent(self):
        self.populate("mod")
        (self.output / "RimPilot.Mod.pdb").unlink()
        self.verify(self.output, "mod")

    def test_symlinked_output_file_and_directory_rejected(self):
        self.populate("cli")
        link = self.output / "rim-pilot"
        link.unlink()
        link.symlink_to(ROOT / "README.md")
        with self.assertRaisesRegex(RuntimeError, "symlinks"):
            self.verify(self.output, "cli")
        link.unlink()
        link.symlink_to(self.root, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "symlinks"):
            self.verify(self.output, "cli")
        root_link = self.root / "linked-output"
        root_link.symlink_to(self.output, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "symlinked"):
            self.verify(root_link, "cli")

    def test_renamed_proprietary_content_rejected_by_hash(self):
        self.populate("mod")
        (self.output / "RimPilot.Mod.dll").write_bytes(b"private installed assembly")
        audit = self.root / "audit.json"
        audit.write_text(json.dumps({"References": [{"Sha256": hashlib.sha256(
            b"private installed assembly").hexdigest().upper()}]}))
        with self.assertRaisesRegex(RuntimeError, "game/Harmony content"):
            self.verify(self.output, "mod", audit)

    def test_configured_mod_build_requires_both_absolute_paths(self):
        build = COMMANDS["build_game"]
        for env in ({}, {"RIMWORLD_DIR": "/configured"},
                    {"RIMWORLD_DIR": "relative", "HARMONY_MOD_DIR": "/configured"}):
            with self.subTest(env=env), patch.dict(os.environ, env, clear=True), \
                    patch.dict(build.__globals__, {"dotnet": unittest.mock.Mock()}) as state:
                with self.assertRaisesRegex(RuntimeError, "requires absolute"):
                    build()
                state["dotnet"].assert_not_called()

    def test_check_compiles_no_game_and_stops_on_first_error(self):
        check = COMMANDS["check"]
        fake_dotnet = unittest.mock.Mock()
        fake_run = unittest.mock.Mock()
        with patch.dict(check.__globals__, {"dotnet": fake_dotnet, "run": fake_run}):
            check()
            projects = [call.args[1] for call in fake_dotnet.call_args_list]
            self.assertEqual(projects, ["tools/ReferenceAudit/ReferenceAudit.csproj",
                                       "cli/RimPilot.Cli.csproj", "tests/JsonIngress/JsonIngress.csproj"])
            self.assertTrue(all("-p:RestoreLockedMode=true" in call.args for call in fake_dotnet.call_args_list))
            fake_dotnet.reset_mock()
            fake_run.reset_mock()
            fake_dotnet.side_effect = subprocess.CalledProcessError(1, "compiler")
            with self.assertRaises(subprocess.CalledProcessError):
                check()
            self.assertEqual(fake_dotnet.call_count, 1)
            fake_run.assert_not_called()

    def test_published_smoke_uses_only_scratch_registry_without_sdk_lookup(self):
        smoke = COMMANDS["smoke_cli"]
        calls = []

        def execute(command, **kwargs):
            calls.append((command, kwargs))
            runtime = Path(kwargs["env"]["XDG_RUNTIME_DIR"])
            registry = runtime / "rim-pilot/v1"
            registry.mkdir(parents=True, exist_ok=True)
            self.assertEqual(runtime.stat().st_mode & 0o777, 0o700)
            self.assertTrue(runtime.is_relative_to(self.root / ".local"))
            self.assertTrue(Path(kwargs["env"]["HOME"]).is_relative_to(self.root / ".local"))
            for name in ("DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_BUNDLE_EXTRACT_BASE_DIR", "PATH"):
                self.assertEqual(kwargs["env"][name], kwargs["cwd"])
            self.assertEqual(kwargs["env"]["DOTNET_MULTILEVEL_LOOKUP"], "0")
            self.assertEqual(kwargs["timeout"], 30)
            if len(command) == 1:
                return subprocess.CompletedProcess(command, 64, "", USAGE)
            output = '{"v":1,"sessions":[]}\n' if command[1] == "sessions" else ""
            return subprocess.CompletedProcess(command, 69, output, "rim-pilot: no_live_session.\n")

        with patch.dict(smoke.__globals__, {"ROOT": self.root}), patch("subprocess.run", side_effect=execute):
            smoke()
        self.assertEqual([command[1:] for command, _ in calls],
                         [[], ["sessions"], ["health"], ["capabilities"], ["summary"], ["ui"],
                          ["events.read"], ["screenshot", "--output", "capture.png"]])
        self.assertFalse(any((self.root / ".local").iterdir()))

    def test_published_smoke_rejects_obsolete_or_invalid_usage_results(self):
        smoke = COMMANDS["smoke_cli"]
        for result in (subprocess.CompletedProcess([], 64, "", "not implemented\n"),
                       subprocess.CompletedProcess([], 64, "", "rim-pilot: usage: {sessions|health|capabilities} [--session SESSION]\n"),
                       subprocess.CompletedProcess([], 0, "", ""),
                       subprocess.CompletedProcess([], 64, "unexpected output", USAGE)):
            with self.subTest(result=result), patch.dict(smoke.__globals__, {"ROOT": self.root}), \
                    patch("subprocess.run", return_value=result):
                with self.assertRaisesRegex(RuntimeError, "smoke failed"):
                    smoke()

    def test_published_smoke_rejects_nonempty_or_wrong_registry_result(self):
        smoke = COMMANDS["smoke_cli"]
        usage = subprocess.CompletedProcess([], 64, "", USAGE)
        for result in (subprocess.CompletedProcess([], 0, '{"v":1,"sessions":[]}', ""),
                       subprocess.CompletedProcess([], 69, '{"v":1,"sessions":[{"status":"live"}]}',
                           "rim-pilot: no_live_session.\n"),
                       subprocess.CompletedProcess([], 69, "", "rim-pilot: registry_rejected.\n")):
            with self.subTest(result=result), patch.dict(smoke.__globals__, {"ROOT": self.root}), \
                    patch("subprocess.run", side_effect=[usage, result]):
                with self.assertRaises((RuntimeError, ValueError)):
                    smoke()

    def test_subprocess_failure_is_not_silenced(self):
        run = COMMANDS["run"]
        with patch("subprocess.run", side_effect=subprocess.CalledProcessError(3, "test")) as child:
            with self.assertRaises(subprocess.CalledProcessError):
                run("example")
            self.assertTrue(child.call_args.kwargs["check"])
            self.assertEqual(child.call_args.kwargs["cwd"], ROOT)


if __name__ == "__main__":
    unittest.main()
