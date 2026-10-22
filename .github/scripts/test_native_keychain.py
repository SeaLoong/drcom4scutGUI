"""Run native Security.framework tests against a disposable runner keychain."""

from pathlib import Path
import shlex
import subprocess
import tempfile


def security(*arguments, check=True):
    return subprocess.run(["security", *arguments], check=check)


def main():
    original_default = shlex.split(subprocess.check_output(
        ["security", "default-keychain", "-d", "user"], text=True
    ))
    original_search = shlex.split(subprocess.check_output(
        ["security", "list-keychains", "-d", "user"], text=True
    ))
    if len(original_default) != 1:
        raise SystemExit("Expected the runner's existing default keychain")
    with tempfile.TemporaryDirectory() as directory:
        keychain = str(Path(directory) / "drcom4scutGUI-tests.keychain-db")
        created = False
        try:
            # Empty passwords belong only to this disposable CI fixture; actual
            # application secrets stay inside Security.framework, never argv.
            security("create-keychain", "-p", "", keychain)
            created = True
            security("set-keychain-settings", "-u", "-t", "21600", keychain)
            security("unlock-keychain", "-p", "", keychain)
            security("list-keychains", "-d", "user", "-s", keychain, *original_search)
            security("default-keychain", "-d", "user", "-s", keychain)
            subprocess.run([
                "dotnet", "test", "--project", "drcom4scutGUI.Tests/drcom4scutGUI.Tests.csproj",
                "--configuration", "Release", "--no-restore", "--report-trx",
                "--results-directory", "artifacts/test-results",
            ], check=True)
        finally:
            security("default-keychain", "-d", "user", "-s", original_default[0], check=False)
            security("list-keychains", "-d", "user", "-s", *original_search, check=False)
            if created:
                security("delete-keychain", keychain, check=False)


if __name__ == "__main__":
    main()
