using System;
using System.IO;
using Microsoft.Win32;

namespace drcom4scutGUI;

/// <summary>Opt-in startup for the current user, with no elevated scheduled task.</summary>
internal static class WindowsStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "drcom4scutGUI";

    internal static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Key);
        return string.Equals(key?.GetValue(Name) as string, Command(), StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        if (enabled) key.SetValue(Name, Command(), RegistryValueKind.String);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }

    private static string Command()
    {
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "drcom4scutGUI.exe");
        return "\"" + executable + "\"";
    }
}
