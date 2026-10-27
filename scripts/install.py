#!/usr/bin/env python3
"""Explicit-target Linux mod installation; never edits game configuration or launches it."""
import argparse
import ctypes
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import stat

_spec = importlib.util.spec_from_file_location("rim_pilot_package", Path(__file__).with_name("package.py"))
package = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(package)

RECEIPT = ".rim-pilot-owned.json"
WORK = ".RimPilot.rim-pilot-operation"
MARKER = b"rim-pilot transaction commit v1\n"
PREFIX = "mod/RimPilot/"
# Independent installed-mod allowlist, not supplied by a receipt or package manifest.
FILES = {"About/About.xml": 0o644, "Assemblies/RimPilot.Mod.dll": 0o644,
         "Assemblies/Newtonsoft.Json.dll": 0o644,
         "licenses/Newtonsoft.Json.LICENSE.md": 0o644}
DIRS = ["About", "Assemblies", "licenses"]
MAX_JOURNAL = 32768


def fail(message):
    raise RuntimeError(message)


def boundary(name):
    """Failure-injection seam; production has no environment-controlled test hooks."""


def encode(value):
    return (json.dumps(value, sort_keys=True, indent=2) + "\n").encode("utf-8")


def no_replace(source_fd, source, destination_fd, destination):
    libc = ctypes.CDLL(None, use_errno=True)
    rename = libc.renameat2
    rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
    rename.restype = ctypes.c_int
    if rename(source_fd, os.fsencode(source), destination_fd, os.fsencode(destination), 1):
        raise OSError(ctypes.get_errno(), "Atomic no-overwrite installer rename failed.")
    os.fsync(source_fd)
    os.fsync(destination_fd)


def info(fd):
    value = os.fstat(fd)
    return [value.st_dev, value.st_ino]


def exists(fd, name):
    try:
        os.stat(name, dir_fd=fd, follow_symlinks=False)
        return True
    except FileNotFoundError:
        return False


def directory(parent, name, mode=None):
    fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
    value = os.fstat(fd)
    if (value.st_uid != os.getuid() or value.st_mode & 0o022 or
            (mode is not None and stat.S_IMODE(value.st_mode) != mode)):
        os.close(fd)
        fail("Installer directory ownership/mode refused.")
    return fd


def read(parent, name, maximum=package.MAX_FILE, mode=0o644):
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=parent)
    try:
        before = os.fstat(fd)
        if (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or
                before.st_uid != os.getuid() or stat.S_IMODE(before.st_mode) != mode or
                not 0 < before.st_size <= maximum):
            fail("Installer requires bounded owned regular files with exact modes and no links.")
        chunks, total = [], 0
        while True:
            part = os.read(fd, min(1024 * 1024, maximum + 1 - total))
            if not part:
                break
            chunks.append(part)
            total += len(part)
            if total > maximum:
                fail("Installer file exceeded limit.")
        after = os.fstat(fd)
        if (total != before.st_size or
                (before.st_size, before.st_mtime_ns, before.st_ctime_ns) !=
                (after.st_size, after.st_mtime_ns, after.st_ctime_ns)):
            fail("Installer file changed during verification.")
        return b"".join(chunks)
    finally:
        os.close(fd)


def write(parent, name, content, mode=0o644):
    fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                 mode, dir_fd=parent)
    try:
        os.fchmod(fd, mode)
        view = memoryview(content)
        while view:
            written = os.write(fd, view)
            if written <= 0:
                fail("Installer short write.")
            view = view[written:]
        os.fsync(fd)
    finally:
        os.close(fd)
    os.fsync(parent)


def validate_receipt(value):
    if (not isinstance(value, dict) or set(value) !=
            {"format", "owner", "packageId", "version", "directories", "files"} or
            type(value["format"]) is not int or value["format"] != 1 or
            value["owner"] != "rim-pilot" or value["packageId"] != "rimpilot.bridge" or
            not isinstance(value["version"], str) or
            not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", value["version"]) or
            value["directories"] != DIRS or not isinstance(value["files"], list) or
            len(value["files"]) != len(FILES)):
        fail("Invalid installed ownership receipt.")
    seen = set()
    for item in value["files"]:
        if (not isinstance(item, dict) or set(item) != {"path", "size", "sha256", "mode"} or
                not isinstance(item["path"], str) or item["path"] not in FILES or item["path"] in seen or
                type(item["size"]) is not int or not 0 < item["size"] <= package.MAX_FILE or
                item["mode"] != format(FILES[item["path"]], "04o") or
                not isinstance(item["sha256"], str) or not re.fullmatch(r"[a-f0-9]{64}", item["sha256"])):
            fail("Invalid installed file ownership entry.")
        seen.add(item["path"])
    return value


