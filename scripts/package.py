#!/usr/bin/env python3
"""Build/verify a complete development package against independent trusted inputs."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
OUTPUT = ROOT / "artifacts/package/rim-pilot-linux-x64"
AUDIT = ROOT / ".local/game-references.props.audit.json"
MANIFEST = "ownership.json"
OWNER = "rim-pilot"
MAX_FILE = 192 * 1024 * 1024
MAX_MANIFEST = 16384
# Not derived from ownership.json. No generic DLL, asset or documentation glob.
# relative path -> (trusted source relative to checkout, component, required mode)
FILES = {
    "mod/RimPilot/About/About.xml": ("mod/About/About.xml", "mod", 0o644),
    "mod/RimPilot/Assemblies/RimPilot.Mod.dll": ("mod/bin/Release/net472/RimPilot.Mod.dll", "mod", 0o644),
    "mod/RimPilot/Assemblies/Newtonsoft.Json.dll": ("mod/bin/Release/net472/Newtonsoft.Json.dll", "newtonsoft", 0o644),
    "mod/RimPilot/licenses/Newtonsoft.Json.LICENSE.md": ("licenses/Newtonsoft.Json.LICENSE.md", "newtonsoft", 0o644),
    "cli/rim-pilot": ("artifacts/cli/linux-x64/rim-pilot", "cli", 0o755),
    "licenses/Newtonsoft.Json.LICENSE.md": ("licenses/Newtonsoft.Json.LICENSE.md", "newtonsoft", 0o644),
    "licenses/dotnet/LICENSE.TXT": (None, "dotnet", 0o644),
    "licenses/dotnet/THIRD-PARTY-NOTICES.TXT": (None, "dotnet", 0o644),
    "docs/package.md": ("docs/package.md", "docs", 0o644),
    "docs/usage.md": ("packaging/USAGE.md", "docs", 0o644),
}


def fail(message):
    raise RuntimeError(message)


def valid_relative(value):
    if (not isinstance(value, str) or not value or "\\" in value or
            any(part in ("", ".", "..") for part in value.split("/")) or
            PurePosixPath(value).is_absolute()):
        fail("Invalid package-relative path.")
    return value


def open_path(path, directory=False):
    """No-follow every ancestor and final component, including absolute input paths."""
    if ".." in Path(path).parts:
        fail("Package filesystem path traversal forbidden.")
    path = Path(os.path.abspath(path))
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        for index, part in enumerate(path.parts[1:]):
            flags = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
            if directory or index < len(path.parts) - 2:
                flags |= os.O_DIRECTORY
            next_fd = os.open(part, flags, dir_fd=fd)
            os.close(fd)
            fd = next_fd
        return fd
    except BaseException:
        os.close(fd)
        raise


def read_file(path, maximum=MAX_FILE):
    fd = open_path(path)
    try:
        before = os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or not 0 < before.st_size <= maximum:
            fail("Package inputs must be bounded regular files without hardlinks.")
        with os.fdopen(fd, "rb", closefd=False) as stream:
            content = stream.read(maximum + 1)
        after = os.fstat(fd)
        if (len(content) != before.st_size or len(content) > maximum or
                (before.st_mtime_ns, before.st_ctime_ns, before.st_size) !=
                (after.st_mtime_ns, after.st_ctime_ns, after.st_size)):
            fail("Package file changed during inspection.")
        return content
    finally:
        os.close(fd)


def strict_json(content):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                fail("Duplicate JSON key.")
            result[key] = value
        return result
    try:
        return json.loads(content, object_pairs_hook=pairs,
                          parse_constant=lambda _: fail("Invalid JSON number."))
    except (ValueError, UnicodeError, RecursionError) as error:
        raise RuntimeError("Invalid package JSON.") from error


def digest(content):
    return hashlib.sha256(content).hexdigest()


def directories():
    return sorted({parent.as_posix() for name in FILES for parent in PurePosixPath(name).parents
                   if parent != PurePosixPath(".")})


def metadata_version(root):
    props = ET.fromstring(read_file(root / "Directory.Build.props", MAX_MANIFEST))
    version = props.findtext("PropertyGroup/Version")
    if not version or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        fail("Invalid build version.")
    about = ET.fromstring(read_file(root / "mod/About/About.xml", MAX_MANIFEST))
    if (about.tag != "ModMetaData" or about.findtext("packageId") != "rimpilot.bridge" or
            about.findtext("modVersion") != version or
            [node.text for node in about.findall("supportedVersions/li")] != ["1.6"] or
            [node.text for node in about.findall("modDependencies/li/packageId")] != ["brrainz.harmony"] or
            [node.text for node in about.findall("loadAfter/li")] != ["brrainz.harmony"] or
            about.findall("loadBefore/li")):
        fail("Mod version/Harmony dependency metadata differs from package policy.")
    return version


def trusted_inputs(root, audit):
    """Fresh build/source files are authority; package manifest is never authority."""
    version = metadata_version(root)
    pins = strict_json(read_file(root / "packaging/redistributables.json", MAX_MANIFEST))
    cli = ET.fromstring(read_file(root / "cli/RimPilot.Cli.csproj", MAX_MANIFEST))
    runtime = pins["runtime"]
    if cli.findtext("PropertyGroup/RuntimeFrameworkVersion") != runtime["version"]:
        fail("CLI runtime version differs from redistribution pin.")
    assets = strict_json(read_file(root / "cli/obj/project.assets.json", 1024 * 1024))
    # Restore's explicit package cache, not arbitrary SDK-wide notices or host runtime.
    cache = Path(assets["project"]["restore"]["packagesPath"])
    if not cache.is_absolute():
        fail("Restored runtime cache must be absolute.")
    runtime_folder = cache / runtime["package"] / runtime["version"]
    references = strict_json(read_file(audit, 4 * 1024 * 1024))["References"]
    forbidden = {item["Sha256"].lower() for item in references}
    if not forbidden or not all(re.fullmatch(r"[a-f0-9]{64}", item) for item in forbidden):
        fail("Missing/invalid installed-reference audit hashes.")
    result = {}
    for relative, (source, component, mode) in FILES.items():
        if source is None:
            name = PurePosixPath(relative).name
            content = read_file(runtime_folder / name)
            if digest(content) != runtime["notices"][name]:
                fail("Runtime redistribution notice hash differs from pin.")
        else:
            content = read_file(root / source)
        if component == "newtonsoft" and relative.endswith(".dll"):
            if digest(content) != pins["newtonsoft"]["net45Sha256"]:
                fail("Newtonsoft.Json redistributable hash differs from net45 pin.")
        if digest(content) in forbidden:
            fail("Installed game/Harmony content found in package inputs.")
        result[relative] = content
    return version, result, forbidden


def file_entry(relative, content):
    _, component, mode = FILES[relative]
    return {"path": relative, "owner": OWNER, "component": component,
            "size": len(content), "sha256": digest(content), "mode": format(mode, "04o")}


def inventory(version, contents):
    return {"format": 1, "packageId": "rimpilot.bridge", "version": version,
            "platform": "linux-x64", "owner": OWNER, "manifest": MANIFEST,
            "directories": directories(),
            "files": [file_entry(name, contents[name]) for name in sorted(FILES)]}


def inspect_tree(folder):
    """Walk all entries by directory fd; refuse links, special files and unknown dirs."""
    expected_dirs = set(directories())
    found_files, found_dirs = set(), set()
    def visit(fd, prefix):
        if stat.S_IMODE(os.fstat(fd).st_mode) != 0o755:
            fail("Package directory mode differs from policy.")
        with os.scandir(fd) as entries:
            for entry in entries:
                name = f"{prefix}/{entry.name}" if prefix else entry.name
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISDIR(info.st_mode):
                    if name not in expected_dirs:
                        fail("Complete-package directory allowlist differs.")
                    child = os.open(entry.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
                    try:
                        visit(child, name)
                    finally:
                        os.close(child)
                    found_dirs.add(name)
                elif stat.S_ISREG(info.st_mode) and info.st_nlink == 1:
                    if name not in FILES and name != MANIFEST:
                        fail("Complete-package file allowlist differs.")
                    mode = 0o644 if name == MANIFEST else FILES[name][2]
                    if stat.S_IMODE(info.st_mode) != mode:
                        fail("Package file mode differs from policy.")
                    found_files.add(name)
                else:
                    fail("Package links/hardlinks/special files forbidden.")
    fd = open_path(folder, directory=True)
    try:
        visit(fd, "")
    finally:
        os.close(fd)
    if found_files != set(FILES) | {MANIFEST} or found_dirs != expected_dirs:
        fail("Complete-package allowlist missing files/directories.")


def verify(folder, root=ROOT, audit=AUDIT):
    inspect_tree(folder)
    manifest = strict_json(read_file(folder / MANIFEST, MAX_MANIFEST))
    if not isinstance(manifest, dict) or not isinstance(manifest.get("files"), list):
        fail("Invalid ownership manifest.")
    seen = set()
    for item in manifest["files"]:
        if not isinstance(item, dict):
            fail("Invalid manifest file entry.")
        path = valid_relative(item.get("path"))
        if path in seen or path not in FILES:
            fail("Duplicate/unknown manifest path.")
        seen.add(path)
    if not isinstance(manifest.get("directories"), list):
        fail("Invalid manifest directories.")
    for path in manifest["directories"]:
        valid_relative(path)
    version, trusted, forbidden = trusted_inputs(root, audit)
    for relative in sorted(FILES):
        content = read_file(folder / relative)
        if digest(content) in forbidden:
            fail("Installed game/Harmony content found in complete package.")
        if content != trusted[relative]:
            fail("Package content differs from trusted build/source inputs.")
    # Canonical JSON comparison preserves bool/int/float distinctions unlike dict ==.
    if json.dumps(manifest, sort_keys=True) != json.dumps(inventory(version, trusted), sort_keys=True):
        fail("Ownership/hash manifest differs from independent package policy.")
    return manifest


def ensure_directory(path):
    """Create parents fd-relative, never through an existing symlink."""
    if ".." in Path(path).parts:
        fail("Package filesystem path traversal forbidden.")
    path = Path(os.path.abspath(path))
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        for part in path.parts[1:]:
            try:
                os.mkdir(part, 0o755, dir_fd=fd)
            except FileExistsError:
                pass
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            os.close(fd)
            fd = child
    finally:
        os.close(fd)


def build(folder=OUTPUT, root=ROOT, audit=AUDIT):
    # Configured builds happen in project.py before entering this packaging boundary.
    version, contents, _ = trusted_inputs(root, audit)
    if os.path.lexists(folder):
        fail("Package output already exists; refusing overwrite.")
    ensure_directory(folder.parent)
    with tempfile.TemporaryDirectory(prefix=".package-", dir=folder.parent) as temporary:
        stage = Path(temporary) / "payload"
        stage.mkdir(mode=0o755)
        stage.chmod(0o755)
        for relative, content in contents.items():
            target = stage / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
            target.chmod(FILES[relative][2])
        for relative in directories():
            (stage / relative).chmod(0o755)
        manifest = stage / MANIFEST
        manifest.write_text(json.dumps(inventory(version, contents), indent=2) + "\n", encoding="utf-8")
        manifest.chmod(0o644)
        verify(stage, root, audit)
        # Linux no-replace publication. Never replace even an empty concurrent target.
        import ctypes
        libc = ctypes.CDLL(None, use_errno=True)
        rename = libc.renameat2
        rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        rename.restype = ctypes.c_int
        if rename(-100, os.fsencode(stage), -100, os.fsencode(folder), 1) != 0:
            fail("Atomic no-overwrite package publication failed.")
    verify(folder, root, audit)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("build", "verify"))
    parser.add_argument("--package", type=Path, default=OUTPUT)
    args = parser.parse_args()
    try:
        {"build": build, "verify": verify}[args.command](args.package)
    except (OSError, RuntimeError, ValueError, KeyError, TypeError, ET.ParseError) as error:
        parser.exit(1, f"Package command failed: {error}\n")
    print("Complete package/ownership/redistribution boundary passed; no Mono/game execution.")


if __name__ == "__main__":
    main()
