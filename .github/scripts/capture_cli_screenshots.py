#!/usr/bin/env python3
"""Capture real core output in a native Windows or macOS terminal window.

Only version, interface enumeration and configuration validation are executed.
The temporary example account is never used to authenticate.
"""
from __future__ import annotations

import argparse
import base64
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import shlex
import shutil
import signal
import struct
import subprocess
import tempfile
import time
import uuid

from capture_gui import capture_macos, capture_windows, run


def powershell_literal(value: str | Path) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def encoded_powershell(source: str) -> str:
    return base64.b64encode(source.encode("utf-16le")).decode("ascii")


def windows_session(directory: Path, title: str) -> str:
    path = powershell_literal
    return f"""$ErrorActionPreference = 'Stop'
try {{
    Set-Location -LiteralPath {path(directory)}
    $Host.UI.RawUI.WindowTitle = {path(title)}
    $Host.UI.RawUI.BackgroundColor = 'DarkBlue'
    $Host.UI.RawUI.ForegroundColor = 'Gray'
    $columns = [Math]::Min(112, $Host.UI.RawUI.LargestWindowSize.Width)
    $rows = [Math]::Min(28, $Host.UI.RawUI.LargestWindowSize.Height)
    $buffer = $Host.UI.RawUI.BufferSize
    $buffer.Width = [Math]::Max($buffer.Width, $columns)
    $buffer.Height = [Math]::Max($buffer.Height, $rows)
    $Host.UI.RawUI.BufferSize = $buffer
    $Host.UI.RawUI.WindowSize = [System.Management.Automation.Host.Size]::new($columns, $rows)
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CaptureConsole {{
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}}
'@
    Clear-Host
    function Invoke-Visible([string[]]$Arguments) {{
        Write-Host ('PS> .\\drcom4scut.exe ' + ($Arguments -join ' ')) -ForegroundColor Cyan
        & '.\\drcom4scut.exe' @Arguments | Tee-Object -FilePath {path(directory / 'transcript.txt')} -Append
        if ($LASTEXITCODE -ne 0) {{ throw "Core command failed with exit code $LASTEXITCODE" }}
        Write-Host ''
    }}
    Invoke-Visible -Arguments @('--version')
    Invoke-Visible -Arguments @('--list-interfaces')
    Invoke-Visible -Arguments @('--config', 'config.yml', '--check-config')
    [uint32]$consoleOwner = 0
    $consoleHandle = [CaptureConsole]::GetConsoleWindow()
    if ($consoleHandle -eq [IntPtr]::Zero) {{ throw 'No native console window is available' }}
    [void][CaptureConsole]::GetWindowThreadProcessId($consoleHandle, [ref]$consoleOwner)
    $ready = @{{shell_pid = $PID; window_pid = $consoleOwner}} | ConvertTo-Json -Compress
    [IO.File]::WriteAllText({path(directory / 'ready.json')}, $ready)
}} catch {{
    [IO.File]::WriteAllText({path(directory / 'error.txt')}, $_.Exception.ToString())
    exit 1
}}
while (!(Test-Path -LiteralPath {path(directory / 'done')})) {{ Start-Sleep -Milliseconds 200 }}
"""


def macos_session(directory: Path, title: str) -> str:
    quote = lambda value: shlex.quote(str(value))
    lines = [
        "#!/bin/bash", "set -euo pipefail",
        "trap 'status=$?; if [ \"$status\" -ne 0 ]; then printf \"Core terminal command failed: %s\\n\" \"$status\" > error.txt; fi' EXIT",
        f"cd {quote(directory)}",
        f"printf '\\033]0;%s\\007' {quote(title)}",
        "printf '\\033[8;28;112t\\033[2J\\033[H'",
    ]
    for arguments in [("--version",), ("--list-interfaces",), ("--config", "config.yml", "--check-config")]:
        command = "./drcom4scut " + " ".join(quote(argument) for argument in arguments)
        lines.extend([f"printf '%s\\n' {quote('$ ' + command)}", f"{command} | tee -a transcript.txt", "printf '\\n'"])
    lines.extend(["printf '{\"shell_pid\":%s}\\n' \"$$\" > ready.json", "while [[ ! -f done ]]; do sleep 0.2; done"])
    return "\n".join(lines) + "\n"


