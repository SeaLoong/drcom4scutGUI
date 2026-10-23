"""Run the native GUI update transaction against the exact distributed archive."""

import argparse
import importlib.util
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("update_test_packager", Path(__file__).with_name("package_release.py"))
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)
TEST = "NativePackagedGuiUpdateInstallsAndPreservesCoreAndConfiguration"


def test_package(runtime):
    archive = ROOT / "dist" / f"drcom4scutGUI-{packager.version()}-{runtime}.{packager.PLATFORMS[runtime]['archive']}"
    if archive.is_symlink() or not archive.is_file():
        raise RuntimeError("The update transaction test requires the exact regular GUI release archive")
    source = os.environ.get("GITHUB_SHA") or subprocess.check_output(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise RuntimeError("The update transaction test requires the full source commit")
    results = ROOT / "artifacts" / "update-package-results"
    results.mkdir(parents=True, exist_ok=True)
    for stale in results.glob("*.trx"):
        stale.unlink()
    environment = dict(os.environ, DRCOM_GUI_UPDATE_TEST_ARCHIVE=str(archive), DRCOM_GUI_UPDATE_TEST_SOURCE=source)
    command = ["dotnet", "test", "--project", "drcom4scutGUI.Tests/drcom4scutGUI.Tests.csproj",
               "--configuration", "Release", "--no-restore", "--no-build", "--report-trx",
               "--results-directory", str(results), "--filter-method", "*" + TEST]
    result = subprocess.run(command, cwd=ROOT, env=environment, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", timeout=600)
    print(result.stdout, end="")
    (results / f"update-package-{runtime}.log").write_text(result.stdout, encoding="utf-8")
    result.check_returncode()
    reports = list(results.glob("*.trx"))
    if len(reports) != 1:
        raise RuntimeError("The native update package test must produce one fresh TRX report")
    report = ET.parse(reports[0]).getroot()
    counters = report.find(".//{*}Counters")
    if counters is None or any(int(counters.get(key, "0")) != 1 for key in ("total", "executed", "passed")):
        raise RuntimeError("The actual GUI package installer test did not execute exactly once")
    outcomes = report.findall(".//{*}UnitTestResult")
    if len(outcomes) != 1 or outcomes[0].get("outcome") != "Passed" or TEST not in outcomes[0].get("testName", ""):
        raise RuntimeError("The actual GUI package installer result was missing or unsuccessful")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=packager.PLATFORMS, required=True)
    arguments = parser.parse_args()
    test_package(arguments.runtime)
