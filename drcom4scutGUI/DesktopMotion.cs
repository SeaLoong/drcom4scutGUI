using System;
using System.Runtime.InteropServices;

namespace drcom4scutGUI;

internal static class DesktopMotion
{
    // No animation polling or background clock. Re-read on window activation.
    // Platforms without a reliable preference adapter use immediate feedback.
    internal static bool AllowsTransitions => OperatingSystem.IsWindows()
        && SystemParametersInfo(0x1042, 0, out bool enabled, 0) && enabled;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