MACOS_LAUNCHER = r"""import AppKit
import Foundation

guard CommandLine.arguments.count == 2,
      let terminal = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Terminal") else {
    fatalError("Terminal.app is required for native CLI capture")
}
let document = URL(fileURLWithPath: CommandLine.arguments[1])
let configuration = NSWorkspace.OpenConfiguration()
configuration.createsNewApplicationInstance = true
configuration.activates = true
NSWorkspace.shared.open([document], withApplicationAt: terminal, configuration: configuration) { application, error in
    guard let application = application else {
        fatalError("Cannot open a native Terminal window: \(String(describing: error))")
    }
    print(application.processIdentifier)
    exit(0)
}
RunLoop.main.run()
"""


class NativeTerminal:
    """Own exactly one launched console or a new Terminal.app instance."""

    def __init__(self, directory: Path, title: str, system: str):
        self.directory, self.title, self.system = directory, title, system
        self.pid: int | None = None
        self.handle = None

    def start(self) -> None:
        if self.system == "Windows":
            powershell = shutil.which("powershell.exe")
            if powershell is None:
                raise RuntimeError("Windows PowerShell is required for native console capture")
            script = self.directory / "capture.ps1"
            script.write_text(windows_session(self.directory, self.title), encoding="utf-8-sig")
            child = encoded_powershell(f"& {powershell_literal(script)}")
            launch = f"""$ErrorActionPreference = 'Stop'
$process = Start-Process -FilePath {powershell_literal(powershell)} -ArgumentList @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', '{child}') -WindowStyle Normal -PassThru
[Console]::Out.WriteLine($process.Id)
"""
            self.pid = int(run(powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded_powershell(launch), timeout=30))
            from ctypes import wintypes as w
            self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            self.kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
            self.kernel.OpenProcess.restype = w.HANDLE
            self.kernel.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
            self.kernel.TerminateProcess.argtypes = [w.HANDLE, w.UINT]
            self.kernel.CloseHandle.argtypes = [w.HANDLE]
            self.handle = self.kernel.OpenProcess(0x00100001, False, self.pid)  # SYNCHRONIZE | PROCESS_TERMINATE
            if not self.handle:
                raise ctypes.WinError(ctypes.get_last_error())
        else:
            script = self.directory / "capture.command"
            script.write_text(macos_session(self.directory, self.title), encoding="utf-8")
            script.chmod(0o700)
            source = self.directory / "launch-terminal.swift"
            source.write_text(MACOS_LAUNCHER, encoding="utf-8")
            launcher = self.directory / "launch-terminal"
            subprocess.run(["swiftc", str(source), "-o", str(launcher)], check=True, timeout=120)
            # Refuse to claim or close an instance which was already running.
            existing = subprocess.run(["pgrep", "-x", "Terminal"], capture_output=True, text=True, check=False)
            if existing.returncode not in (0, 1):
                raise RuntimeError("Cannot identify existing Terminal instances")
            previous = {int(value) for value in existing.stdout.split()}
            launched = int(run(str(launcher), str(script), timeout=30))
            if launched in previous:
                raise RuntimeError("macOS reused an existing Terminal instance; capture requires a new instance")
            self.pid = launched

    def wait_ready(self) -> dict:
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            error = self.directory / "error.txt"
            if error.exists():
                raise RuntimeError(error.read_text(encoding="utf-8-sig"))
            ready = self.directory / "ready.json"
            if ready.exists():
                try:
                    return json.loads(ready.read_text(encoding="utf-8-sig"))
                except json.JSONDecodeError:
                    pass  # The terminal may still be finishing its small ready file.
            if self.handle and self.kernel.WaitForSingleObject(self.handle, 0) == 0:
                raise RuntimeError("The native PowerShell console exited before completing the commands")
            time.sleep(0.2)
        raise RuntimeError("The native terminal did not finish its commands; an interactive desktop is required")

    def close(self) -> None:
        (self.directory / "done").touch()
        if self.handle:
            try:
                if self.kernel.WaitForSingleObject(self.handle, 5000) == 0x102:
                    self.kernel.TerminateProcess(self.handle, 1)
                    self.kernel.WaitForSingleObject(self.handle, 5000)
            finally:
                self.kernel.CloseHandle(self.handle)
                self.handle = None
        elif self.pid is not None and self.system == "Darwin":
            # This PID belongs to the new app instance returned by NSWorkspace.
            time.sleep(0.4)
            try:
                os.kill(self.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", required=True, type=Path)
    parser.add_argument("--core-source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    system = platform.system()
    if system not in ("Windows", "Darwin"):
        parser.error("Run this capture on native Windows or macOS; Linux has its own recorded terminal capture")
    core = args.core.resolve(strict=True)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    platform_name, slug = ("Windows", "windows") if system == "Windows" else ("macOS", "macos")
    image = output / f"{slug}-cli-check.png"
    manifest = output / "cli-screenshots.json"
    failure = output / "cli-capture-failure.json"
    for artifact in (image, manifest, failure):
        artifact.unlink(missing_ok=True)
    try:
        core_sha = run("git", "-C", str(args.core_source), "rev-parse", "HEAD")
        core_version = run(str(core), "--version", timeout=15)
        with tempfile.TemporaryDirectory(prefix="drcom-cli-capture-") as temporary:
            directory = Path(temporary)
            executable = directory / ("drcom4scut.exe" if system == "Windows" else "drcom4scut")
            shutil.copy2(core, executable)
            if system != "Windows":
                executable.chmod(0o700)
            config = directory / "config.yml"
            config.write_text("username: screenshot-student\npassword: screenshot-example-password\nhostname: screenshot-demo\n", encoding="utf-8")
            config.chmod(0o600)
            title = f"drcom4scut CLI - {uuid.uuid4().hex[:12]}"
            terminal = NativeTerminal(directory, title, system)
            try:
                terminal.start()
                ready = terminal.wait_ready()
                if system == "Windows":
                    capture_windows(ready.get("window_pid") or None, image, title)
                else:
                    capture_macos(terminal.pid, image, title)
            finally:
                terminal.close()
                transcript = directory / "transcript.txt"
                if transcript.exists():
                    # PowerShell 5 Tee-Object writes UTF-16LE; normalize only this text evidence.
                    data = transcript.read_bytes()
                    text = data.decode("utf-16") if data.startswith(b"\xff\xfe") else data.decode("utf-8-sig")
                    (output / f"{slug}-cli-output.txt").write_text(text, encoding="utf-8")
        data = image.read_bytes()
        if not data.startswith(b"\x89PNG\r\n\x1a\n"):
            raise RuntimeError("The native terminal capture did not produce a PNG image")
        width, height = struct.unpack(">II", data[16:24])
        record = {
            "file": image.name, "platform": platform_name, "kind": "CLI",
            "title": "终端自检", "version": core_version.removeprefix("drcom4scut "),
            "description": "实际运行版本查询、网卡列表和示例配置检查，未发起校园网认证。",
            "capture": f"实际 {platform_name} 原生" + (" PowerShell 控制台" if system == "Windows" else " Terminal.app 窗口"),
            "width": width, "height": height, "core_commit": core_sha,
            "source_url": f"https://github.com/SeaLoong/drcom4scut/commit/{core_sha}",
            "core_version": core_version, "os": platform.platform(), "machine": platform.machine(),
            "sha256": hashlib.sha256(data).hexdigest(),
            "core_executable_sha256": hashlib.sha256(core.read_bytes()).hexdigest(),
            "commands": ["drcom4scut --version", "drcom4scut --list-interfaces", "drcom4scut --config config.yml --check-config"],
        }
        manifest.write_text(json.dumps([record], ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Captured native {platform_name} CLI: {image}")
    except Exception as error:
        image.unlink(missing_ok=True)
        failure.write_text(json.dumps({"platform": platform_name, "error": str(error), "os": platform.platform()}, indent=2) + "\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
