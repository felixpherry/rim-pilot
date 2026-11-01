"""Asset-free adversarial installer tests. Every target/package/profile is disposable."""
import errno
import json
import os
from pathlib import Path
import runpy
import subprocess
import sys
import unittest
from unittest.mock import patch

import test_package

ROOT = Path(__file__).resolve().parent.parent
INSTALL = runpy.run_path(str(ROOT / "scripts/install.py"), run_name="install_test")
OPERATE = INSTALL["operate"]
STATE = OPERATE.__globals__


class Interrupted(BaseException):
    """Simulate process death, not a caught operation failure."""


class InstallTests(unittest.TestCase):
    def setUp(self):
        self.fixture = test_package.PackageTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.base = self.fixture.output.parent
        self.parent = self.base / "Mods"
        self.parent.mkdir()
        self.target = self.parent / "RimPilot"
        self.sentinels = {}
        for relative in ("Mods/OtherMod/keep.txt", "profile/Saves/copy.rws",
                         "profile/Config/ModsConfig.xml", "profile/Config/Prefs.xml"):
            path = self.base / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(("sentinel:" + relative).encode())
            self.sentinels[path] = path.read_bytes()
        self.addCleanup(self.assert_sentinels)

    def assert_sentinels(self):
        for path, content in self.sentinels.items():
            self.assertEqual(path.read_bytes(), content)

    def run_operation(self, command, target=None):
        OPERATE(command, target or self.target, self.fixture.output,
                self.fixture.root, self.fixture.audit)

    def assert_installed(self):
        fd = INSTALL["package"].open_path(self.target, directory=True)
        try:
            receipt = INSTALL["load_receipt"](fd)
            INSTALL["verify_tree"](fd, receipt)
        finally:
            os.close(fd)
        for name in INSTALL["FILES"]:
            self.assertEqual((self.target / name).read_bytes(),
                             (self.fixture.output / (INSTALL["PREFIX"] + name)).read_bytes())
        self.assertFalse((self.parent / INSTALL["WORK"]).exists())

    def snapshot(self, folder=None):
        folder = folder or self.parent
        return {str(path.relative_to(folder)): (path.lstat().st_mode,
                                                path.read_bytes() if path.is_file() and not path.is_symlink()
                                                else os.readlink(path) if path.is_symlink() else None)
                for path in folder.rglob("*")}

    def interrupt(self, command, point):
        def stop(name):
            if name == point:
                raise Interrupted(name)
        with patch.dict(STATE, {"boundary": stop}):
            with self.assertRaises(Interrupted):
                self.run_operation(command)

    def test_repeated_install_same_version_reinstall_remove_and_receipt(self):
        for _ in range(2):
            self.run_operation("install")
            self.assert_installed()
            self.assertEqual(set(path.name for path in self.target.iterdir()),
                             set(INSTALL["DIRS"]) | {INSTALL["RECEIPT"]})
            before = self.snapshot(self.target)
            self.run_operation("reinstall")
            self.assert_installed()
            self.assertEqual(self.snapshot(self.target), before)
            self.run_operation("uninstall")
            self.assertFalse(self.target.exists())
        self.assertEqual(sorted(path.name for path in self.parent.iterdir()), ["OtherMod"])

    def test_new_version_reinstall_replaces_only_owned_tree(self):
        self.run_operation("install")
        # Model a separately verified next-version artifact without depending on proprietary assets.
        source = STATE["source_payload"]
        def next_version(*args):
            contents, receipt = source(*args)
            contents["Assemblies/RimPilot.Mod.dll"] = b"new mod version"
            receipt["version"] = "0.2.0"
            for item in receipt["files"]:
                if item["path"] == "Assemblies/RimPilot.Mod.dll":
                    item["sha256"] = INSTALL["package"].digest(contents[item["path"]])
                    item["size"] = len(contents[item["path"]])
            return contents, receipt
        with patch.dict(STATE, {"source_payload": next_version}):
            self.run_operation("reinstall")
        self.assertEqual((self.target / "Assemblies/RimPilot.Mod.dll").read_bytes(), b"new mod version")
        self.run_operation("uninstall")

    def test_explicit_absolute_fixed_basename_and_existing_parent_required(self):
        for target in (Path("Mods/RimPilot"), self.parent, self.base / "Saves", self.parent / "x/../RimPilot",
                       self.base / "missing/RimPilot", Path("/")):
            before = self.snapshot()
            with self.subTest(target=target), self.assertRaises((OSError, RuntimeError)):
                self.run_operation("install", target)
            self.assertEqual(self.snapshot(), before)
        self.assertFalse((self.base / "missing").exists())

    def test_empty_unknown_nonempty_and_file_target_collisions_preserved(self):
        for kind in ("empty", "directory", "file", "symlink"):
            if kind in ("empty", "directory"):
                self.target.mkdir()
                if kind == "directory":
                    (self.target / "user.txt").write_text("keep")
            elif kind == "file":
                self.target.write_text("keep")
            else:
                self.target.symlink_to(self.parent / "OtherMod", target_is_directory=True)
            before = self.snapshot()
            for command in ("install", "reinstall", "uninstall"):
                with self.subTest(kind=kind, command=command), self.assertRaises((OSError, RuntimeError)):
                    self.run_operation(command)
                self.assertEqual(self.snapshot(), before)
            if self.target.is_dir() and not self.target.is_symlink():
                for item in self.target.iterdir():
                    item.unlink()
                self.target.rmdir()
            else:
                self.target.unlink()

    def test_unknown_files_directories_and_modified_or_missing_files_refuse_without_changes(self):
        self.run_operation("install")
        for kind in ("extra", "extra-dir", "modified", "missing", "mode", "receipt"):
            path = self.target / "Assemblies/RimPilot.Mod.dll"
            content = path.read_bytes()
            if kind == "extra":
                (self.target / "user-file.txt").write_text("keep")
            elif kind == "extra-dir":
                (self.target / "Saves").mkdir()
            elif kind == "modified":
                path.write_bytes(b"user changed assembly")
            elif kind == "missing":
                path.unlink()
            elif kind == "mode":
                path.chmod(0o600)
            else:
                (self.target / INSTALL["RECEIPT"]).write_text("{}")
            before = self.snapshot()
            for command in ("reinstall", "uninstall"):
                with self.subTest(kind=kind, command=command), self.assertRaises((OSError, RuntimeError)):
                    self.run_operation(command)
                self.assertEqual(self.snapshot(), before)
            if kind == "extra":
                (self.target / "user-file.txt").unlink()
            elif kind == "extra-dir":
                (self.target / "Saves").rmdir()
            elif kind == "receipt":
                # Restore using original package receipt, not inventing ownership.
                contents, receipt = INSTALL["source_payload"](
                    self.fixture.output, self.fixture.root, self.fixture.audit)
                (self.target / INSTALL["RECEIPT"]).write_bytes(INSTALL["encode"](receipt))
            else:
                if not path.exists():
                    path.write_bytes(content)
                path.write_bytes(content)
                path.chmod(0o644)

    def test_links_fifo_ancestor_and_directory_links_refused(self):
        self.run_operation("install")
        path = self.target / "Assemblies/RimPilot.Mod.dll"
        content = path.read_bytes()
        for kind in ("symlink", "hardlink", "fifo"):
            path.unlink()
            if kind == "symlink":
                path.symlink_to(self.parent / "OtherMod/keep.txt")
            elif kind == "hardlink":
                os.link(self.parent / "OtherMod/keep.txt", path)
            else:
                os.mkfifo(path)
            before = self.snapshot()
            for command in ("reinstall", "uninstall"):
                with self.subTest(kind=kind, command=command), self.assertRaises((OSError, RuntimeError)):
                    self.run_operation(command)
                self.assertEqual(self.snapshot(), before)
            path.unlink()
            path.write_bytes(content)
        (self.target / "licenses").rename(self.base / "moved-licenses")
        (self.target / "licenses").symlink_to(self.base / "moved-licenses", target_is_directory=True)
        with self.assertRaises((OSError, RuntimeError)):
            self.run_operation("uninstall")
        link = self.base / "linked-parent"
        link.symlink_to(self.parent, target_is_directory=True)
        with self.assertRaises(OSError):
            self.run_operation("install", link / "RimPilot")

    def test_world_writable_parent_root_and_wrong_uid_refused(self):
        self.parent.chmod(0o777)
        with self.assertRaisesRegex(RuntimeError, "parent must be owned"):
            self.run_operation("install")
        self.parent.chmod(0o755)
        self.run_operation("install")
        self.target.chmod(0o777)
        with self.assertRaisesRegex(RuntimeError, "ownership/mode"):
            self.run_operation("uninstall")
        self.target.chmod(0o755)
        with patch.object(INSTALL["os"], "getuid", return_value=os.getuid() + 1):
            with self.assertRaisesRegex(RuntimeError, "parent must be owned"):
                self.run_operation("uninstall")

    def test_strict_receipt_schema_cannot_authorize_unrelated_path(self):
        self.run_operation("install")
        path = self.target / INSTALL["RECEIPT"]
        original = path.read_bytes()
        for mutate in (lambda value: value.update(format=True),
                       lambda value: value["files"][0].update(path="../OtherMod/keep.txt"),
                       lambda value: value["files"][0].update(path="/etc/passwd"),
                       lambda value: value["files"].append(value["files"][0]),
                       lambda value: value["directories"].append("Saves"),
                       lambda value: value["files"][0].update(size=True),
                       lambda value: value.update(owner="user")):
            value = json.loads(original)
            mutate(value)
            path.write_bytes(INSTALL["encode"](value))
            with self.assertRaises(RuntimeError):
                self.run_operation("uninstall")
        path.write_bytes(original.replace(b'"format": 1,', b'"format": 1, "format": 1,'))
        with self.assertRaisesRegex(RuntimeError, "Duplicate JSON"):
            self.run_operation("uninstall")
        path.write_bytes(b" " * (INSTALL["package"].MAX_MANIFEST + 1))
        with self.assertRaises(RuntimeError):
            self.run_operation("uninstall")

    def test_package_is_independently_verified_before_any_target_change(self):
        self.run_operation("install")
        (self.fixture.output / "mod/RimPilot/Assemblies/RimPilot.Mod.dll").write_bytes(b"bad")
        before = self.snapshot()
        with self.assertRaisesRegex(RuntimeError, "trusted build/source"):
            self.run_operation("reinstall")
        self.assertEqual(self.snapshot(), before)

    def test_install_recovery_before_and_after_publication(self):
        for point in ("journal-published", "before-publish", "published", "committed"):
            with self.subTest(point=point):
                self.interrupt("install", point)
                with self.assertRaisesRegex(RuntimeError, "workspace collision"):
                    self.run_operation("install")
                self.run_operation("recover")
                self.assert_installed()
                self.run_operation("uninstall")

    def test_reinstall_and_uninstall_recovery_at_every_owned_mutation_boundary(self):
        deletion = ["deleted:" + name for name in INSTALL["FILES"]]
        deletion += ["deleted-dir:" + name for name in INSTALL["DIRS"]]
        points = ["journal-published", "before-quarantine", "quarantined", "committed",
                  *deletion, "deleted-receipt", "deleted-old"]
        for command in ("reinstall", "uninstall"):
            for point in points + (["before-publish", "published"] if command == "reinstall" else []):
                with self.subTest(command=command, point=point):
                    self.run_operation("install")
                    self.interrupt(command, point)
                    self.run_operation("recover")
                    if command == "reinstall":
                        self.assert_installed()
                        self.run_operation("uninstall")
                    self.assertFalse(self.target.exists())
                    self.assertFalse((self.parent / INSTALL["WORK"]).exists())

    def test_recovery_can_itself_be_interrupted_repeatedly_without_losing_ownership(self):
        for command in ("reinstall", "uninstall"):
            with self.subTest(command=command):
                self.run_operation("install")
                self.interrupt(command, "quarantined")
                # Resume from real partial cleanup, then die again at a later file. Recovery
                # must keep its journal/receipt authority rather than infer ownership from absence.
                for name in list(INSTALL["FILES"])[:2]:
                    self.interrupt("recover", "deleted:" + name)
                    old = self.parent / INSTALL["WORK"] / "old"
                    self.assertFalse((old / name).exists())
                    self.assertTrue((old / INSTALL["RECEIPT"]).is_file())
                    self.assert_sentinels()
                self.run_operation("recover")
                if command == "reinstall":
                    self.assert_installed()
                    self.run_operation("uninstall")
                self.assertFalse(self.target.exists())
                self.assertFalse((self.parent / INSTALL["WORK"]).exists())

    def test_prepublication_death_retains_preparation_and_leaves_target_unchanged(self):
        self.run_operation("install")
        before = self.snapshot(self.target)
        for point in ("prepared-directory", "stage-file:Assemblies/RimPilot.Mod.dll", "prepared"):
            self.interrupt("reinstall", point)
            self.assertEqual(self.snapshot(self.target), before)
            self.assertFalse((self.parent / INSTALL["WORK"]).exists())
        self.assertEqual(len(list(self.parent.glob(".RimPilot.rim-pilot-prepare-*"))), 3)
        # Preparations are never adopted/scavenged. A fresh verified operation is safe.
        self.run_operation("reinstall")
        self.assert_installed()

    def test_completion_death_does_not_block_new_operation_or_delete_orphans(self):
        for command in ("install", "reinstall", "uninstall"):
            if command != "install":
                self.run_operation("install")
            self.interrupt(command, "completed")
            self.assertFalse((self.parent / INSTALL["WORK"]).exists())
            if command != "uninstall":
                self.assert_installed()
                self.run_operation("uninstall")
        self.assertEqual(len(list(self.parent.glob(".RimPilot.rim-pilot-completed-*"))), 3)
        for path in self.parent.glob(".RimPilot.rim-pilot-completed-*"):
            self.assertEqual(set(item.name for item in path.iterdir()), {"journal.json", "committed"})

    def test_unknown_or_corrupt_workspace_is_never_cleaned_or_adopted(self):
        work = self.parent / INSTALL["WORK"]
        work.mkdir(mode=0o700)
        for command in ("install", "recover"):
            with self.assertRaises((OSError, RuntimeError)):
                self.run_operation(command)
        self.assertTrue(work.is_dir())
        (work / "journal.json").write_bytes(b"{}")
        before = self.snapshot()
        with self.assertRaises((OSError, RuntimeError)):
            self.run_operation("recover")
        self.assertEqual(self.snapshot(), before)

    def test_unknown_changed_and_linked_quarantined_files_block_recovery_cleanup(self):
        for kind in ("unknown", "changed", "link"):
            with self.subTest(kind=kind):
                # Each case gets a fresh parent; preserve refused workspace for inspection.
                if kind != "unknown":
                    self.parent = self.base / ("Mods-" + kind)
                    self.parent.mkdir()
                    self.target = self.parent / "RimPilot"
                self.run_operation("install")
                self.interrupt("uninstall", "quarantined")
                old = self.parent / INSTALL["WORK"] / "old"
                if kind == "unknown":
                    (old / "user.txt").write_text("keep")
                elif kind == "changed":
                    (old / "Assemblies/RimPilot.Mod.dll").write_text("keep modified")
                else:
                    (old / "Assemblies/RimPilot.Mod.dll").unlink()
                    (old / "Assemblies/RimPilot.Mod.dll").symlink_to(self.base / "profile/Config/Prefs.xml")
                before = self.snapshot()
                with self.assertRaises((OSError, RuntimeError)):
                    self.run_operation("recover")
                self.assertEqual(self.snapshot(), before)

    def test_target_and_workspace_collisions_never_overwrite_or_remove_unknowns(self):
        def collide(name):
            if name == "before-publish":
                self.target.mkdir()
                self.target.chmod(0o755)
                (self.target / "user.txt").write_text("keep")
        with patch.dict(STATE, {"boundary": collide}):
            with self.assertRaises(OSError):
                self.run_operation("install")
        before = self.snapshot()
        with self.assertRaisesRegex(RuntimeError, "Unknown target collision"):
            self.run_operation("recover")
        self.assertEqual(self.snapshot(), before)
        self.assertEqual((self.target / "user.txt").read_text(), "keep")

    def test_quarantine_replacement_detected_and_unknown_object_restored(self):
        self.run_operation("install")
        def replace(name):
            if name == "before-quarantine":
                self.target.rename(self.parent / "original-owned")
                self.target.mkdir()
                self.target.chmod(0o755)
                (self.target / "user.txt").write_text("keep")
        with patch.dict(STATE, {"boundary": replace}):
            with self.assertRaisesRegex(RuntimeError, "identity changed"):
                self.run_operation("uninstall")
        self.assertEqual((self.target / "user.txt").read_text(), "keep")
        self.assertTrue((self.parent / "original-owned/Assemblies/RimPilot.Mod.dll").is_file())

    def test_modification_immediately_before_delete_is_not_removed(self):
        self.run_operation("install")
        def modify(name):
            if name == "before-delete:About/About.xml":
                (self.parent / INSTALL["WORK"] / "old/About/About.xml").write_text("keep changed")
        with patch.dict(STATE, {"boundary": modify}):
            with self.assertRaisesRegex(RuntimeError, "Modified installed file"):
                self.run_operation("uninstall")
        self.assertEqual((self.parent / INSTALL["WORK"] / "old/About/About.xml").read_text(), "keep changed")

    def test_no_replace_failure_and_disk_full_leave_recoverable_or_untouched_state(self):
        self.run_operation("install")
        before = self.snapshot(self.target)
        real_write = STATE["write"]
        def disk_full(parent, name, content, mode=0o644):
            if name == "RimPilot.Mod.dll":
                raise OSError(errno.ENOSPC, "injected disk full")
            return real_write(parent, name, content, mode)
        with patch.dict(STATE, {"write": disk_full}):
            with self.assertRaises(OSError):
                self.run_operation("reinstall")
        self.assertEqual(self.snapshot(self.target), before)
        rename = STATE["no_replace"]
        def fail_publish(source_fd, source, destination_fd, destination):
            if source == "new":
                raise OSError(errno.EIO, "injected rename failure")
            rename(source_fd, source, destination_fd, destination)
        with patch.dict(STATE, {"no_replace": fail_publish}):
            with self.assertRaises(OSError):
                self.run_operation("reinstall")
        self.run_operation("recover")
        self.assert_installed()

    def test_real_process_exit_recovers_published_transaction_and_partial_removal(self):
        code = '''
import os, runpy, sys
state = runpy.run_path(sys.argv[1], run_name="killed_installer")
def stop(name):
    if name == sys.argv[3]:
        os._exit(73)
state["operate"].__globals__["boundary"] = stop
state["operate"](sys.argv[2], *map(state["Path"], sys.argv[4:]))
'''
        for command, point in (("install", "journal-published"), ("reinstall", "quarantined"),
                               ("reinstall", "published"), ("uninstall", "deleted:About/About.xml")):
            if command != "install":
                self.run_operation("install")
            result = subprocess.run([sys.executable, "-c", code, str(ROOT / "scripts/install.py"),
                                     command, point, str(self.target), str(self.fixture.output),
                                     str(self.fixture.root), str(self.fixture.audit)],
                                    capture_output=True, text=True, timeout=15)
            self.assertEqual(result.returncode, 73, result.stderr)
            self.run_operation("recover")
            if command != "uninstall":
                self.assert_installed()
                self.run_operation("uninstall")
            self.assertFalse(self.target.exists())

    def test_workspace_link_extra_content_and_modified_journal_refuse_recovery(self):
        self.interrupt("install", "journal-published")
        work = self.parent / INSTALL["WORK"]
        journal = work / "journal.json"
        original = journal.read_bytes()
        for content in (b"{}", original.replace(b'"format": 1,', b'"format": true,'),
                        original.replace(b'"operation": "install"', b'"operation": "uninstall"'),
                        original.replace(b'"format": 1,', b'"format": 1, "format": 1,')):
            journal.write_bytes(content)
            before = self.snapshot()
            with self.assertRaises((OSError, RuntimeError)):
                self.run_operation("recover")
            self.assertEqual(self.snapshot(), before)
        journal.write_bytes(original)
        (work / "user-file").write_text("keep")
        before = self.snapshot()
        with self.assertRaisesRegex(RuntimeError, "Unknown recovery workspace"):
            self.run_operation("recover")
        self.assertEqual(self.snapshot(), before)
        (work / "user-file").unlink()
        saved = self.parent / "saved-work"
        work.rename(saved)
        work.symlink_to(saved, target_is_directory=True)
        with self.assertRaises(OSError):
            self.run_operation("recover")
        self.assertEqual((saved / "journal.json").read_bytes(), original)

    def test_same_content_tree_replacement_is_not_adopted_and_partial_cleanup_checks_unknowns(self):
        self.run_operation("install")
        self.interrupt("reinstall", "journal-published")
        self.target.rename(self.parent / "moved-original")
        import shutil
        shutil.copytree(self.parent / "moved-original", self.target)
        before = self.snapshot()
        with self.assertRaisesRegex(RuntimeError, "Unknown target collision"):
            self.run_operation("recover")
        self.assertEqual(self.snapshot(), before)
        # Separate transaction: unknown content appearing after commit still prevents deletion.
        self.parent = self.base / "Mods-partial"
        self.parent.mkdir()
        self.target = self.parent / "RimPilot"
        self.run_operation("install")
        self.interrupt("uninstall", "deleted:About/About.xml")
        old = self.parent / INSTALL["WORK"] / "old"
        (old / "user-file").write_text("keep")
        before = self.snapshot()
        with self.assertRaisesRegex(RuntimeError, "Unknown installed file"):
            self.run_operation("recover")
        self.assertEqual(self.snapshot(), before)

    def test_concurrent_workspace_collision_preserves_other_transaction(self):
        def collide(name):
            if name == "prepared":
                work = self.parent / INSTALL["WORK"]
                work.mkdir(mode=0o700)
                (work / "user-file").write_text("keep")
        with patch.dict(STATE, {"boundary": collide}):
            with self.assertRaises(OSError):
                self.run_operation("install")
        self.assertFalse(self.target.exists())
        self.assertEqual((self.parent / INSTALL["WORK"] / "user-file").read_text(), "keep")

    def test_failed_verification_does_not_leak_owned_file_descriptors(self):
        self.run_operation("install")
        (self.target / "user-file").write_text("keep")
        before = len(os.listdir("/proc/self/fd"))
        for _ in range(12):
            with self.assertRaises(RuntimeError):
                self.run_operation("uninstall")
        self.assertEqual(len(os.listdir("/proc/self/fd")), before)

    def test_restrictive_umask_still_creates_exact_owned_modes(self):
        previous = os.umask(0o077)
        try:
            self.run_operation("install")
            self.assert_installed()
            self.run_operation("uninstall")
        finally:
            os.umask(previous)

    def test_parallel_installer_directory_lock_refuses_before_mutation(self):
        fd = INSTALL["target_parent"](self.target)
        try:
            result = subprocess.run([sys.executable, str(ROOT / "scripts/install.py"), "uninstall",
                                     "--target", str(self.target)], capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 1)
            self.assertIn("Filesystem operation refused/failed.", result.stderr)
            self.assertNotIn(str(self.parent), result.stderr)
        finally:
            os.close(fd)
        self.assertFalse(self.target.exists())

    def test_cli_requires_explicit_arguments_and_never_autodiscovers(self):
        for arguments in ([], ["install"], ["install", "--target", str(self.target)], ["uninstall"]):
            result = subprocess.run([sys.executable, str(ROOT / "scripts/install.py"), *arguments],
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 2)
        self.assertFalse(self.target.exists())


if __name__ == "__main__":
    unittest.main()
