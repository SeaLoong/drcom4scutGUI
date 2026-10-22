using System;
using System.IO;

namespace drcom4scutGUI;

/// <summary>The operating system releases this per-user lock if the GUI crashes.</summary>
internal static class DesktopInstance
{
    internal static FileStream? TryAcquire(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStreamOptions options = new() { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(Path.Combine(directory, ".instance.lock"), options); }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33) { return null; }
    }
}
