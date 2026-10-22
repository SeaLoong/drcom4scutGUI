"""Verify the GUI archive's checksum and source, then write aggregate checksums."""

import hashlib
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[2]


def verify(dist=None, source_commit=None):
    dist = ROOT / "dist" if dist is None else Path(dist)
    source_commit = os.environ.get("GITHUB_SHA") if source_commit is None else source_commit
    if not isinstance(source_commit, str) or not re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", source_commit):
        raise SystemExit("A complete validated source commit is required")
    version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if version is None or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", version):
        raise SystemExit("Directory.Build.props must declare a semantic Version")
    stem = f"drcom4scutGUI-{version}-win-x64"
    archive = dist / f"{stem}.zip"
    checksum = archive.with_suffix(".zip.sha256")
    expected_names = {archive.name, checksum.name}
    entries = list(dist.iterdir())
    if any(not path.is_file() or path.is_symlink() for path in entries):
        raise SystemExit("GUI release artifacts must be regular files")
    actual_names = {path.name for path in entries if path.name != "SHA256SUMS"}
    if actual_names != expected_names:
        raise SystemExit(f"Unexpected GUI release artifact set: {actual_names ^ expected_names}")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    if checksum.read_text(encoding="utf-8").strip() != f"{digest}  {archive.name}":
        raise SystemExit("GUI package checksum or filename mismatch")
    try:
        with zipfile.ZipFile(archive) as package:
            metadata = json.loads(package.read(f"{stem}/BUILD-INFO.json"))
    except (zipfile.BadZipFile, KeyError, UnicodeError, json.JSONDecodeError) as error:
        raise SystemExit("Invalid GUI package or build metadata") from error
    if not isinstance(metadata, dict) or (
        metadata.get("version") != version
        or metadata.get("runtime") != "win-x64"
        or metadata.get("source_commit") != source_commit
    ):
        raise SystemExit("GUI package does not match the validated source")
    if metadata.get("source_dirty") is not False:
        raise SystemExit("Refusing a release package built from a modified source checkout")
    if metadata.get("self_contained") is not True or metadata.get("core_included") is not False:
        raise SystemExit("Unexpected GUI package layout")
    (dist / "SHA256SUMS").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
    return archive


if __name__ == "__main__":
    verify()
