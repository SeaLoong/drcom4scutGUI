"""Package only a fresh dotnet publish directory, never user configuration."""

import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import xml.etree.ElementTree as ET
import zipfile


ROOT = pathlib.Path(__file__).resolve().parents[2]
MINIMUM_CORE_VERSION = "0.4.1"


def version():
    value = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if value is None or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", value):
        raise SystemExit("Directory.Build.props must declare a semantic Version")
    return value


def source_revision():
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    expected = os.environ.get("GITHUB_SHA")
    if not re.fullmatch(r"[0-9a-f]{40,64}", commit) or (expected and expected != commit):
        raise SystemExit("Source checkout does not match the build commit")
    dirty = bool(subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=normal"], cwd=ROOT, text=True
    ).strip())
    return commit, dirty


def package(publish, output):
    publish = publish.resolve(strict=True)
    runtime = "win-x64"
    current_version = version()
    required = (
        "drcom4scutGUI.exe", "drcom4scutGUI.dll", "drcom4scutGUI.deps.json",
        "drcom4scutGUI.runtimeconfig.json", "coreclr.dll", "hostfxr.dll",
        "PresentationFramework.dll",
    )
    for name in required:
        if not (publish / name).is_file():
            raise SystemExit(f"Publish output is missing {name}")
    deps = json.loads((publish / "drcom4scutGUI.deps.json").read_text(encoding="utf-8"))
    if not deps.get("runtimeTarget", {}).get("name", "").endswith(f"/{runtime}"):
        raise SystemExit("Publish output targets an unexpected runtime")
    if f"drcom4scutGUI/{current_version}" not in deps.get("libraries", {}):
        raise SystemExit("Publish output does not match the source version")
    # dotnet publish has no user data. Refuse to accidentally ship a hand-edited
    # deployment directory containing account/configuration files.
    forbidden = {
        "gui.json", "drcom4scutgui.json", "config.json", "accounts.json",
        "config.yml", "config.yaml", "core.yml", "core.yaml",
    }
    files = sorted(path for path in publish.rglob("*") if path.is_file())
    if any(path.name.lower() in forbidden or path.is_symlink() for path in files):
        raise SystemExit("Publish output contains user configuration or symbolic links")
    commit, dirty = source_revision()
    stem = f"drcom4scutGUI-{current_version}-{runtime}"
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"{stem}.zip"
    metadata = {
        "version": current_version,
        "runtime": runtime,
        "source_commit": commit,
        "source_dirty": dirty,
        "minimum_core_version": MINIMUM_CORE_VERSION,
        "self_contained": True,
        "core_included": False,
    }
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as target:
        for path in files:
            target.write(path, f"{stem}/{path.relative_to(publish).as_posix()}")
        for name in ("README.md", "LICENSE"):
            target.write(ROOT / name, f"{stem}/{name}")
        # README links must also work in an extracted release, without a checkout.
        for path in sorted((ROOT / "docs").rglob("*")):
            if path.is_file():
                if path.is_symlink():
                    raise SystemExit("Documentation contains symbolic links")
                target.write(path, f"{stem}/{path.relative_to(ROOT).as_posix()}")
        target.writestr(f"{stem}/BUILD-INFO.json", json.dumps(metadata, indent=2) + "\n")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
    print(f"Created {archive.name}")
    return archive


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-dir", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, default=ROOT / "dist")
    args = parser.parse_args()
    package(args.publish_dir, args.output)


if __name__ == "__main__":
    main()