def load_receipt(fd):
    raw = read(fd, RECEIPT, package.MAX_MANIFEST)
    receipt = validate_receipt(package.strict_json(raw))
    if raw != encode(receipt):
        fail("Installed receipt is not canonical; refusing modified ownership proof.")
    return receipt


def verify_tree(fd, receipt, partial=False):
    """No recursive deletion: every surviving path must have independent ownership proof."""
    validate_receipt(receipt)
    expected = {item["path"]: item for item in receipt["files"]}
    expected[RECEIPT] = {"size": len(encode(receipt)), "sha256": package.digest(encode(receipt)), "mode": "0644"}
    seen_files, seen_dirs = set(), set()
    def visit(current, prefix):
        for name in os.listdir(current):
            relative = f"{prefix}/{name}" if prefix else name
            value = os.stat(name, dir_fd=current, follow_symlinks=False)
            if stat.S_ISDIR(value.st_mode):
                if relative not in DIRS:
                    fail("Unknown installed directory; refusing changes.")
                child = directory(current, name, 0o755)
                try:
                    visit(child, relative)
                finally:
                    os.close(child)
                seen_dirs.add(relative)
            else:
                if relative not in expected:
                    fail("Unknown installed file; refusing changes.")
                item = expected[relative]
                content = read(current, name, mode=int(item["mode"], 8))
                if len(content) != item["size"] or package.digest(content) != item["sha256"]:
                    fail("Modified installed file; refusing changes.")
                seen_files.add(relative)
    if stat.S_IMODE(os.fstat(fd).st_mode) != 0o755:
        fail("Installed root mode differs from policy.")
    visit(fd, "")
    if not partial and (seen_files != set(expected) or seen_dirs != set(DIRS)):
        fail("Installed ownership files/directories missing; refusing changes.")


def target_parent(target):
    target = Path(target)
    if not target.is_absolute() or target.name != "RimPilot" or ".." in target.parts:
        fail("Explicit absolute target ending in /RimPilot required; existing parent only.")
    fd = package.open_path(target.parent, directory=True)
    value = os.fstat(fd)
    if value.st_uid != os.getuid() or value.st_mode & 0o022:
        os.close(fd)
        fail("Target parent must be owned by current user and not group/world writable.")
    try:
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BaseException:
        os.close(fd)
        raise
    return fd


def make_payload(work, contents, receipt):
    os.mkdir("new", 0o755, dir_fd=work)
    fd = directory(work, "new")
    try:
        os.fchmod(fd, 0o755)
        for name in DIRS:
            os.mkdir(name, 0o755, dir_fd=fd)
            child = directory(fd, name)
            try:
                os.fchmod(child, 0o755)
                for relative, content in contents.items():
                    folder, filename = relative.split("/")
                    if folder == name:
                        write(child, filename, content)
                        boundary("stage-file:" + relative)
            finally:
                os.close(child)
        write(fd, RECEIPT, encode(receipt))
        verify_tree(fd, receipt)
        os.fsync(fd)
        return info(fd)
    finally:
        os.close(fd)


def source_payload(folder, root, audit):
    manifest = package.verify(Path(folder), root, audit)
    contents = {name: package.read_file(Path(folder) / (PREFIX + name)) for name in FILES}
    entries = {entry["path"]: entry for entry in manifest["files"]}
    for name, content in contents.items():
        expected = entries[PREFIX + name]
        if len(content) != expected["size"] or package.digest(content) != expected["sha256"]:
            fail("Package changed after independent verification.")
    receipt = {"format": 1, "owner": "rim-pilot", "packageId": "rimpilot.bridge",
               "version": manifest["version"], "directories": DIRS,
               "files": [{"path": name, "size": len(contents[name]),
                          "sha256": package.digest(contents[name]), "mode": format(FILES[name], "04o")}
                         for name in sorted(FILES)]}
    return contents, validate_receipt(receipt)


