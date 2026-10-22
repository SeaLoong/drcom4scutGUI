"""Verify every desktop archive against the shared platform matrix and source."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import tarfile
import zipfile


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("gui_release_packager", Path(__file__).with_name("package_release.py"))
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)
PLATFORMS = packager.PLATFORMS


def read_members(archive, names):
    """Read required regular members without extracting untrusted archive paths."""
    try:
        if archive.suffix == ".zip":
            with zipfile.ZipFile(archive) as package:
                entries = package.infolist()
                if len({item.filename for item in entries}) != len(entries):
                    raise ValueError("Duplicate ZIP entries")
                for item in entries:
                    name = PurePosixPath(item.filename)
                    if name.is_absolute() or ".." in name.parts or (item.external_attr >> 16) & 0o170000 == 0o120000:
                        raise ValueError("Unsafe ZIP entry")
                return {name: package.read(name) for name in names}
        with tarfile.open(archive, "r:gz") as package:
            entries = package.getmembers()
            if len({item.name for item in entries}) != len(entries):
                raise ValueError("Duplicate tar entries")
            for item in entries:
                name = PurePosixPath(item.name)
                if name.is_absolute() or ".." in name.parts or not (item.isfile() or item.isdir()):
                    raise ValueError("Unsafe tar entry")
            result = {}
            for name in names:
                item = package.getmember(name)
                if not item.isfile():
                    raise ValueError("Expected a regular archive member")
                with package.extractfile(item) as file:
                    result[name] = file.read()
            return result
    except (zipfile.BadZipFile, tarfile.TarError, KeyError, ValueError, OSError) as error:
        raise SystemExit("Invalid GUI package or build metadata") from error


def verify(dist=None, source_commit=None, runtimes=None):
    dist = ROOT / "dist" if dist is None else Path(dist)
    source_commit = os.environ.get("GITHUB_SHA") if source_commit is None else source_commit
    if not isinstance(source_commit, str) or not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", source_commit):
        raise SystemExit("A complete validated source commit is required")
    # Native build tests can inspect one platform; the CLI always requires all six.
    runtimes = list(PLATFORMS) if runtimes is None else list(runtimes)
    if not runtimes or len(set(runtimes)) != len(runtimes) or any(runtime not in PLATFORMS for runtime in runtimes):
        raise SystemExit("Invalid release platform set")
    with_source_root = packager.ROOT
    try:
        packager.ROOT = ROOT
        version = packager.version()
    finally:
        packager.ROOT = with_source_root
    expected_names = set()
    archives = []
    for runtime in runtimes:
        stem = f"drcom4scutGUI-{version}-{runtime}"
        archive = dist / f"{stem}.{PLATFORMS[runtime]['archive']}"
        expected_names.update((archive.name, archive.with_suffix(archive.suffix + ".sha256").name))
        archives.append((runtime, stem, archive))
    entries = list(dist.iterdir())
    if any(not path.is_file() or path.is_symlink() for path in entries):
        raise SystemExit("GUI release artifacts must be regular files")
    actual_names = {path.name for path in entries if path.name != "SHA256SUMS"}
    if actual_names != expected_names:
        raise SystemExit(f"Unexpected GUI release artifact set: {actual_names ^ expected_names}")
    aggregate = []
    for runtime, stem, archive in archives:
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        line = f"{digest}  {archive.name}"
        if archive.with_suffix(archive.suffix + ".sha256").read_text(encoding="utf-8").strip() != line:
            raise SystemExit("GUI package checksum or filename mismatch")
        app_path = f"{stem}/{packager.executable_relative(runtime)}"
        payload_path = f"{stem}/{packager.payload_relative(runtime)}"
        metadata_path = f"{stem}/BUILD-INFO.json"
        members = read_members(archive, (metadata_path, app_path, payload_path))
        try:
            metadata = json.loads(members[metadata_path])
        except (UnicodeError, json.JSONDecodeError) as error:
            raise SystemExit("Invalid GUI package or build metadata") from error
        if not isinstance(metadata, dict) or (
            metadata.get("version") != version or metadata.get("runtime") != runtime
            or metadata.get("source_commit") != source_commit
            or metadata.get("minimum_core_version") != packager.MINIMUM_CORE_VERSION
        ):
            raise SystemExit("GUI package does not match the validated source")
        if metadata.get("source_dirty") is not False:
            raise SystemExit("Refusing a release package built from a modified source checkout")
        if metadata.get("self_contained") is not True or metadata.get("core_included") is not False:
            raise SystemExit("Unexpected GUI package layout")
        if metadata.get("macos_signing") != ("ad-hoc" if runtime.startswith("osx-") else "none"):
            raise SystemExit("Unexpected GUI package signing metadata")
        packager.verify_abi(members[app_path], PLATFORMS[runtime])
        if payload_path != app_path:
            packager.verify_abi(members[payload_path], PLATFORMS[runtime])
        aggregate.append(line + "\n")
    (dist / "SHA256SUMS").write_text("".join(aggregate), encoding="utf-8")
    return [archive for _, _, archive in archives]


if __name__ == "__main__":
    verify()
