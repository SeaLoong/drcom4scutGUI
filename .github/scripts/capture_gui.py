#!/usr/bin/env python3
"""Photograph the native application with an isolated, password-free demo account.

The GUI and core are real binaries. A compile-only capture capability supplies
local release responses to the real updater and opens its actual controls and
dialogs. No campus authentication, download, or update installation is attempted.
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import struct
import subprocess
import tempfile
import time
import zlib

ROOT = Path(__file__).resolve().parents[2]
READY_TITLE = " — drcom4scut "


def run(*command: str, **kwargs) -> str:
    return subprocess.check_output(command, text=True, encoding="utf-8", **kwargs).strip()


def write_png(path: Path, width: int, height: int, bgr: bytes) -> None:
    """Encode the actual Windows DIB pixels; no drawing or image composition."""
    rows = bytearray()
    for y in range(height):
        rows.append(0)
        row = bgr[y * width * 4:(y + 1) * width * 4]
        for x in range(width):
            pixel = row[x * 4:x * 4 + 4]
            rows.extend((pixel[2], pixel[1], pixel[0]))
    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def capture_windows(pid: int | None, destination: Path, title_fragment: str = READY_TITLE,
                    *, client_area: bool = False, native_frame: bool = False) -> dict:
    if not title_fragment:
        raise ValueError("A nonempty window title is required")
    from ctypes import wintypes as w
    user = ctypes.WinDLL("user32", use_last_error=True)
    gdi = ctypes.WinDLL("gdi32", use_last_error=True)
    user.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
    user.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    callback_type = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    user.EnumWindows.argtypes = [callback_type, w.LPARAM]
    user.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
    user.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
    user.IsWindowVisible.argtypes = [w.HWND]
    user.GetWindowRect.argtypes = [w.HWND, ctypes.POINTER(w.RECT)]
    user.GetClientRect.argtypes = [w.HWND, ctypes.POINTER(w.RECT)]
    user.ClientToScreen.argtypes = [w.HWND, ctypes.POINTER(w.POINT)]
    user.SetForegroundWindow.argtypes = [w.HWND]
    user.GetForegroundWindow.restype = w.HWND
    user.GetDC.argtypes = [w.HWND]
    user.GetDC.restype = w.HDC
    user.ReleaseDC.argtypes = [w.HWND, w.HDC]
    gdi.CreateCompatibleDC.argtypes = [w.HDC]
    gdi.CreateCompatibleDC.restype = w.HDC
    gdi.CreateCompatibleBitmap.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int]
    gdi.CreateCompatibleBitmap.restype = w.HBITMAP
    gdi.SelectObject.argtypes = [w.HDC, w.HGDIOBJ]
    gdi.SelectObject.restype = w.HGDIOBJ
    gdi.DeleteObject.argtypes = [w.HGDIOBJ]
    gdi.DeleteDC.argtypes = [w.HDC]
    gdi.BitBlt.argtypes = [w.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                          w.HDC, ctypes.c_int, ctypes.c_int, w.DWORD]
    class BitmapHeader(ctypes.Structure):
        _fields_ = [("size", w.DWORD), ("width", w.LONG), ("height", w.LONG),
                    ("planes", w.WORD), ("bits", w.WORD), ("compression", w.DWORD),
                    ("image_size", w.DWORD), ("x_pixels", w.LONG), ("y_pixels", w.LONG),
                    ("colors", w.DWORD), ("important", w.DWORD)]
    gdi.GetDIBits.argtypes = [w.HDC, w.HBITMAP, w.UINT, w.UINT, ctypes.c_void_p,
                             ctypes.POINTER(BitmapHeader), w.UINT]
    found = []
    @callback_type
    def visit(hwnd, _):
        owner = w.DWORD()
        user.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        text = ctypes.create_unicode_buffer(1024)
        user.GetWindowTextW(hwnd, text, len(text))
        if (pid is None or owner.value == pid) and user.IsWindowVisible(hwnd) and title_fragment in text.value:
            found.append(hwnd)
        return True
    deadline = time.monotonic() + 30
    while not found and time.monotonic() < deadline:
        user.EnumWindows(visit, 0)
        if not found:
            time.sleep(0.2)
    if len(found) != 1:
        raise RuntimeError("Expected one ready native Windows window owned by the GUI process")
    hwnd = found[0]
    user.SetForegroundWindow(hwnd)
    time.sleep(1)
    if user.GetForegroundWindow() != hwnd:
        raise RuntimeError("The selected Windows window could not be brought to the foreground for capture")
    rect = w.RECT()
    method = "GetWindowRect"
    frame_found = False
    working_area = None
    repositioned = False
    if native_frame:
        try:
            dwm = ctypes.WinDLL("dwmapi", use_last_error=True)
            dwm.DwmGetWindowAttribute.argtypes = [w.HWND, w.DWORD, ctypes.c_void_p, w.DWORD]
            dwm.DwmGetWindowAttribute.restype = ctypes.c_long
            # DWMWA_EXTENDED_FRAME_BOUNDS includes the real title bar and
            # excludes the invisible resize margins around a desktop window.
            frame_found = dwm.DwmGetWindowAttribute(hwnd, 9, ctypes.byref(rect), ctypes.sizeof(rect)) == 0
            frame_found = frame_found and rect.right > rect.left and rect.bottom > rect.top
            if frame_found:
                method = "DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9)"
        except (AttributeError, OSError):
            frame_found = False
    if native_frame and frame_found:
        class MonitorInfo(ctypes.Structure):
            _fields_ = [("size", w.DWORD), ("monitor", w.RECT), ("work", w.RECT), ("flags", w.DWORD)]
        user.MonitorFromWindow.argtypes = [w.HWND, w.DWORD]
        user.MonitorFromWindow.restype = w.HANDLE
        user.GetMonitorInfoW.argtypes = [w.HANDLE, ctypes.POINTER(MonitorInfo)]
        user.SetWindowPos.argtypes = [w.HWND, w.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, w.UINT]
        monitor = user.MonitorFromWindow(hwnd, 2)  # MONITOR_DEFAULTTONEAREST
        info = MonitorInfo()
        info.size = ctypes.sizeof(info)
        if not monitor or not user.GetMonitorInfoW(monitor, ctypes.byref(info)):
            raise ctypes.WinError(ctypes.get_last_error())
        work = info.work
        width, height = rect.right - rect.left, rect.bottom - rect.top
        if width > work.right - work.left or height > work.bottom - work.top:
            raise RuntimeError("The actual Windows capture window is larger than its monitor working area")
        left = max(work.left, min(rect.left, work.right - width))
        top = max(work.top, min(rect.top, work.bottom - height))
        if (left, top) != (rect.left, rect.top):
            outer = w.RECT()
            if not user.GetWindowRect(hwnd, ctypes.byref(outer)):
                raise ctypes.WinError(ctypes.get_last_error())
            # Move the real window, preserving its exact size and accounting for
            # GetWindowRect's invisible margins. Never fill/crop missing pixels.
            if not user.SetWindowPos(hwnd, None, outer.left + left - rect.left, outer.top + top - rect.top,
                                     0, 0, 0x0001 | 0x0004 | 0x0010):
                raise ctypes.WinError(ctypes.get_last_error())
            time.sleep(0.3)
            if dwm.DwmGetWindowAttribute(hwnd, 9, ctypes.byref(rect), ctypes.sizeof(rect)) != 0:
                raise RuntimeError("DWM could not confirm the moved native window bounds")
            repositioned = True
        if rect.left < work.left or rect.top < work.top or rect.right > work.right or rect.bottom > work.bottom:
            raise RuntimeError("The actual Windows window could not be fully positioned inside its monitor working area")
        working_area = {"left": work.left, "top": work.top, "right": work.right, "bottom": work.bottom}
    if not frame_found and client_area:
        # DWM is unavailable on some desktop configurations. Photograph only
        # the real app content there, without manufacturing window decoration.
        if not user.GetClientRect(hwnd, ctypes.byref(rect)):
            raise ctypes.WinError(ctypes.get_last_error())
        origin = w.POINT(rect.left, rect.top)
        if not user.ClientToScreen(hwnd, ctypes.byref(origin)):
            raise ctypes.WinError(ctypes.get_last_error())
        rect.right, rect.bottom = origin.x + rect.right, origin.y + rect.bottom
        rect.left, rect.top = origin.x, origin.y
        method = "GetClientRect/ClientToScreen (DWM unavailable)" if native_frame else "GetClientRect/ClientToScreen"
    elif not frame_found and not user.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise ctypes.WinError(ctypes.get_last_error())
    width, height = rect.right - rect.left, rect.bottom - rect.top
    if not (200 <= width <= 4000 and 100 <= height <= 4000):
        raise RuntimeError("Native window dimensions are outside the capture budget")
    screen = user.GetDC(None)
    memory = gdi.CreateCompatibleDC(screen)
    bitmap = gdi.CreateCompatibleBitmap(screen, width, height)
    previous = gdi.SelectObject(memory, bitmap)
    try:
        if not gdi.BitBlt(memory, 0, 0, width, height, screen, rect.left, rect.top, 0x00CC0020 | 0x40000000):
            raise ctypes.WinError(ctypes.get_last_error())
        gdi.SelectObject(memory, previous)
        header = BitmapHeader(ctypes.sizeof(BitmapHeader), width, -height, 1, 32, 0, 0, 0, 0, 0, 0)
        pixels = ctypes.create_string_buffer(width * height * 4)
        if gdi.GetDIBits(memory, bitmap, 0, height, pixels, ctypes.byref(header), 0) != height:
            raise ctypes.WinError(ctypes.get_last_error())
        if len(set(pixels.raw[::4])) < 8:
            raise RuntimeError("Windows returned an empty desktop capture")
        write_png(destination, width, height, pixels.raw)
    finally:
        gdi.SelectObject(memory, previous)
        gdi.DeleteObject(bitmap)
        gdi.DeleteDC(memory)
        user.ReleaseDC(None, screen)
    return {"method": method, "left": rect.left, "top": rect.top, "right": rect.right, "bottom": rect.bottom,
            "workingArea": working_area, "repositioned": repositioned}


def capture_linux(pid: int, destination: Path, title_fragment: str = READY_TITLE) -> None:
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        windows = re.findall(r"0x[0-9a-fA-F]+", run("xprop", "-root", "_NET_CLIENT_LIST"))
        for window in windows:
            properties = run("xprop", "-id", window, "_NET_WM_PID", "_NET_WM_NAME")
            owner = re.search(r"_NET_WM_PID\(CARDINAL\) = (\d+)", properties)
            if owner and int(owner.group(1)) == pid and title_fragment in properties:
                time.sleep(1)
                subprocess.run(["import", "-window", window, "-frame", str(destination)], check=True, timeout=15)
                return
        time.sleep(0.2)
    raise RuntimeError("No ready native X11 GUI window was found; run a window manager on DISPLAY")


def capture_macos(pid: int | None, destination: Path, title_fragment: str = READY_TITLE, *, helper: Path | None = None) -> None:
    if not title_fragment:
        raise ValueError("A nonempty window title is required")
    if helper is None:
        with tempfile.TemporaryDirectory(prefix="drcom-window-capture-") as temporary:
            helper = Path(temporary) / "capture-window"
            subprocess.run(["swiftc", str(ROOT / ".github/scripts/capture_window.swift"), "-o", str(helper)], check=True)
            capture_macos(pid, destination, title_fragment, helper=helper)
    else:
        subprocess.run([str(helper), str(pid or 0), str(destination), title_fragment], check=True, timeout=45)


def configuration_directory(environment: dict[str, str]) -> Path:
    if platform.system() == "Windows":
        from ctypes import wintypes as w
        shell = ctypes.WinDLL("shell32", use_last_error=True)
        path = ctypes.create_unicode_buffer(32768)
        # CSIDL_LOCAL_APPDATA matches Environment.SpecialFolder.LocalApplicationData.
        shell.SHGetFolderPathW.argtypes = [w.HWND, ctypes.c_int, w.HANDLE, w.DWORD, w.LPWSTR]
        if shell.SHGetFolderPathW(None, 0x001C, None, 0, path) != 0:
            raise RuntimeError("Cannot locate the current user's local application-data folder")
        return Path(path.value) / "drcom4scutGUI"
    if platform.system() == "Darwin":
        return Path.home() / "Library" / "Application Support" / "drcom4scutGUI"
    return Path(environment["XDG_CONFIG_HOME"]) / "drcom4scutGUI"


def example_configuration(mode: str) -> dict:
    return {"schemaVersion": 2, "accounts": [{"name": "student-demo", "encryptedPassword": "", "rememberPassword": False, "ip": ""}],
            "mac": "", "account": "student-demo", "auto": False, "noUdp": False, "mode": mode,
            "portalBase": "https://s.scut.edu.cn/" if mode == "portal" else "", "portalApi": "",
            "portalAcIp": "", "portalClientIp": "", "allowHttp": False}


def native_runtime(system: str, machine: str) -> str:
    architecture = {"x86_64": "x64", "amd64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(machine.lower())
    if architecture is None:
        raise ValueError("Native screenshot capture requires x64 or ARM64")
    return {"Windows": "win", "Darwin": "osx", "Linux": "linux"}[system] + "-" + architecture


def simulated_release(component: str, version: str, runtime: str, source: str) -> dict:
    """Local metadata only: assets are deliberately never downloaded."""
    repository = "SeaLoong/" + component
    core_targets = {"win-x64": "x86_64-pc-windows-msvc", "win-arm64": "aarch64-pc-windows-msvc",
                    "linux-x64": "x86_64-unknown-linux-gnu", "linux-arm64": "aarch64-unknown-linux-gnu",
                    "osx-x64": "x86_64-apple-darwin", "osx-arm64": "aarch64-apple-darwin"}
    target = core_targets[runtime] if component == "drcom4scut" else runtime
    extension = "zip" if runtime.startswith("win-") else "tar.gz"
    archive = f"{component}-{version}-{target}.{extension}"
    tag = "v" + version
    release_id = 990001 if component == "drcom4scutGUI" else 990002
    return {"id": release_id, "url": f"https://api.github.com/repos/{repository}/releases/{release_id}",
            "html_url": f"https://github.com/{repository}/releases/tag/{tag}",
            "tag_name": tag, "target_commitish": source, "draft": False, "prerelease": False,
            "name": "Local screenshot fixture: simulated release availability",
            "assets": [{"id": release_id + offset, "name": name, "state": "uploaded", "size": 1,
                        "digest": "sha256:" + "0" * 64,
                        "browser_download_url": f"https://github.com/{repository}/releases/download/{tag}/{name}"}
                       for offset, name in enumerate((archive, "SHA256SUMS"), start=1)]}


def next_patch(version: str) -> str:
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Screenshot fixtures require formal semantic versions")
    major, minor, patch = map(int, version.split("."))
    return f"{major}.{minor}.{patch + 1}"


def capture_fixture(scene: str, mode: str, gui_version: str, core_version: str,
                    runtime: str, gui_sha: str, core_sha: str) -> dict:
    available = scene in ("available", "confirm")
    return {"schema": 1, "guiVersion": gui_version, "coreVersion": core_version,
            "configuration": example_configuration(mode),
            "mockUpdateAvailability": True, "campusAuthentication": False,
            "gui": simulated_release("drcom4scutGUI", next_patch(gui_version) if available else gui_version, runtime, gui_sha),
            "core": simulated_release("drcom4scut", next_patch(core_version) if available else core_version, runtime, core_sha)}


def wait_for_capture_ready(process: subprocess.Popen, ready: Path, scenario: str, timeout: float = 45) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"The capture GUI exited with status {process.returncode}; inspect the scene log")
        if ready.exists():
            document = json.loads(ready.read_text(encoding="utf-8-sig"))
            expected_requests = {"idle": 0, "current": 1, "available": 2, "confirm": 1}[scenario]
            if (document.get("scenario") != scenario or not document.get("windowTitle")
                    or len(document.get("requests", [])) != expected_requests
                    or document.get("mockUpdateAvailability") is not True
                    or document.get("campusAuthentication") is not False):
                raise RuntimeError("The native application did not confirm the requested local fixture scenario")
            return document
        time.sleep(0.1)
    raise RuntimeError("The GUI did not settle the capture scenario; publish with -p:ScreenshotCapture=true and inspect the scene log")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gui", type=Path, required=True)
    parser.add_argument("--core", type=Path, required=True)
    parser.add_argument("--core-source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    gui, core, output = args.gui.resolve(strict=True), args.core.resolve(strict=True), args.output.resolve()
    system = platform.system()
    platform_name = {"Windows": "Windows", "Darwin": "macOS", "Linux": "Linux"}[system]
    slug = {"Windows": "windows", "Darwin": "macos", "Linux": "linux"}[system]
    gui_sha = run("git", "-C", str(ROOT), "rev-parse", "HEAD")
    core_sha = run("git", "-C", str(args.core_source), "rev-parse", "HEAD")
    gui_changes = run("git", "-C", str(ROOT), "status", "--porcelain", "--untracked-files=normal").splitlines()
    core_changes = run("git", "-C", str(args.core_source), "status", "--porcelain", "--untracked-files=normal").splitlines()
    gui_dirty, core_dirty = bool(gui_changes), bool(core_changes)
    core_version = run(str(core), "--version")
    core_semver = re.fullmatch(r"drcom4scut (\d+\.\d+\.\d+)", core_version)
    if core_semver is None:
        raise RuntimeError("The staged core did not report a formal semantic version")
    runtime = native_runtime(system, platform.machine())
    version = re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text()).group(1)
    output.mkdir(parents=True, exist_ok=True)
    (output / "screenshots.json").unlink(missing_ok=True)
    # Capture a staged publish directory, not an installed .app or user account.
    adjacent_core = gui.parent / ("drcom4scut.exe" if system == "Windows" else "drcom4scut")
    copied_core = not adjacent_core.exists()
    if copied_core:
        shutil.copy2(core, adjacent_core)
    elif hashlib.sha256(adjacent_core.read_bytes()).digest() != hashlib.sha256(core.read_bytes()).digest():
        raise RuntimeError("The staged GUI already has a different adjacent core")
    manifest = []
    try:
        with tempfile.TemporaryDirectory(prefix="drcom-screenshots-") as temporary:
            environment = dict(os.environ)
            environment["XDG_CONFIG_HOME"] = str(Path(temporary) / "config")
            environment["XDG_CACHE_HOME"] = str(Path(temporary) / "cache")
            directory = configuration_directory(environment)
            if directory.exists():
                raise RuntimeError("Screenshot capture uses a fresh configuration directory; an existing user configuration is never changed")
            directory.mkdir(mode=0o700, parents=True)
            try:
                helper = Path(temporary) / "capture-window"
                if system == "Darwin":
                    subprocess.run(["swiftc", str(ROOT / ".github/scripts/capture_window.swift"), "-o", str(helper)], check=True)
                for scene, mode, scenario, title in [
                    ("main", "eap", "idle", "有线连接窗口"),
                    ("portal", "portal", "idle", "校园网页认证窗口"),
                    ("update-current", "eap", "current", "手动检查：当前版本已是最新"),
                    ("update-available", "eap", "available", "图形界面与核心的新版本提示"),
                    ("update-confirm", "eap", "confirm", "更新图形界面的确认对话框"),
                ]:
                    config = directory / "config.json"
                    config.write_text(json.dumps(example_configuration(mode), ensure_ascii=False), encoding="utf-8")
                    config.chmod(0o600)
                    image = output / f"gui-{slug}-{scene}.png"
                    image.unlink(missing_ok=True)
                    fixture_file = output / f"gui-{slug}-{scene}.fixture.json"
                    fixture = capture_fixture(scenario, mode, version, core_semver[1], runtime, gui_sha, core_sha)
                    fixture_file.write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
                    ready = Path(temporary) / f"{scene}.ready.json"
                    command = [str(gui), f"--capture-update={scenario}",
                               f"--capture-update-fixture={fixture_file}", f"--capture-ready-file={ready}"]
                    with (output / f"gui-{slug}-{scene}.log").open("wb") as log:
                        process = subprocess.Popen(command, cwd=gui.parent, env=environment, stdout=log, stderr=subprocess.STDOUT)
                        try:
                            settled = wait_for_capture_ready(process, ready, scenario)
                            capture_title = settled["windowTitle"]
                            native_bounds = None
                            if system == "Windows":
                                native_bounds = capture_windows(process.pid, image, capture_title, client_area=True, native_frame=True)
                            elif system == "Darwin":
                                capture_macos(process.pid, image, capture_title, helper=helper)
                            else:
                                capture_linux(process.pid, image, capture_title)
                            if process.poll() is not None:
                                raise RuntimeError("The GUI exited before its screenshot was verified")
                        finally:
                            process.terminate()
                            try:
                                process.wait(timeout=5)
                            except subprocess.TimeoutExpired:
                                process.kill()
                                process.wait(timeout=5)
                    data = image.read_bytes()
                    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
                        raise RuntimeError("The native capture did not produce a PNG image")
                    width, height = struct.unpack(">II", data[16:24])
                    manifest.append({"file": image.name, "platform": platform_name, "kind": "GUI", "version": version,
                                     "title": title,
                                     "description": "示例账号 student-demo，密码为空，未发起校园网认证。" +
                                         ("更新信息来自本地模拟发布响应，未下载或安装更新。" if scenario != "idle" else ""),
                                     "capture": (("实际 Windows 原生应用窗口（DWM 可见窗口边界，含标题栏）" if native_bounds["method"].startswith("Dwm")
                                                  else "实际 Windows 原生应用内容区域（DWM 不可用）") if system == "Windows" else
                                                 f"实际 {platform_name} 原生应用窗口" + ("（X11 虚拟显示器）" if system == "Linux" else "")),
                                     "nativeWindowBounds": native_bounds,
                                     "width": width, "height": height, "gui_commit": gui_sha, "core_commit": core_sha,
                                     "gui_source_dirty": gui_dirty, "core_source_dirty": core_dirty,
                                     "gui_source_changes": gui_changes, "core_source_changes": core_changes,
                                     "core_version": core_version, "sha256": hashlib.sha256(data).hexdigest(),
                                     "capture_build": "SCREENSHOT_CAPTURE", "update_scenario": scenario,
                                     "mock_update_availability": scenario != "idle", "campus_authentication": False,
                                     "fixture": fixture_file.name, "fixture_sha256": hashlib.sha256(fixture_file.read_bytes()).hexdigest(),
                                     "fixture_requests": settled["requests"], "window_title": capture_title,
                                     "simulated_gui_release": fixture["gui"]["tag_name"],
                                     "simulated_core_release": fixture["core"]["tag_name"],
                                     "gui_executable_sha256": hashlib.sha256(gui.read_bytes()).hexdigest(),
                                     "core_executable_sha256": hashlib.sha256(core.read_bytes()).hexdigest()})
            finally:
                shutil.rmtree(directory)
    finally:
        if copied_core:
            adjacent_core.unlink()
    (output / "screenshots.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    # Windows CI can redirect stdout through a legacy console encoding.
    # The artifact above remains UTF-8; escaped log output also works there.
    print(json.dumps(manifest, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