def validate_journal(value):
    if (not isinstance(value, dict) or set(value) != {"format", "operation", "old", "new"} or
            type(value["format"]) is not int or value["format"] != 1 or
            value["operation"] not in ("install", "reinstall", "uninstall")):
        fail("Invalid recovery journal.")
    for key in ("old", "new"):
        state = value[key]
        required = value["operation"] != ("install" if key == "old" else "uninstall")
        if not required:
            if state is not None:
                fail("Unexpected recovery tree.")
        elif (not isinstance(state, dict) or set(state) != {"identity", "receipt"} or
              not isinstance(state["identity"], list) or len(state["identity"]) != 2 or
              any(type(number) is not int or number < 0 for number in state["identity"])):
            fail("Invalid recovery tree identity.")
        else:
            validate_receipt(state["receipt"])
    return value


def check_tree(parent, name, state, partial=False):
    fd = directory(parent, name, 0o755)
    try:
        if info(fd) != state["identity"]:
            fail("Installed tree identity changed; refusing collision/replacement.")
        verify_tree(fd, state["receipt"], partial)
    finally:
        os.close(fd)


def delete_old(work, state):
    if not exists(work, "old"):
        return
    check_tree(work, "old", state, partial=True)
    fd = directory(work, "old", 0o755)
    try:
        # Reverify the complete remaining tree before every destructive step.
        for folder in DIRS:
            if not exists(fd, folder):
                continue
            child = directory(fd, folder, 0o755)
            try:
                for name in os.listdir(child):
                    boundary("before-delete:" + folder + "/" + name)
                    verify_tree(fd, state["receipt"], partial=True)
                    os.unlink(name, dir_fd=child)
                    os.fsync(child)
                    boundary("deleted:" + folder + "/" + name)
            finally:
                os.close(child)
            os.rmdir(folder, dir_fd=fd)
            os.fsync(fd)
            boundary("deleted-dir:" + folder)
        if exists(fd, RECEIPT):
            verify_tree(fd, state["receipt"], partial=True)
            os.unlink(RECEIPT, dir_fd=fd)
            os.fsync(fd)
            boundary("deleted-receipt")
    finally:
        os.close(fd)
    os.rmdir("old", dir_fd=work)
    os.fsync(work)
    boundary("deleted-old")


def resume(parent):
    work = directory(parent, WORK, 0o700)
    try:
        journal_raw = read(work, "journal.json", MAX_JOURNAL, 0o600)
        journal = validate_journal(package.strict_json(journal_raw))
        if journal_raw != encode(journal):
            fail("Modified recovery journal refused.")
        names = set(os.listdir(work))
        committed = "committed" in names
        marker = "committed" if committed else "commit-ready"
        if (names - {"journal.json", marker, "old", "new"} or
                read(work, marker, len(MARKER), 0o600) != MARKER):
            fail("Unknown recovery workspace content refused.")
        old, new = journal["old"], journal["new"]
        if (old is None and "old" in names) or (new is None and "new" in names):
            fail("Unexpected recovery workspace tree.")
        target_state = None
        if exists(parent, "RimPilot"):
            fd = directory(parent, "RimPilot", 0o755)
            try:
                identity = info(fd)
            finally:
                os.close(fd)
            target_state = next((key for key in ("old", "new")
                                 if journal[key] is not None and journal[key]["identity"] == identity), None)
            if target_state is None:
                fail("Unknown target collision during recovery; no overwrite allowed.")
            check_tree(parent, "RimPilot", journal[target_state])
        if "new" in names:
            check_tree(work, "new", new)
        if "old" in names:
            check_tree(work, "old", old, partial=committed)
        if committed:
            if target_state != ("new" if new else None) or "new" in names:
                fail("Committed recovery target differs; refusing changes.")
        else:
            # Validate the full state before any mutation, including same-version reinstall.
            if old and ((target_state == "old") == ("old" in names)):
                fail("Recovery requires exactly one intact old tree.")
            if new and ((target_state == "new") == ("new" in names)):
                fail("Recovery requires exactly one intact new tree.")
            if target_state == "old":
                boundary("before-quarantine")
                no_replace(parent, "RimPilot", work, "old")
                boundary("quarantined")
                try:
                    check_tree(work, "old", old)
                except BaseException:
                    # Preserve an object swapped in immediately before quarantine; never delete it.
                    if not exists(parent, "RimPilot"):
                        no_replace(work, "old", parent, "RimPilot")
                    raise
            if new and target_state != "new":
                boundary("before-publish")
                check_tree(work, "new", new)
                no_replace(work, "new", parent, "RimPilot")
                boundary("published")
                check_tree(parent, "RimPilot", new)
            elif not new and exists(parent, "RimPilot"):
                fail("Uninstall target collision; refusing cleanup.")
            no_replace(work, "commit-ready", work, "committed")
            boundary("committed")
        if old:
            delete_old(work, old)
        # Detach a completed journal atomically before final bookkeeping deletion.
        # Crashes here may leave a harmless completed workspace, never block the next operation.
        done = ".RimPilot.rim-pilot-completed-" + secrets.token_hex(12)
        no_replace(parent, WORK, parent, done)
        boundary("completed")
        for name in ("committed", "journal.json"):
            os.unlink(name, dir_fd=work)
            os.fsync(work)
        os.rmdir(done, dir_fd=parent)
        os.fsync(parent)
    finally:
        os.close(work)


