#!/usr/bin/env python3
"""Reproducible asset-free commands; installed-game compilation is explicit and separate."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent
DOTNET = ROOT / "scripts/dotnet"
CLI_OUTPUT = ROOT / "artifacts/cli/linux-x64"
MOD_OUTPUT = ROOT / "mod/bin/Release/net472"
REFERENCES = ROOT / ".local/game-references.props"
NOTICE = "licenses/Newtonsoft.Json.LICENSE.md"
OUTPUTS = {
    "cli": ({"rim-pilot", NOTICE}, {"rim-pilot.pdb", "Newtonsoft.Json.xml"}),
    "mod": ({"RimPilot.Mod.dll", "Newtonsoft.Json.dll", "Newtonsoft.Json.xml", NOTICE}, {"RimPilot.Mod.pdb"}),
}


def run(*arguments, **kwargs):
    subprocess.run([str(arg) for arg in arguments], cwd=ROOT, check=True, **kwargs)


def dotnet(*arguments):
    # No persistent compiler/MSBuild servers after unattended build commands.
    run(DOTNET, *arguments, "--disable-build-servers", "-p:UseSharedCompilation=false")


def verify_output(folder, kind, audit=None):
    """Exact owned-file boundary; no external glob/copy. Not a final-package audit."""
    required, optional = OUTPUTS[kind]
    if not folder.is_dir() or folder.is_symlink():
        raise RuntimeError(f"Missing or symlinked {kind} output directory.")
    files = set()
    for path in folder.rglob("*"):
        if path.is_symlink():
            raise RuntimeError("Output must not contain symlinks.")
        if path.is_file():
            files.add(path.relative_to(folder).as_posix())
    if not required <= files or files - required - optional:
        raise RuntimeError(f"{kind} output differs from explicit redistributable file allowlist.")
    if audit is not None:
        forbidden = {item["Sha256"].lower() for item in json.loads(audit.read_text())["References"]}
        for relative in files:
            with (folder / relative).open("rb") as source:
                if hashlib.file_digest(source, "sha256").hexdigest() in forbidden:
                    raise RuntimeError("Installed game/Harmony content found in output.")


def check():
    for project in ("tools/ReferenceAudit/ReferenceAudit.csproj", "cli/RimPilot.Cli.csproj",
                    "tests/JsonIngress/JsonIngress.csproj"):
        dotnet("build", project, "-c", "Release", "-p:RestoreLockedMode=true")
    run(sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py", "-v")


def build():
    dotnet("publish", "cli/RimPilot.Cli.csproj", "-c", "Release", "-p:RestoreLockedMode=true", "-o", CLI_OUTPUT)
    verify_output(CLI_OUTPUT, "cli")


def test():
    run(sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py", "-v")
    dotnet("run", "--project", "tests/JsonIngress", "-c", "Release", "-p:RestoreLockedMode=true")
    build()
    build()  # Repeat publish must preserve external redistribution notice.
    smoke_cli()
    print("Game compilation/runtime tests NOT RUN by asset-free test; use build-game for compilation only.")


def smoke_cli():
    # Run operational commands against an owned empty registry, never host sessions.
    # No SDK/shared-runtime lookup; usage/no-session failures are not observations.
    (ROOT / ".local").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="cli-smoke-") as temporary:
        runtime = Path(temporary) / "runtime"
        runtime.mkdir(mode=0o700)
        home = Path(temporary) / "home"
        home.mkdir(mode=0o700)
        env = os.environ.copy()
        env.update({"DOTNET_ROOT": temporary, "DOTNET_ROOT_X64": temporary,
                    "DOTNET_MULTILEVEL_LOOKUP": "0", "DOTNET_BUNDLE_EXTRACT_BASE_DIR": temporary,
                    "PATH": temporary, "HOME": str(home), "XDG_RUNTIME_DIR": str(runtime)})
        cases = (([], 64, "", "usage: {sessions|health|capabilities|summary|pawns.list|pawn.get|things.list|resources|ui|events.read|screenshot} [--session SESSION] [--root ROOT --game GAME --map MAP] [--pawn PAWN] [--limit 1..64 --cursor CURSOR] [--output LOCAL_PATH]"),
                 (["sessions"], 69, {"v": 1, "sessions": []}, "no_live_session."),
                 (["health"], 69, "", "no_live_session."),
                 (["capabilities"], 69, "", "no_live_session."),
                 (["summary"], 69, "", "no_live_session."),
                 (["ui"], 69, "", "no_live_session."),
                 (["events.read"], 69, "", "no_live_session."),
                 (["screenshot", "--output", "capture.png"], 69, "", "no_live_session."))
        for arguments, code, output, diagnostic in cases:
            result = subprocess.run([str(CLI_OUTPUT / "rim-pilot"), *arguments], cwd=temporary, env=env,
                                    capture_output=True, text=True, timeout=30)
            if (result.returncode != code or result.stderr != f"rim-pilot: {diagnostic}\n"
                    or (json.loads(result.stdout) if isinstance(output, dict) else result.stdout) != output):
                raise RuntimeError("Self-contained CLI usage/empty-registry smoke failed.")
        registry = runtime / "rim-pilot/v1"
        if not registry.is_dir() or any(registry.iterdir()):
            raise RuntimeError("Self-contained CLI smoke did not use its empty private registry.")
    print("Published CLI usage/bootstrap/observation refusals ran SDK-free against scratch registry; no observation pass.")


def build_game():
    game, harmony = os.environ.get("RIMWORLD_DIR"), os.environ.get("HARMONY_MOD_DIR")
    if not game or not harmony or not Path(game).is_absolute() or not Path(harmony).is_absolute():
        raise RuntimeError("build-game requires absolute RIMWORLD_DIR and HARMONY_MOD_DIR; no game auto-discovery.")
    run(DOTNET, "run", "--project", "tools/ReferenceAudit", "-c", "Release",
        "--disable-build-servers", "-p:UseSharedCompilation=false", "--", game, harmony, REFERENCES)
    dotnet("build", "mod/RimPilot.Mod.csproj", "-c", "Release", "-p:RestoreLockedMode=true")
    audit = Path(str(REFERENCES) + ".audit.json")
    verify_output(MOD_OUTPUT, "mod", audit)
    # Negative build: even accidentally enabled copy-local must fail before copy.
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="copy-local-test-") as temporary:
        poisoned = Path(temporary) / "references.props"
        text = REFERENCES.read_text()
        if "<Private>false</Private>" not in text:
            raise RuntimeError("No non-copy-local references available for rejection test.")
        poisoned.write_text(text.replace("<Private>false</Private>", "<Private>true</Private>"))
        result = subprocess.run([str(DOTNET), "build", "mod/RimPilot.Mod.csproj", "-c", "Release",
                                 "--no-restore", "--disable-build-servers", "-p:UseSharedCompilation=false",
                                 f"-p:GameReferencesFile={poisoned}"],
                                cwd=ROOT, capture_output=True, text=True)
        if result.returncode == 0 or "Game/Harmony dependencies must never be copy-local." not in result.stdout + result.stderr:
            raise RuntimeError("Configured copy-local rejection test failed.")
    verify_output(MOD_OUTPUT, "mod", audit)
    if CLI_OUTPUT.exists():
        verify_output(CLI_OUTPUT, "cli", audit)
    print("Actual net472 mod compiled; copy-local negative and output boundary passed. No Mono/game execution.")


def package():
    # A package always gets freshly audited configured mod inputs and published CLI.
    build_game()
    build()
    run(sys.executable, ROOT / "scripts/package.py", "build")


def verify_package():
    run(sys.executable, ROOT / "scripts/package.py", "verify")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "test", "build", "build-game", "package", "verify-package"))
    command = parser.parse_args().command
    {"check": check, "test": test, "build": build, "build-game": build_game,
     "package": package, "verify-package": verify_package}[command]()


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"Project command failed: {error}", file=sys.stderr)
        sys.exit(1)
