using System;
using System.IO;

namespace drcom4scutGUI;

public static class PlatformPaths
{
    public static string ConfigurationDirectory
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "drcom4scutGUI");
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "drcom4scutGUI");
            string? configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string root = configured is not null && Path.IsPathFullyQualified(configured) ? configured : Path.Combine(home, ".config");
            return Path.Combine(root, "drcom4scutGUI");
        }
    }

    public static string CoreExecutablePath => ResolveCoreExecutablePath(AppContext.BaseDirectory,
        OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    public static string GuiExecutablePath => ResolveGuiExecutablePath(AppContext.BaseDirectory,
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序安装路径。"), OperatingSystem.IsMacOS());

    public static string ResolveCoreExecutablePath(string baseDirectory, bool windows, bool macOS)
    {
        string directory = Path.GetFullPath(baseDirectory);
        string name = windows ? "drcom4scut.exe" : "drcom4scut";
        if (macOS && MacBundle(directory) is { Parent: not null } bundle)
        {
            string adjacent = Path.Combine(bundle.Parent.FullName, name);
            if (File.Exists(adjacent)) return adjacent;
        }
        return Path.Combine(directory, name);
    }

    /// <summary>Login startup must enter the native bundle launcher, not its managed payload.</summary>
    public static string ResolveGuiExecutablePath(string baseDirectory, string processPath, bool macOS)
    {
        if (macOS && MacBundle(Path.GetFullPath(baseDirectory)) is { } bundle)
        {
            string launcher = Path.Combine(bundle.FullName, "Contents", "MacOS", "drcom4scutGUI");
            if (File.Exists(launcher)) return launcher;
        }
        return Path.GetFullPath(processPath);
    }

    private static DirectoryInfo? MacBundle(string directory)
    {
        DirectoryInfo payload = new(directory);
        DirectoryInfo? contents = payload.Name == "MacOS" ? payload.Parent :
            payload.Name == "publish" && payload.Parent?.Name == "Resources" ? payload.Parent.Parent : null;
        return contents is { Name: "Contents", Parent: { } bundle }
            && string.Equals(bundle.Extension, ".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }
}