def operate(command, target, folder=None, root=package.ROOT, audit=package.AUDIT):
    if command not in ("install", "reinstall", "uninstall", "recover"):
        fail("Unsupported installer operation.")
    parent = target_parent(target)
    try:
        if command == "recover":
            if not exists(parent, WORK):
                fail("No published interrupted operation to recover.")
            resume(parent)
            return
        if exists(parent, WORK):
            fail("Operation workspace collision; use explicit recover after inspection.")
        old = None
        if command == "install":
            if exists(parent, "RimPilot"):
                fail("Install target collision; use reinstall only for verified owned trees.")
        else:
            fd = directory(parent, "RimPilot", 0o755)
            try:
                receipt = load_receipt(fd)
                verify_tree(fd, receipt)
                old = {"identity": info(fd), "receipt": receipt}
            finally:
                os.close(fd)
        contents, receipt = source_payload(folder, root, audit) if command != "uninstall" else (None, None)
        # Private preparation has no effect on target. Retain incomplete preparations on errors;
        # never recursively remove an object lacking a complete journal/ownership proof.
        temporary = ".RimPilot.rim-pilot-prepare-" + secrets.token_hex(12)
        os.mkdir(temporary, 0o700, dir_fd=parent)
        work = directory(parent, temporary)
        try:
            os.fchmod(work, 0o700)
            boundary("prepared-directory")
            new = {"identity": make_payload(work, contents, receipt), "receipt": receipt} if contents else None
            journal = {"format": 1, "operation": command, "old": old, "new": new}
            write(work, "journal.json", encode(validate_journal(journal)), 0o600)
            write(work, "commit-ready", MARKER, 0o600)
            os.fsync(work)
            boundary("prepared")
            no_replace(parent, temporary, parent, WORK)
            boundary("journal-published")
        finally:
            os.close(work)
        resume(parent)
    finally:
        os.close(parent)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for command in ("install", "reinstall", "uninstall", "recover"):
        sub = commands.add_parser(command)
        sub.add_argument("--target", required=True, type=Path, help="explicit /absolute/existing-parent/RimPilot")
        if command in ("install", "reinstall"):
            sub.add_argument("--package", required=True, type=Path)
    args = parser.parse_args()
    try:
        operate(args.command, args.target, getattr(args, "package", None))
    except (OSError, RuntimeError, ValueError, KeyError, TypeError) as error:
        # Avoid printing private filesystem paths from OSError. No credentials/config are read.
        detail = "Filesystem operation refused/failed." if isinstance(error, OSError) else str(error)
        parser.exit(1, f"Installer failed: {detail}\n")
    print("Mod operation complete. No settings/saves/configuration edited; human enable/restart required.")


if __name__ == "__main__":
    main()
