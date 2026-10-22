"""Validate a self-contained desktop publish, then archive its exact native files."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]
PLATFORMS = {p["runtime"]: p for p in json.loads((ROOT / ".github/scripts/platforms.json").read_text())}
MINIMUM_CORE_VERSION = "0.5.0"


def version():
    value = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if value is None or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", value):
        raise SystemExit("Directory.Build.props must declare a semantic Version")
    return value


def source_revision():
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    expected = os.environ.get("GITHUB_SHA")
    if not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", commit) or (expected and expected != commit):
        raise SystemExit("Source checkout does not match the build commit")
    dirty = bool(subprocess.check_output(
        ["git", "status", "--porcelain", "--untracked-files=normal"], cwd=ROOT, text=True
    ).strip())
    return commit, dirty


def verify_abi(data, platform, *, executable=True):
    """Check architecture and executable type without running a foreign binary."""
    if platform["format"] == "pe":
        if len(data) < 64 or data[:2] != b"MZ":
            raise SystemExit("Expected a Windows PE executable")
        offset = struct.unpack_from("<I", data, 60)[0]
        if offset + 26 > len(data) or data[offset:offset + 4] != b"PE\0\0":
            raise SystemExit("Malformed PE header")
        if struct.unpack_from("<H", data, offset + 4)[0] != platform["machine"]:
            raise SystemExit("PE CPU architecture does not match runtime")
        if struct.unpack_from("<H", data, offset + 24)[0] != 0x20b:
            raise SystemExit("Expected a 64-bit PE image")
    elif platform["format"] == "elf":
        if len(data) < 64 or data[:6] != b"\x7fELF\x02\x01":
            raise SystemExit("Expected a little-endian 64-bit ELF image")
        if struct.unpack_from("<H", data, 18)[0] != platform["machine"]:
            raise SystemExit("ELF CPU architecture does not match runtime")
        if struct.unpack_from("<H", data, 16)[0] not in (2, 3):
            raise SystemExit("Expected an executable or shared ELF image")
    elif platform["format"] == "macho":
        if len(data) < 32 or data[:4] != b"\xcf\xfa\xed\xfe":
            raise SystemExit("Expected a little-endian thin 64-bit Mach-O image")
        if struct.unpack_from("<I", data, 4)[0] != platform["machine"]:
            raise SystemExit("Mach-O CPU architecture does not match runtime")
        if struct.unpack_from("<I", data, 12)[0] != (2 if executable else 6):
            raise SystemExit("Unexpected Mach-O executable/library type")
    else:
        raise SystemExit("Unsupported native executable format")


def executable_name(runtime):
    return "drcom4scutGUI.exe" if runtime.startswith("win-") else "drcom4scutGUI"


def required_files(runtime):
    native = (("coreclr.dll", "hostfxr.dll") if runtime.startswith("win-") else
              (("libcoreclr.dylib", "libhostfxr.dylib") if runtime.startswith("osx-") else
               ("libcoreclr.so", "libhostfxr.so")))
    return (
        executable_name(runtime), "drcom4scutGUI.dll", "drcom4scutGUI.deps.json",
        "drcom4scutGUI.runtimeconfig.json", "Avalonia.Base.dll", "Avalonia.Controls.dll",
        "Avalonia.Themes.Fluent.dll", "Avalonia.Fonts.Inter.dll", *native,
    )


def executable_relative(runtime):
    if runtime.startswith("osx-"):
        return "drcom4scutGUI.app/Contents/MacOS/drcom4scutGUI"
    return executable_name(runtime)


def sign_app_bundle(bundle):
    if sys.platform != "darwin":
        raise SystemExit("macOS packages must be assembled and signed on macOS")
    # NuGet runtime assemblies can inherit execute bits. macOS interprets such
    # managed PE/data files as nested code and refuses to seal the app bundle.
    # Only true Mach-O images belong in its executable set.
    mach_o_magic = {b"\xcf\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca"}
    native_files = []
    executable = bundle / "Contents/MacOS/drcom4scutGUI"
    for path in sorted((bundle / "Contents/MacOS").rglob("*")):
        if path.is_file():
            with path.open("rb") as file:
                native = file.read(4) in mach_o_magic
            path.chmod(0o755 if native else 0o644)
            if native:
                native_files.append(path)
    for path in native_files:
        # Signing the main apphost by itself implicitly signs its enclosing
        # bundle. Leave it until all native children are signed and seal once.
        if path != executable:
            subprocess.run(["codesign", "--force", "--sign", "-", str(path)], check=True, timeout=120)
    subprocess.run(["codesign", "--force", "--deep", "--sign", "-", str(bundle)], check=True, timeout=120)
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True, timeout=60)


def package(publish, output, runtime="win-x64"):
    if runtime not in PLATFORMS:
        raise SystemExit("Unsupported desktop runtime")
    platform = PLATFORMS[runtime]
    publish = Path(publish).resolve(strict=True)
    current_version = version()
    for name in required_files(runtime):
        if not (publish / name).is_file():
            raise SystemExit(f"Publish output is missing {name}")
    deps = json.loads((publish / "drcom4scutGUI.deps.json").read_text(encoding="utf-8"))
    if not deps.get("runtimeTarget", {}).get("name", "").endswith(f"/{runtime}"):
        raise SystemExit("Publish output targets an unexpected runtime")
    if f"drcom4scutGUI/{current_version}" not in deps.get("libraries", {}):
        raise SystemExit("Publish output does not match the source version")
    forbidden = {
        "gui.json", "drcom4scutgui.json", "config.json", "accounts.json", "config.yml",
        "config.yaml", "core.yml", "core.yaml", "drcom4scut", "drcom4scut.exe",
    }
    entries = sorted(publish.rglob("*"))
    if any(path.is_symlink() or not (path.is_file() or path.is_dir())
           or path.name.lower() in forbidden for path in entries):
        raise SystemExit("Publish output contains user configuration, a core or symbolic links")
    for name in (executable_name(runtime), *required_files(runtime)[-2:]):
        verify_abi((publish / name).read_bytes(), platform, executable=name == executable_name(runtime))
    commit, dirty = source_revision()
    metadata = {
        "version": current_version, "runtime": runtime, "source_commit": commit,
        "source_dirty": dirty, "minimum_core_version": MINIMUM_CORE_VERSION,
        "self_contained": True, "core_included": False,
        "macos_signing": "ad-hoc" if runtime.startswith("osx-") else "none",
    }
    stem = f"drcom4scutGUI-{current_version}-{runtime}"
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"{stem}.{platform['archive']}"
    with tempfile.TemporaryDirectory() as directory:
        contents = Path(directory) / stem
        contents.mkdir()
        if runtime.startswith("osx-"):
            bundle = contents / "drcom4scutGUI.app"
            destination = bundle / "Contents/MacOS"
        else:
            bundle = None
            destination = contents
        shutil.copytree(publish, destination, dirs_exist_ok=True)
        for name in ("README.md", "LICENSE"):
            shutil.copy2(ROOT / name, contents / name)
        if (ROOT / "THIRD-PARTY-NOTICES.md").is_file():
            shutil.copy2(ROOT / "THIRD-PARTY-NOTICES.md", contents / "THIRD-PARTY-NOTICES.md")
        if (ROOT / "docs").exists():
            if any(path.is_symlink() for path in (ROOT / "docs").rglob("*")):
                raise SystemExit("Documentation contains symbolic links")
            shutil.copytree(ROOT / "docs", contents / "docs")
        (contents / "BUILD-INFO.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
        if bundle:
            # Keep notices and provenance with the app when a user moves only
            # the .app bundle, while retaining the archive's normal docs entry.
            resources = bundle / "Contents/Resources"
            resources.mkdir()
            for name in ("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "BUILD-INFO.json"):
                if (contents / name).is_file():
                    shutil.copy2(contents / name, resources / name)
            if (contents / "docs").is_dir():
                shutil.copytree(contents / "docs", resources / "docs")
            info = {
                "CFBundleExecutable": "drcom4scutGUI", "CFBundleIdentifier": "com.github.SeaLoong.drcom4scutGUI",
                "CFBundleName": "drcom4scutGUI", "CFBundleDisplayName": "drcom4scutGUI",
                "CFBundlePackageType": "APPL", "CFBundleShortVersionString": current_version.split("-", 1)[0],
                "CFBundleVersion": current_version.split("-", 1)[0], "LSMinimumSystemVersion": "15.0",
                "NSHighResolutionCapable": True,
            }
            (bundle / "Contents/Info.plist").write_bytes(plistlib.dumps(info))
            sign_app_bundle(bundle)
        if platform["archive"] == "zip":
            with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as target:
                for path in sorted(contents.rglob("*")):
                    if path.is_file():
                        target.write(path, path.relative_to(contents.parent).as_posix())
        else:
            with tarfile.open(archive, "w:gz") as target:
                def executable_mode(member):
                    # NTFS cannot represent Unix execute bits when publishing
                    # another RID. The apphost must still be runnable on extract.
                    if member.name == f"{stem}/{executable_relative(runtime)}":
                        member.mode = 0o755
                    return member
                target.add(contents, arcname=stem, filter=executable_mode)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(archive.suffix + ".sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
    print(f"Created {archive.name}")
    return archive


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--runtime", choices=PLATFORMS, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    package(args.publish_dir, args.output, args.runtime)


if __name__ == "__main__":
    main()
