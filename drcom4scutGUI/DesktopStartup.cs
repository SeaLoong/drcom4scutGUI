using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace drcom4scutGUI;

/// <summary>Opt-in login startup belongs to this user; no privileged helper is installed.</summary>
internal static class DesktopStartup
{
    private const string Identifier = "net.sealoong.drcom4scutGUI";
    private const string WindowsKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsName = "drcom4scutGUI";

    internal static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows()) return WindowsEnabled();
        return File.Exists(StartupPath);
    }

    internal static void SetEnabled(bool enabled)
    {
        string executable = PlatformPaths.GuiExecutablePath;
        if (OperatingSystem.IsWindows()) { SetWindowsEnabled(enabled, executable); return; }
        string path = StartupPath;
        if (!enabled) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string contents = OperatingSystem.IsMacOS() ? MacDefinition(executable) : LinuxDefinition(executable);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string MacDefinition(string executable)
    {
        XDocument document = new(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "Label"), new XElement("string", Identifier),
                new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", executable)),
                new XElement("key", "RunAtLoad"), new XElement("true"))));
        using StringWriter writer = new();
        document.Save(writer);
        return writer.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    internal static string LinuxDefinition(string executable)
    {
        if (executable.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidOperationException("登录启动路径不能包含换行。");
        // First quote the Exec argument, then escape the desktop file's string
        // value. Its parser removes one escaping layer before parsing Exec.
        string quoted = executable.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal);
        return "[Desktop Entry]\nType=Application\nName=drcom4scutGUI\nExec=\"" + quoted + "\"\nTerminal=false\n";
    }

    private static string StartupPath => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Identifier + ".plist")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured && Path.IsPathRooted(configured)
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "drcom4scutGUI.desktop");

    [SupportedOSPlatform("windows")]
    private static bool WindowsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(WindowsKey);
        return string.Equals(key?.GetValue(WindowsName) as string, "\"" + Environment.ProcessPath + "\"", StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsEnabled(bool enabled, string executable)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(WindowsKey, writable: true);
        if (enabled) key.SetValue(WindowsName, "\"" + executable + "\"", RegistryValueKind.String);
        else key.DeleteValue(WindowsName, throwOnMissingValue: false);
    }
}
