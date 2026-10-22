#!/usr/bin/env bash
# An isolated desktop session exercises libsecret without touching user keyrings.
set -euo pipefail
session_directory=$(mktemp -d)
trap 'rm -rf -- "$session_directory"' EXIT
mkdir -m 700 "$session_directory/data" "$session_directory/runtime"
export XDG_DATA_HOME="$session_directory/data"
export XDG_RUNTIME_DIR="$session_directory/runtime"
export DRCOM_TEST_SECRET_SERVICE=1
dbus-run-session -- bash -euo pipefail -c '
  printf "\n" | gnome-keyring-daemon --unlock --components=secrets
  gnome-keyring-daemon --start --components=secrets
  dotnet test --project drcom4scutGUI.Tests/drcom4scutGUI.Tests.csproj --configuration Release --no-restore --report-trx --results-directory artifacts/test-results
'
