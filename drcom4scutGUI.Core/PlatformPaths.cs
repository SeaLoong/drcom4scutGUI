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

    public static string ResolveCoreExecutablePath(string baseDirectory, bool windows, bool macOS)
    {
        string directory = Path.GetFullPath(baseDirectory);
        string name = windows ? "drcom4scut.exe" : "drcom4scut";
        if (macOS)
        {
            DirectoryInfo contents = new DirectoryInfo(directory);
            if (contents.Name == "MacOS" && contents.Parent?.Name == "Contents"
                && contents.Parent.Parent is { Extension: ".app", Parent: not null } bundle)
            {
                string adjacent = Path.Combine(bundle.Parent.FullName, name);
                if (File.Exists(adjacent)) return adjacent;
            }
        }
        return Path.Combine(directory, name);
    }
}
