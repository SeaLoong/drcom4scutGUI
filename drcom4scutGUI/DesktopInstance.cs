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
        // FileStream reports Unix flock conflicts with the platform's errno:
        // EWOULDBLOCK is 11 on Linux but 35 on Darwin. Keep unrelated I/O errors
        // visible instead of treating an inaccessible lock as another instance.
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33
            || OperatingSystem.IsMacOS() && (error.HResult & 0xffff) == 35) { return null; }
    }
}
