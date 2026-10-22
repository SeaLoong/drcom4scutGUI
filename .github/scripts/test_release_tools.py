"""Exercise desktop archives, ABI/source guards and nonzero test-result checks."""

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import plistlib
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

HERE = Path(__file__).resolve().parent
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("gui_package_release", HERE / "package_release.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)
source_revision = packager.source_revision
spec = importlib.util.spec_from_file_location("gui_verify_release", HERE / "verify_release.py")
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


def native_image(runtime, executable=True):
    platform = packager.PLATFORMS[runtime]
    data = bytearray(128)
    if platform["format"] == "pe":
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, 64)
        data[64:68] = b"PE\0\0"
        struct.pack_into("<H", data, 68, platform["machine"])
        struct.pack_into("<H", data, 88, 0x20b)
    elif platform["format"] == "elf":
        data[:6] = b"\x7fELF\x02\x01"
        struct.pack_into("<HH", data, 16, 3, platform["machine"])
    else:
        data[:4] = b"\xcf\xfa\xed\xfe"
        struct.pack_into("<I", data, 4, platform["machine"])
        struct.pack_into("<I", data, 12, 2 if executable else 6)
    return data


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Version>2.0.0</Version></PropertyGroup></Project>"
        )
        (self.root / "README.md").write_text("[Usage](docs/usage.md)")
        (self.root / "LICENSE").write_text("Fixture license")
        (self.root / "THIRD-PARTY-NOTICES.md").write_text("Bundled dependency notices")
        (self.root / "docs").mkdir()
        (self.root / "docs/usage.md").write_text("Offline usage instructions")
        (self.root / "docs/security.md").write_text("Password storage instructions")
        for module in (packager, verifier):
            root_patch = patch.object(module, "ROOT", self.root)
            root_patch.start()
            self.addCleanup(root_patch.stop)
        self.commit_patch = patch.object(packager, "source_revision", return_value=("a" * 40, False))
        self.commit_patch.start()
        self.addCleanup(self.commit_patch.stop)
        self.signer = patch.object(packager, "sign_app_bundle")
        self.signer_mock = self.signer.start()
        self.addCleanup(self.signer.stop)
        def build_launcher(bundle, runtime):
            executable = bundle / "Contents/MacOS/drcom4scutGUI"
            executable.parent.mkdir(parents=True)
            executable.write_bytes(native_image(runtime))
        self.launcher = patch.object(packager, "build_macos_launcher", side_effect=build_launcher)
        self.launcher.start()
        self.addCleanup(self.launcher.stop)
        self.publish = self.make_publish("win-x64")

    def make_publish(self, runtime, version="2.0.0"):
        publish = self.root / ("publish-" + runtime)
        publish.mkdir(exist_ok=True)
        for name in packager.required_files(runtime):
            content = native_image(runtime, executable=name == packager.executable_name(runtime)) if name in (
                packager.executable_name(runtime), *packager.required_files(runtime)[-2:]
            ) else b"fixture-publish-file"
            (publish / name).write_bytes(content)
        (publish / packager.executable_name(runtime)).chmod(0o755)
        self.write_deps(runtime, version, publish)
        (publish / "drcom4scutGUI.runtimeconfig.json").write_text("{}")
        return publish

    def write_deps(self, runtime, version, publish=None):
        publish = self.publish if publish is None else publish
        (publish / "drcom4scutGUI.deps.json").write_text(json.dumps({
            "runtimeTarget": {"name": f".NETCoreApp,Version=v10.0/{runtime}"},
            "libraries": {f"drcom4scutGUI/{version}": {}},
        }))

    def package(self, runtime="win-x64"):
        publish = self.publish if runtime == "win-x64" else self.make_publish(runtime)
        return packager.package(publish, self.root / "dist", runtime)

    def verify_one(self, archive, commit="a" * 40, runtime="win-x64"):
        return verifier.verify(archive.parent, commit, [runtime])

    def rewrite_zip(self, archive, mutate):
        with zipfile.ZipFile(archive) as source:
            contents = {item.filename: source.read(item) for item in source.infolist()}
        mutate(contents)
        with zipfile.ZipFile(archive, "w") as output:
            for name, content in contents.items():
                output.writestr(name, content)
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n")

    def test_archive_contents_metadata_and_checksum(self):
        archive = self.package()
        actual = hashlib.sha256(archive.read_bytes()).hexdigest()
        self.assertEqual(archive.with_suffix(".zip.sha256").read_text(), f"{actual}  {archive.name}\n")
        with zipfile.ZipFile(archive) as package:
            prefix = "drcom4scutGUI-2.0.0-win-x64/"
            self.assertIn(prefix + "README.md", package.namelist())
            self.assertIn(prefix + "LICENSE", package.namelist())
            self.assertEqual(package.read(prefix + "THIRD-PARTY-NOTICES.md"), b"Bundled dependency notices")
            self.assertEqual(package.read(prefix + "docs/usage.md"), b"Offline usage instructions")
            self.assertEqual(package.read(prefix + "docs/security.md"), b"Password storage instructions")
            metadata = json.loads(package.read(prefix + "BUILD-INFO.json"))
        self.assertEqual(metadata["source_commit"], "a" * 40)
        self.assertFalse(metadata["source_dirty"])
        self.assertEqual(metadata["minimum_core_version"], "0.5.0")
        self.assertTrue(metadata["self_contained"])
        self.assertFalse(metadata["core_included"])
        for _ in range(2):
            (archive.parent / "SHA256SUMS").write_text("stale checksums\n")
            self.assertEqual(self.verify_one(archive), [archive])
            self.assertEqual((archive.parent / "SHA256SUMS").read_text(), f"{actual}  {archive.name}\n")

    def test_complete_six_runtime_release_and_real_macos_bundle_layout(self):
        archives = [self.package(runtime) for runtime in packager.PLATFORMS]
        self.assertEqual(len(archives), 6)
        self.assertEqual(set(verifier.verify(self.root / "dist", "a" * 40)), set(archives))
        sums = (self.root / "dist/SHA256SUMS").read_text().splitlines()
        self.assertEqual(len(sums), 6)
        self.assertEqual(self.signer_mock.call_count, 2)
        for runtime in ("osx-x64", "osx-arm64"):
            archive = next(path for path in archives if runtime in path.name)
            prefix = f"drcom4scutGUI-2.0.0-{runtime}/"
            with tarfile.open(archive) as package:
                apphost = package.getmember(prefix + packager.executable_relative(runtime))
                self.assertTrue(apphost.mode & 0o111)
                payload = package.getmember(prefix + packager.payload_relative(runtime))
                self.assertTrue(payload.mode & 0o111)
                self.assertEqual(package.extractfile(payload).read(), native_image(runtime))
                self.assertNotIn(prefix + "drcom4scutGUI.app/Contents/MacOS/drcom4scutGUI.dll", package.getnames())
                self.assertIn(prefix + "drcom4scutGUI.app/Contents/Resources/publish/drcom4scutGUI.dll", package.getnames())
                info = plistlib.loads(package.extractfile(prefix + "drcom4scutGUI.app/Contents/Info.plist").read())
                self.assertEqual(info["CFBundleExecutable"], "drcom4scutGUI")
                self.assertEqual(info["LSMinimumSystemVersion"], "15.0")
                self.assertIn(prefix + "README.md", package.getnames())
                self.assertEqual(package.extractfile(prefix + "drcom4scutGUI.app/Contents/Resources/THIRD-PARTY-NOTICES.md").read(), b"Bundled dependency notices")
                self.assertIn(prefix + "drcom4scutGUI.app/Contents/Resources/docs/usage.md", package.getnames())

    def test_release_requires_the_complete_manifest_by_default(self):
        archive = self.package()
        with self.assertRaisesRegex(SystemExit, "artifact set"):
            verifier.verify(archive.parent, "a" * 40)
        self.assertFalse((archive.parent / "SHA256SUMS").exists())

    def test_unix_archive_preserves_a_runnable_apphost_on_hosts_without_execute_bits(self):
        publish = self.make_publish("linux-x64")
        (publish / "drcom4scutGUI").chmod(0o644)
        archive = packager.package(publish, self.root / "dist", "linux-x64")
        with tarfile.open(archive) as package:
            apphost = package.getmember("drcom4scutGUI-2.0.0-linux-x64/drcom4scutGUI")
            self.assertEqual(apphost.mode, 0o755)

    def test_release_rejects_damaged_archive_and_wrong_checksum_filename(self):
        archive = self.package()
        with archive.open("ab") as output:
            output.write(b"archive bytes were changed after packaging")
        with self.assertRaisesRegex(SystemExit, "checksum"):
            self.verify_one(archive)
        self.assertFalse((archive.parent / "SHA256SUMS").exists())
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_suffix(".zip.sha256").write_text(f"{digest}  another-package.zip\n")
        with self.assertRaisesRegex(SystemExit, "filename"):
            self.verify_one(archive)

    def test_release_rejects_wrong_source_and_unexpected_files(self):
        archive = self.package()
        with self.assertRaisesRegex(SystemExit, "validated source"):
            self.verify_one(archive, "b" * 40)
        (archive.parent / "another-package.zip").write_bytes(b"unexpected archive")
        with self.assertRaisesRegex(SystemExit, "artifact set"):
            self.verify_one(archive)
        self.assertFalse((archive.parent / "SHA256SUMS").exists())

    def test_release_rejects_forged_metadata_even_with_valid_checksum(self):
        for field, invalid, message in (
            ("version", "1.0.0", "validated source"), ("runtime", "win-arm64", "validated source"),
            ("source_commit", "b" * 40, "validated source"), ("minimum_core_version", "0.1.0", "validated source"),
            ("source_dirty", True, "modified source"), ("source_dirty", "false", "modified source"),
            ("self_contained", False, "layout"), ("core_included", True, "layout"),
            ("macos_signing", "notarized", "signing"),
        ):
            with self.subTest(field=field, invalid=invalid):
                archive = self.package()
                def mutate(contents):
                    name = archive.stem + "/BUILD-INFO.json"
                    metadata = json.loads(contents[name])
                    metadata[field] = invalid
                    contents[name] = json.dumps(metadata).encode()
                self.rewrite_zip(archive, mutate)
                with self.assertRaisesRegex(SystemExit, message):
                    self.verify_one(archive)
                self.assertFalse((archive.parent / "SHA256SUMS").exists())

    def test_release_rejects_forged_apphost_even_with_valid_checksum(self):
        archive = self.package()
        def mutate(contents):
            contents[archive.stem + "/drcom4scutGUI.exe"] = native_image("win-arm64")
        self.rewrite_zip(archive, mutate)
        with self.assertRaisesRegex(SystemExit, "architecture"):
            self.verify_one(archive)

    def test_release_rejects_foreign_macos_payload_even_with_valid_checksum(self):
        archive = self.package("osx-arm64")
        with tarfile.open(archive) as source:
            entries = [(item, source.extractfile(item).read() if item.isfile() else None) for item in source]
        with tarfile.open(archive, "w:gz") as target:
            import io
            for item, data in entries:
                if item.name.endswith("/Contents/Resources/publish/drcom4scutGUI"):
                    data = native_image("osx-x64")
                    item.size = len(data)
                target.addfile(item, io.BytesIO(data) if data is not None else None)
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_suffix(".gz.sha256").write_text(f"{digest}  {archive.name}\n")
        with self.assertRaisesRegex(SystemExit, "architecture"):
            self.verify_one(archive, runtime="osx-arm64")

    @unittest.skipUnless(sys.platform == "darwin", "Requires native macOS clang and codesign")
    def test_native_macos_bundle_survives_tar_and_launcher_preserves_pid_and_arguments(self):
        self.signer.stop()
        self.launcher.stop()
        runtime = "osx-arm64" if platform.machine() == "arm64" else "osx-x64"
        publish = self.make_publish(runtime)
        compiler = ["clang", "-arch", "arm64" if runtime == "osx-arm64" else "x86_64",
                    "-mmacosx-version-min=15.0", "-Wall", "-Wextra", "-Werror", "-x", "c"]
        subprocess.run(compiler + ["-o", str(publish / "drcom4scutGUI"), "-"], input=(
            '#include <stdio.h>\n#include <unistd.h>\n'
            'int main(int argc, char **argv) { if(argc != 2) return 2; '
            'printf("%ld\\n%s\\n", (long)getpid(), argv[1]); return 37; }\n'
        ), text=True, check=True, timeout=60)
        subprocess.run(compiler + ["-dynamiclib", "-o", str(publish / "libcoreclr.dylib"), "-"],
                       input="int runtime_fixture(void) { return 0; }\n", text=True, check=True, timeout=60)
        shutil.copy2(publish / "libcoreclr.dylib", publish / "libhostfxr.dylib")
        (publish / "System.Diagnostics.Contracts.dll").write_bytes(native_image("win-x64"))
        archive = packager.package(publish, self.root / "dist", runtime)
        extracted = self.root / "extracted with spaces 中文"
        with tarfile.open(archive) as package:
            package.extractall(extracted, filter="data")
        root = extracted / f"drcom4scutGUI-2.0.0-{runtime}"
        bundle = root / "drcom4scutGUI.app"
        packager.verify_app_bundle(bundle)
        argument = 'literal spaces 中文 ; $HOME "quotes"'
        with subprocess.Popen([str(root / packager.executable_relative(runtime)), argument],
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as child:
            stdout, stderr = child.communicate(timeout=30)
            self.assertEqual(child.returncode, 37, stderr)
            self.assertEqual(stdout.splitlines(), [str(child.pid), argument])
        managed = bundle / "Contents/Resources/publish/System.Diagnostics.Contracts.dll"
        managed.write_bytes(managed.read_bytes() + b"modified after signing")
        with self.assertRaises(subprocess.CalledProcessError):
            packager.verify_app_bundle(bundle)

    def test_release_rejects_invalid_zip_with_matching_checksum(self):
        archive = self.package()
        archive.write_bytes(b"not a ZIP archive")
        digest = hashlib.sha256(archive.read_bytes()).hexdigest()
        archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n")
        with self.assertRaisesRegex(SystemExit, "Invalid GUI package"):
            self.verify_one(archive)

    def test_user_account_and_core_files_are_never_packaged(self):
        for name in (
            "gui.json", "drcom4scutGUI.json", "accounts.json", "config.json", "config.yml",
            "config.yaml", "core.yml", "core.yaml", "Config.JSON", "drcom4scut", "drcom4scut.exe",
        ):
            with self.subTest(name=name):
                path = self.publish / name
                path.write_text('"private-password"')
                with self.assertRaisesRegex(SystemExit, "user configuration"):
                    self.package()
                path.unlink()
        self.assertFalse((self.root / "dist").exists())

    def test_wrong_version_and_runtime_are_rejected(self):
        for runtime, version in (("win-arm64", "2.0.0"), ("win-x64", "1.0.0")):
            with self.subTest(runtime=runtime, version=version):
                self.write_deps(runtime, version)
                with self.assertRaises(SystemExit):
                    self.package()
        self.assertFalse((self.root / "dist").exists())

    def test_every_native_format_rejects_foreign_cpu_and_malformed_images(self):
        for runtime, other in (("win-x64", "win-arm64"), ("linux-x64", "linux-arm64"), ("osx-x64", "osx-arm64")):
            platform = packager.PLATFORMS[runtime]
            with self.subTest(runtime=runtime):
                packager.verify_abi(native_image(runtime), platform)
                with self.assertRaisesRegex(SystemExit, "architecture"):
                    packager.verify_abi(native_image(other), platform)
                for bad in (b"", b"not a binary", native_image(runtime)[:24]):
                    with self.assertRaises(SystemExit):
                        packager.verify_abi(bad, platform)
        data = native_image("osx-arm64", executable=False)
        with self.assertRaisesRegex(SystemExit, "type"):
            packager.verify_abi(data, packager.PLATFORMS["osx-arm64"])
        with self.assertRaisesRegex(SystemExit, "thin"):
            packager.verify_abi(b"\xca\xfe\xba\xbe" + bytes(128), packager.PLATFORMS["osx-x64"])

    def test_macos_never_silently_omits_native_signing(self):
        self.signer.stop()
        with patch.object(packager.sys, "platform", "linux"), self.assertRaisesRegex(SystemExit, "signed on macOS"):
            self.package("osx-x64")

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
            self.package()


class NativeBundleSigningTests(unittest.TestCase):
    def test_managed_pe_data_is_not_executable_and_native_children_precede_bundle_signing(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "drcom4scutGUI.app"
            macos = bundle / "Contents/MacOS"
            macos.mkdir(parents=True)
            publish = bundle / "Contents/Resources/publish"
            publish.mkdir(parents=True)
            apphost = macos / "drcom4scutGUI"
            native = publish / "libcoreclr.dylib"
            managed = publish / "System.Diagnostics.Contracts.dll"
            apphost.write_bytes(native_image("osx-arm64"))
            native.write_bytes(native_image("osx-arm64", executable=False))
            managed.write_bytes(native_image("win-x64"))
            for file in (apphost, native, managed):
                file.chmod(0o755)
            managed.chmod(0o700)
            with patch.object(packager.sys, "platform", "darwin"), patch.object(packager.subprocess, "run") as run:
                packager.sign_app_bundle(bundle)
            commands = [call.args[0] for call in run.call_args_list]
            signatures = [command for command in commands if "--sign" in command]
            self.assertEqual([command[-1] for command in signatures], [str(native), str(bundle)])
            self.assertFalse(any("--deep" in command for command in signatures))
            self.assertFalse(any(command[-1] == str(managed) for command in commands))
            self.assertTrue(any(command[-1] == str(apphost) and "--verify" in command for command in commands))
            self.assertEqual(commands[-1][-1], str(bundle))
            self.assertIn("--verify", commands[-1])
            self.assertIn("--deep", commands[-1])
            self.assertIn("--strict", commands[-1])
            if os.name != "nt":
                # NTFS has no Unix execute bits; native signing is macOS-only.
                self.assertEqual(managed.stat().st_mode & 0o777, 0o644)
                self.assertEqual(apphost.stat().st_mode & 0o777, 0o755)
                self.assertEqual(native.stat().st_mode & 0o777, 0o755)


class NativeKeychainFixtureTests(unittest.TestCase):
    def test_runner_keychains_are_restored_when_native_tests_pass_or_fail(self):
        spec = importlib.util.spec_from_file_location("native_keychain_fixture", HERE / "test_native_keychain.py")
        fixture = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(fixture)
        original = "/Users/runner/User Keychains/login.keychain-db"
        for failed in (False, True):
            calls = []
            def run(arguments, **options):
                calls.append(arguments)
                if failed and arguments[0] == "dotnet":
                    raise subprocess.CalledProcessError(1, arguments)
            with self.subTest(failed=failed), patch.object(fixture.subprocess, "check_output", side_effect=[
                f'    "{original}"\n', f'    "{original}"\n    "/Library/Keychains/System.keychain"\n',
            ]), patch.object(fixture.subprocess, "run", side_effect=run):
                if failed:
                    with self.assertRaises(subprocess.CalledProcessError):
                        fixture.main()
                else:
                    fixture.main()
            self.assertIn(["security", "default-keychain", "-d", "user", "-s", original], calls)
            self.assertIn(["security", "list-keychains", "-d", "user", "-s", original, "/Library/Keychains/System.keychain"], calls)
            deleted = [command[-1] for command in calls if command[:2] == ["security", "delete-keychain"]]
            self.assertEqual(len(deleted), 1)
            self.assertNotEqual(deleted[0], original)
            self.assertTrue(deleted[0].endswith("drcom4scutGUI-tests.keychain-db"))


class ResultTests(unittest.TestCase):
    def check(self, counters):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.trx"
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
