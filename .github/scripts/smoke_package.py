"""Launch the native GUI from its actual release archive, with a bounded UI smoke."""

import argparse
import importlib.util
from pathlib import Path
import subprocess
import tarfile
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("gui_smoke_packager", Path(__file__).with_name("package_release.py"))
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)


def smoke(runtime):
    stem = f"drcom4scutGUI-{packager.version()}-{runtime}"
    archive = ROOT / "dist" / f"{stem}.{packager.PLATFORMS[runtime]['archive']}"
    with tempfile.TemporaryDirectory() as directory:
        if archive.suffix == ".zip":
            with zipfile.ZipFile(archive) as package:
                package.extractall(directory)
        else:
            with tarfile.open(archive, "r:gz") as package:
                package.extractall(directory, filter="data")
        executable = Path(directory) / stem / packager.executable_relative(runtime)
        if runtime.startswith("osx-"):
            packager.verify_app_bundle(Path(directory) / stem / "drcom4scutGUI.app")
        subprocess.run([str(executable), "--smoke-test"], cwd=executable.parent, check=True, timeout=45)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime", choices=packager.PLATFORMS, required=True)
    arguments = parser.parse_args()
    smoke(arguments.runtime)
