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
spec = importlib.util.spec_from_file_location("gui_verify_release", HERE / "verify_release.py")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


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
        (self.root / "README.md").write_text("[Usage](docs/usage.md)")
        (self.root / "LICENSE").write_text("Fixture license")
        (self.root / "docs").mkdir()
        (self.root / "docs/usage.md").write_text("Offline usage instructions")
        (self.root / "docs/security.md").write_text("Password storage instructions")
        for name in ("drcom4scutGUI.exe", "drcom4scutGUI.dll", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll"):
            (self.publish / name).write_bytes(b"fixture-publish-file")
        (self.publish / "drcom4scutGUI.runtimeconfig.json").write_text("{}")
        self.write_deps("win-x64", "2.0.0")
        self.root_patch = patch.object(packager, "ROOT", self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)
        self.verifier_root_patch = patch.object(verifier, "ROOT", self.root)
        self.verifier_root_patch.start()
        self.addCleanup(self.verifier_root_patch.stop)
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
            self.assertEqual(package.read(prefix + "docs/usage.md"), b"Offline usage instructions")
            self.assertEqual(package.read(prefix + "docs/security.md"), b"Password storage instructions")
            metadata = json.loads(package.read(prefix + "BUILD-INFO.json"))
        self.assertEqual(metadata["source_commit"], "a" * 40)
        self.assertFalse(metadata["source_dirty"])
        self.assertEqual(metadata["minimum_core_version"], "0.4.1")
        self.assertTrue(metadata["self_contained"])
        self.assertFalse(metadata["core_included"])
        for _ in range(2):
            # A retry accepts its previous aggregate, then regenerates it from
            # the validated archive rather than trusting the old contents.
            (archive.parent / "SHA256SUMS").write_text("stale checksums\n")
            self.assertEqual(verifier.verify(archive.parent, "a" * 40), archive)
            self.assertEqual((archive.parent / "SHA256SUMS").read_text(), f"{actual}  {archive.name}\n")

    def test_release_rejects_damaged_archive_and_wrong_checksum_filename(self):
        archive = packager.package(self.publish, self.root / "dist")
        with archive.open("ab") as output:
            output.write(b"archive bytes were changed after packaging")
        with self.assertRaisesRegex(SystemExit, "checksum"):
            verifier.verify(archive.parent, "a" * 40)
        self.assertFalse((archive.parent / "SHA256SUMS").exists())
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_suffix(".zip.sha256").write_text(f"{digest}  another-package.zip\n")
        with self.assertRaisesRegex(SystemExit, "filename"):
            verifier.verify(archive.parent, "a" * 40)

    def test_release_rejects_wrong_source_and_unexpected_files(self):
        archive = packager.package(self.publish, self.root / "dist")
        with self.assertRaisesRegex(SystemExit, "validated source"):
            verifier.verify(archive.parent, "b" * 40)
        (archive.parent / "another-package.zip").write_bytes(b"unexpected archive")
        with self.assertRaisesRegex(SystemExit, "artifact set"):
            verifier.verify(archive.parent, "a" * 40)
        self.assertFalse((archive.parent / "SHA256SUMS").exists())

    def test_release_rejects_forged_metadata_even_with_a_valid_archive_checksum(self):
        for field, invalid, message in (
            ("version", "1.0.0", "validated source"),
            ("runtime", "win-arm64", "validated source"),
            ("source_commit", "b" * 40, "validated source"),
            ("source_dirty", True, "modified source"),
            ("source_dirty", "false", "modified source"),
            ("self_contained", False, "layout"),
            ("core_included", True, "layout"),
        ):
            with self.subTest(field=field, invalid=invalid):
                archive = packager.package(self.publish, self.root / "dist")
                with zipfile.ZipFile(archive) as source:
                    contents = {item.filename: source.read(item) for item in source.infolist()}
                name = archive.stem + "/BUILD-INFO.json"
                metadata = json.loads(contents[name])
                metadata[field] = invalid
                contents[name] = json.dumps(metadata).encode("utf-8")
                with zipfile.ZipFile(archive, "w") as output:
                    for name, content in contents.items():
                        output.writestr(name, content)
                digest = hashlib.sha256(archive.read_bytes()).hexdigest()
                archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n")
                with self.assertRaisesRegex(SystemExit, message):
                    verifier.verify(archive.parent, "a" * 40)
                self.assertFalse((archive.parent / "SHA256SUMS").exists())

    def test_release_rejects_invalid_zip_with_matching_checksum(self):
        archive = packager.package(self.publish, self.root / "dist")
        archive.write_bytes(b"not a ZIP archive")
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n")
        with self.assertRaisesRegex(SystemExit, "Invalid GUI package"):
            verifier.verify(archive.parent, "a" * 40)
        self.assertFalse((archive.parent / "SHA256SUMS").exists())

    def test_user_account_files_are_never_packaged(self):
        for name in (
            "gui.json", "drcom4scutGUI.json", "accounts.json", "config.json",
            "config.yml", "config.yaml", "core.yml", "core.yaml", "Config.JSON",
        ):
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
