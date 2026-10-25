"""Select screenshot source only from the fetched, trusted core master history."""
import argparse
import os
from pathlib import Path
import re
import subprocess


def git(repository, *arguments):
    result = subprocess.run(
        ["git", "-C", str(repository), *arguments],
        capture_output=True, text=True, check=False,
    )
    if result.returncode:
        raise ValueError("Could not verify the fetched core Git history")
    return result.stdout.strip()


def select_core(repository, requested):
    """Verify ancestry before checkout; never fetch or execute the requested ref."""
    repository = Path(repository)
    requested = requested or "master"
    if requested != "master" and not re.fullmatch(r"[0-9a-f]{40}", requested):
        raise ValueError("Core source must be master or a full lowercase commit SHA")
    trusted = git(repository, "rev-parse", "--verify", "refs/remotes/origin/master^{commit}")
    if requested == "master":
        selected = trusted
    else:
        if git(repository, "cat-file", "-t", requested) != "commit":
            raise ValueError("The requested SHA must identify a commit")
        selected = git(repository, "rev-parse", "--verify", requested + "^{commit}")
        ancestry = subprocess.run(
            ["git", "-C", str(repository), "merge-base", "--is-ancestor", selected, trusted],
            capture_output=True, text=True, check=False,
        )
        if ancestry.returncode == 1:
            raise ValueError("The requested core commit is not merged into fetched origin/master")
        if ancestry.returncode:
            raise ValueError("Could not verify core commit ancestry")
    git(repository, "checkout", "--detach", selected)
    return selected


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, required=True)
    args = parser.parse_args()
    try:
        print("Screenshot core commit: " + select_core(args.repository, os.environ.get("CAPTURE_CORE_REF", "master")))
    except ValueError as error:
        raise SystemExit(str(error)) from error
