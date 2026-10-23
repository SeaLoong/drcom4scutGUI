#!/usr/bin/env python3
"""Photograph the native application with an isolated, password-free demo account.

The GUI and core are real binaries. The script never clicks Connect, saves a
password, changes login startup, or renders a replacement/headless interface.
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
                    *, client_area: bool = False) -> None:
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
    if client_area:
        # Photograph the actual app content, excluding Windows' invisible resize
        # margins and the runner desktop's unpainted non-client title bar.
        if not user.GetClientRect(hwnd, ctypes.byref(rect)):
            raise ctypes.WinError(ctypes.get_last_error())
        origin = w.POINT(rect.left, rect.top)
        if not user.ClientToScreen(hwnd, ctypes.byref(origin)):
            raise ctypes.WinError(ctypes.get_last_error())
        rect.right, rect.bottom = origin.x + rect.right, origin.y + rect.bottom
        rect.left, rect.top = origin.x, origin.y
    elif not user.GetWindowRect(hwnd, ctypes.byref(rect)):
        raise ctypes.WinError(ctypes.get_last_error())
    width, height = rect.right - rect.left, rect.bottom - rect.top
    if not (400 <= width <= 4000 and 400 <= height <= 4000):
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
    core_version = run(str(core), "--version")
    version = re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text()).group(1)
    expected_title = f"drcom4scutGUI {version} — {core_version}"
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
                for scene, mode in [("main", "eap"), ("portal", "portal")]:
                    config = directory / "config.json"
                    config.write_text(json.dumps(example_configuration(mode), ensure_ascii=False), encoding="utf-8")
                    config.chmod(0o600)
                    image = output / f"gui-{slug}-{scene}.png"
                    image.unlink(missing_ok=True)
                    with (output / f"gui-{slug}-{scene}.log").open("wb") as log:
                        process = subprocess.Popen([str(gui)], cwd=gui.parent, env=environment, stdout=log, stderr=subprocess.STDOUT)
                        try:
                            if system == "Windows":
                                capture_windows(process.pid, image, expected_title, client_area=True)
                            elif system == "Darwin":
                                capture_macos(process.pid, image, expected_title, helper=helper)
                            else:
                                capture_linux(process.pid, image, expected_title)
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
                                     "title": "有线连接窗口" if scene == "main" else "校园网页认证窗口",
                                     "description": "示例账号 student-demo，密码为空，未发起校园网认证。",
                                     "capture": ("实际 Windows 原生应用内容区域" if system == "Windows" else
                                                 f"实际 {platform_name} 原生应用窗口" + ("（X11 虚拟显示器）" if system == "Linux" else "")),
                                     "width": width, "height": height, "gui_commit": gui_sha, "core_commit": core_sha,
                                     "core_version": core_version, "sha256": hashlib.sha256(data).hexdigest(),
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
