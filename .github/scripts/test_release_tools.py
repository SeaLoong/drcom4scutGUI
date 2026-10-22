"""Regression tests for release version/credential guards and nonzero test checks."""

import hashlib
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile


HERE = pathlib.Path(__file__).resolve().parent
# Loading source this way avoids a dependency on the .github directory being a
# Python package; bytecode is disabled to keep generated files out of the tree.
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("gui_package_release", HERE / "package_release.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)
source_revision = packager.source_revision


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        self.publish = self.root / "publish"
        self.publish.mkdir()
        (self.root / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>"
        )
        (self.root / "README.md").write_text("Fixture instructions")
        (self.root / "LICENSE").write_text("Fixture license")
        for name in ("drcom4scutGUI.exe", "drcom4scutGUI.dll", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll"):
            (self.publish / name).write_bytes(b"fixture-publish-file")
        (self.publish / "drcom4scutGUI.runtimeconfig.json").write_text("{}")
        self.write_deps("win-x64", "2.0.0")
        self.root_patch = patch.object(packager, "ROOT", self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)
        self.commit_patch = patch.object(packager, "source_revision", return_value=("a" * 40, False))
        self.commit_patch.start()
        self.addCleanup(self.commit_patch.stop)

    def write_deps(self, runtime, version):
        (self.publish / "drcom4scutGUI.deps.json").write_text(json.dumps({
            "runtimeTarget": {"name": f".NETCoreApp,Version=v10.0/{runtime}"},
            "libraries": {f"drcom4scutGUI/{version}": {}},
        }))

    def test_archive_contents_metadata_and_checksum(self):
        archive = packager.package(self.publish, self.root / "dist")
        actual = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(archive.with_suffix(".zip.sha256").read_text(), f"{actual}  {archive.name}\n")
        with zipfile.ZipFile(archive) as package:
            prefix = "drcom4scutGUI-2.0.0-win-x64/"
            self.assertIn(prefix + "README.md", package.namelist())
            self.assertIn(prefix + "LICENSE", package.namelist())
            metadata = json.loads(package.read(prefix + "BUILD-INFO.json"))
        self.assertEqual(metadata["source_commit"], "a" * 40)
        self.assertFalse(metadata["source_dirty"])
        self.assertEqual(metadata["minimum_core_version"], "0.4.0")
        self.assertTrue(metadata["self_contained"])
        self.assertFalse(metadata["core_included"])

    def test_user_account_files_are_never_packaged(self):
        for name in ("gui.json", "drcom4scutGUI.json", "accounts.json", "config.yml"):
            with self.subTest(name=name):
                path = self.publish / name
                path.write_text('"private-password"')
                with self.assertRaisesRegex(SystemExit, "user configuration"):
                    packager.package(self.publish, self.root / "dist")
                path.unlink()
        self.assertFalse((self.root / "dist").exists())

    def test_wrong_version_and_runtime_are_rejected(self):
        for runtime, version in (("win-arm64", "2.0.0"), ("win-x64", "1.0.0")):
            with self.subTest(runtime=runtime, version=version):
                self.write_deps(runtime, version)
                with self.assertRaises(SystemExit):
                    packager.package(self.publish, self.root / "dist")
        self.assertFalse((self.root / "dist").exists())

    def test_source_revision_records_local_edits_and_rejects_wrong_ci_commit(self):
        subprocess.run(["git", "init", "--quiet"], cwd=self.root, check=True)
        subprocess.run(["git", "add", "."], cwd=self.root, check=True)
        subprocess.run([
            "git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Fixture",
        ], cwd=self.root, check=True)
        with patch.dict(os.environ, {"GITHUB_SHA": ""}):
            commit, dirty = source_revision()
            self.assertFalse(dirty)
            (self.root / "README.md").write_text("Modified source")
            self.assertEqual(source_revision(), (commit, True))
        with patch.dict(os.environ, {"GITHUB_SHA": "b" * 40}):
            with self.assertRaisesRegex(SystemExit, "build commit"):
                source_revision()

    def test_framework_dependent_directory_is_rejected(self):
        (self.publish / "coreclr.dll").unlink()
        with self.assertRaisesRegex(SystemExit, "missing coreclr.dll"):
            packager.package(self.publish, self.root / "dist")


class ResultTests(unittest.TestCase):
    def check(self, counters):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "result.trx"
            path.write_text(f'<TestRun xmlns="urn:test"><ResultSummary><Counters {counters}/></ResultSummary></TestRun>')
            return subprocess.run(
                [sys.executable, str(HERE / "check_test_results.py"), str(path)],
                capture_output=True, text=True, check=False,
            )

    def test_successful_nonzero_run_is_accepted_and_skips_are_visible(self):
        result = self.check('total="3" executed="2" passed="2" failed="0" notExecuted="1"')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("notExecuted", result.stdout)

    def test_zero_or_failed_run_is_rejected(self):
        for counters in (
            'total="0" executed="0" passed="0" failed="0"',
            'total="1" executed="1" passed="0" failed="1"',
            'total="2" executed="2" passed="1" failed="1"',
            'total="1" executed="1" passed="0" aborted="1"',
        ):
            with self.subTest(counters=counters):
                result = self.check(counters)
                self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
