"""Exercise the source trust boundary with real commits and rejected branch history."""
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("select_capture_core", Path(__file__).with_name("select_capture_core.py"))
selector = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(selector)


class CaptureCoreTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.repository = Path(self.directory.name)
        self.git("init", "-b", "master")
        self.git("config", "user.name", "Capture test")
        self.git("config", "user.email", "capture-test@example.invalid")
        (self.repository / "source.txt").write_text("reviewed first\n")
        self.git("add", "source.txt")
        self.git("commit", "-m", "reviewed first")
        self.first = self.git("rev-parse", "HEAD")
        (self.repository / "source.txt").write_text("reviewed latest\n")
        self.git("commit", "-am", "reviewed latest")
        self.latest = self.git("rev-parse", "HEAD")
        self.git("update-ref", "refs/remotes/origin/master", self.latest)
        self.git("checkout", "-b", "unmerged", self.first)
        (self.repository / "source.txt").write_text("unreviewed branch\n")
        self.git("commit", "-am", "unreviewed branch")
        self.unmerged = self.git("rev-parse", "HEAD")
        self.git("checkout", "master")

    def git(self, *arguments):
        return subprocess.run(
            ["git", "-C", str(self.repository), *arguments],
            check=True, capture_output=True, text=True,
        ).stdout.strip()

    def test_master_uses_fetched_remote_history_and_detaches(self):
        # A local branch is not the trusted anchor.
        self.git("checkout", "--detach", self.latest)
        self.git("branch", "-f", "master", self.unmerged)
        selected = selector.select_core(self.repository, "master")
        self.assertEqual(selected, self.latest)
        self.assertEqual((self.repository / "source.txt").read_text(), "reviewed latest\n")
        self.assertEqual(self.git("rev-parse", "--abbrev-ref", "HEAD"), "HEAD")

    def test_reviewed_historical_commit_can_be_used(self):
        self.assertEqual(selector.select_core(self.repository, self.first), self.first)
        self.assertEqual((self.repository / "source.txt").read_text(), "reviewed first\n")

    def test_unmerged_commit_is_rejected_without_checkout(self):
        with self.assertRaisesRegex(ValueError, "not merged"):
            selector.select_core(self.repository, self.unmerged)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.latest)
        self.assertEqual((self.repository / "source.txt").read_text(), "reviewed latest\n")

    def test_refs_abbreviations_and_shell_inputs_are_rejected_without_checkout(self):
        for requested in ("unmerged", "refs/pull/1/head", self.first[:12], "master; echo unsafe", "--help", "a" * 40 + "\n"):
            with self.subTest(requested=requested), self.assertRaises(ValueError):
                selector.select_core(self.repository, requested)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.latest)

    def test_blob_and_tag_objects_are_not_commits(self):
        blob = self.git("rev-parse", "HEAD:source.txt")
        self.git("tag", "-a", "reviewed-tag", "-m", "reviewed tag", self.first)
        tag = self.git("rev-parse", "reviewed-tag")
        for requested in (blob, tag):
            with self.subTest(requested=requested), self.assertRaisesRegex(ValueError, "must identify a commit"):
                selector.select_core(self.repository, requested)
        self.assertEqual(self.git("rev-parse", "HEAD"), self.latest)

    def test_missing_remote_anchor_fails_closed(self):
        self.git("update-ref", "-d", "refs/remotes/origin/master")
        with self.assertRaisesRegex(ValueError, "fetched core Git history"):
            selector.select_core(self.repository, "master")
        self.assertEqual(self.git("rev-parse", "HEAD"), self.latest)


if __name__ == "__main__":
    unittest.main()
