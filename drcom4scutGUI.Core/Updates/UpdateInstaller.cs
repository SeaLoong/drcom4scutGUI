using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

/// <summary>Installs only a verified component; account files and the other component are never part of a transaction.</summary>
internal static class UpdateInstaller
{
    internal const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static string UpdatesRoot => Path.Combine(PlatformPaths.ConfigurationDirectory, "updates");
    private static readonly HashSet<string> PrivateNames = new(StringComparer.OrdinalIgnoreCase)
    { "accounts.json", "gui.json", "drcom4scutgui.json", "config.json", "config.yml", "config.yaml", "core.yml", "core.yaml", "drcom4scut", "drcom4scut.exe" };

    public static async Task InstallCoreAsync(PreparedUpdate update, string coreExecutablePath, CancellationToken cancellationToken)
    {
        Require(update, UpdateComponent.Core);
        string target = RequireExecutable(coreExecutablePath, UpdateComponent.Core, update.Release.Runtime);
        await UpdateArchive.VerifyAsync(update.StagingDirectory, update.Release, cancellationToken).ConfigureAwait(false);
        string source = Path.Combine(update.StagingDirectory, CoreName(update.Release.Runtime));
        await VerifyCoreAsync(source, update.Release, cancellationToken).ConfigureAwait(false);
        UnixIdentity? owner = ReadIdentity(target);
        UnixFileMode? mode = !OperatingSystem.IsWindows() ? File.GetUnixFileMode(target) : null;
        if (mode is { } permissions && (permissions & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0)
            throw new IOException("核心含有特殊系统权限，请由管理员更新。");
        bool rawCapability = OperatingSystem.IsLinux() && ReadRawCapability(target);
        string originalHash = await HashAsync(target, cancellationToken).ConfigureAwait(false);
        UpdateFile entry = await DescribeAsync(source, Path.GetFileName(target), cancellationToken).ConfigureAwait(false);
        if (mode is { } oldMode) entry = entry with { Mode = (int)oldMode };
        await ApplyFilesAsync(update.StagingDirectory, Path.GetDirectoryName(target)!, [entry], async () =>
        {
            await VerifyCoreAsync(target, update.Release, cancellationToken).ConfigureAwait(false);
            if (OperatingSystem.IsLinux() && rawCapability) await RestoreRawCapabilityAsync(target, entry.Sha256, cancellationToken).ConfigureAwait(false);
        }, null, cancellationToken, owner, async () =>
        {
            if (await HashAsync(target, cancellationToken).ConfigureAwait(false) != originalHash || ReadIdentity(target) != owner)
                throw new InvalidDataException("核心在准备更新时发生变化，请重新检查更新。");
            CommandResult installed = await RunAsync(target, ["--version"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (installed.ExitCode != 0 || UpdateService.ParseVersion(update.Release.Version).CompareTo(UpdateService.ParseVersion(installed.Output.Trim())) <= 0)
                throw new InvalidDataException("已安装的核心版本不低于此更新，安装已取消。");
        }).ConfigureAwait(false);
    }

    public static async Task ScheduleGuiInstallAsync(PreparedUpdate update, string guiExecutablePath, CancellationToken cancellationToken)
    {
        await ValidateGuiInstallAsync(update, guiExecutablePath, cancellationToken).ConfigureAwait(false);
        string target = RequireExecutable(guiExecutablePath, UpdateComponent.Gui, update.Release.Runtime);
        UpdateHelper.CleanupCompleted();
        CreatePrivateDirectory(UpdatesRoot);
        string directory = Path.Combine(UpdatesRoot, "update-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(directory);
        Process? helper = null;
        try
        {
            string payload = Path.Combine(directory, "new");
            await CopyTreeAsync(update.StagingDirectory, payload, cancellationToken).ConfigureAwait(false);
            await UpdateArchive.VerifyAsync(payload, update.Release, cancellationToken).ConfigureAwait(false);
            // The helper needs the entire self-contained runtime. It must remain independent
            // of both the running installation and the disposable download directory.
            await CopyTreeAsync(payload, Path.Combine(directory, "helper"), cancellationToken).ConfigureAwait(false);
            using Process current = Process.GetCurrentProcess();
            UpdatePlan plan = new()
            {
                Release = update.Release, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ParentId = current.Id, ParentStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
                CreatedUtcTicks = DateTime.UtcNow.Ticks, TargetExecutable = target,
                TargetSha256 = await HashAsync(target, cancellationToken).ConfigureAwait(false),
                Files = await DescribeTreeAsync(payload, cancellationToken).ConfigureAwait(false)
            };
            string planPath = Path.Combine(directory, "plan.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(planPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ProcessStartInfo start = HelperStartInfo(directory, plan);
            helper = Process.Start(start) ?? throw new IOException("无法启动独立更新程序。");
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (helper.HasExited || elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new IOException("独立更新程序未能就绪，当前版本没有改变。");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (helper is not null)
            {
                try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            TryDeleteDirectory(directory);
            throw;
        }
        finally { helper?.Dispose(); }
    }

    internal static Task ValidateGuiInstallAsync(PreparedUpdate update, string guiExecutablePath, CancellationToken cancellationToken)
        => ValidateGuiInstallAsync(update, guiExecutablePath, null, cancellationToken);

    internal static async Task ValidateGuiInstallAsync(PreparedUpdate update, string guiExecutablePath,
        Func<string, CancellationToken, Task<string>>? versionProbe, CancellationToken cancellationToken)
    {
        Require(update, UpdateComponent.Gui);
        string target = RequireExecutable(guiExecutablePath, UpdateComponent.Gui, update.Release.Runtime);
        string metadata = Path.Combine(update.StagingDirectory, "BUILD-INFO.json");
        RejectLinks(metadata);
        if (!File.Exists(metadata) || new FileInfo(metadata).Length > 65536) throw new InvalidDataException("GUI 更新缺少有效的核心兼容信息。");
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(metadata, cancellationToken).ConfigureAwait(false));
        string minimum = UpdateService.String(json.RootElement, "minimum_core_version");
        UpdateService.SemanticVersion required = UpdateService.ParseVersion(minimum);
        if (required.Prerelease.Length != 0) throw new InvalidDataException("GUI 更新的最低核心版本无效。");
        bool mac = update.Release.Runtime.StartsWith("osx-", StringComparison.Ordinal);
        string payload = mac ? Path.Combine(MacBundle(target), "Contents", "Resources", "publish") : Path.GetDirectoryName(target)!;
        string core = RequireExecutable(PlatformPaths.ResolveCoreExecutablePath(payload, OperatingSystem.IsWindows(), mac), UpdateComponent.Core, update.Release.Runtime);
        string version;
        if (versionProbe is not null) version = await versionProbe(core, cancellationToken).ConfigureAwait(false);
        else
        {
            await UpdateArchive.VerifyExecutableAsync(core, update.Release.Runtime, true, cancellationToken).ConfigureAwait(false);
            CommandResult result = await RunAsync(core, ["--version"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidDataException("无法读取现有核心版本，请先检查核心更新。");
            version = result.Output.Trim();
        }
        if (UpdateService.ParseVersion(version).CompareTo(required) < 0)
            throw new InvalidDataException($"此 GUI 更新需要核心 {minimum} 或更新版本。请先检查核心更新；当前 GUI 没有改变。");
        await UpdateArchive.VerifyAsync(update.StagingDirectory, update.Release, cancellationToken).ConfigureAwait(false);
    }

    private static void Require(PreparedUpdate update, UpdateComponent component)
    {
        ArgumentNullException.ThrowIfNull(update);
        UpdateService.ValidateRelease(update.Release);
        if (update.Release.Component != component || update.Release.Runtime != UpdateService.NativeRuntime)
            throw new InvalidDataException("更新包不属于本组件或当前系统。");
        RejectLinks(update.StagingDirectory);
    }

    internal static string CoreName(string runtime) => runtime.StartsWith("win-", StringComparison.Ordinal) ? "drcom4scut.exe" : "drcom4scut";
    internal static string GuiName(string runtime) => runtime.StartsWith("win-", StringComparison.Ordinal) ? "drcom4scutGUI.exe" : "drcom4scutGUI";
    internal static string GuiLaunchPath(string root, string runtime) => runtime.StartsWith("osx-", StringComparison.Ordinal)
        ? Path.Combine(root, "drcom4scutGUI.app", "Contents", "MacOS", "drcom4scutGUI") : Path.Combine(root, GuiName(runtime));
    internal static string GuiPayloadPath(string root, string runtime) => runtime.StartsWith("osx-", StringComparison.Ordinal)
        ? Path.Combine(root, "drcom4scutGUI.app", "Contents", "Resources", "publish", "drcom4scutGUI") : GuiLaunchPath(root, runtime);

    internal static ProcessStartInfo HelperStartInfo(string directory, UpdatePlan plan)
    {
        ProcessStartInfo start = new(GuiLaunchPath(Path.Combine(directory, "helper"), plan.Release.Runtime))
        { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true };
        start.ArgumentList.Add(UpdateHelper.Switch);
        start.ArgumentList.Add(Path.Combine(directory, "plan.json"));
        start.ArgumentList.Add(plan.Nonce);
        return start;
    }

    internal static ProcessStartInfo RestartStartInfo(string executable) => new(executable)
    { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = false };

    internal static string RequireExecutable(string path, UpdateComponent component, string runtime)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
            throw new InvalidDataException("安装路径必须是规范的绝对路径。");
        string expected = component == UpdateComponent.Core ? CoreName(runtime) : GuiName(runtime);
        if (Path.GetFileName(path) != expected || !File.Exists(path)) throw new InvalidDataException("无法确定现有组件的安装位置。");
        RejectLinks(path);
        if (component == UpdateComponent.Gui && runtime.StartsWith("osx-", StringComparison.Ordinal)) _ = MacBundle(path);
        return path;
    }

    internal static string MacBundle(string nativeExecutable)
    {
        DirectoryInfo? macOS = Directory.GetParent(nativeExecutable);
        DirectoryInfo? contents = macOS?.Parent;
        DirectoryInfo? app = contents?.Parent;
        if (macOS?.Name != "MacOS" || contents?.Name != "Contents" || app?.Extension != ".app")
            throw new InvalidDataException("macOS 更新必须以完整应用程序包为单位。");
        return app.FullName;
    }

    internal static async Task VerifyCoreAsync(string path, UpdateRelease release, CancellationToken cancellationToken)
    {
        await UpdateArchive.VerifyExecutableAsync(path, release.Runtime, true, cancellationToken).ConfigureAwait(false);
        CommandResult result = await RunAsync(path, ["--version"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || UpdateService.ParseVersion(result.Output.Trim()).Plain != release.Version)
            throw new InvalidDataException("更新核心未报告预期版本。");
    }

    internal static async Task ApplyFilesAsync(string source, string destination, IReadOnlyList<UpdateFile> entries,
        Func<Task>? verify, Func<Task>? activate, CancellationToken cancellationToken, UnixIdentity? owner = null, Func<Task>? beforeApply = null)
    {
        RejectLinks(destination);
        using FileStream gate = OpenInstallLock(destination);
        if (beforeApply is not null) await beforeApply().ConfigureAwait(false);
        string transaction = Path.Combine(destination, ".drcom-update-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(transaction);
        string incoming = Path.Combine(transaction, "incoming"), backup = Path.Combine(transaction, "backup");
        List<(string Target, string? Backup)> applied = [];
        bool rollbackSucceeded = true;
        try
        {
            foreach (UpdateFile entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string from = SafeChild(source, entry.Path), to = SafeChild(incoming, entry.Path);
                RejectLinks(from);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                await CopyFileAsync(from, to, cancellationToken).ConfigureAwait(false);
                await VerifyFileAsync(to, entry, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(to, (UnixFileMode)entry.Mode);
                    if (owner is { } identity) PreserveOwner(to, identity);
                }
            }
            foreach (UpdateFile entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = SafeChild(destination, entry.Path);
                RejectLinks(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                FileSecurity? security = OperatingSystem.IsWindows() && File.Exists(target)
                    ? new FileInfo(target).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group) : null;
                string? previous = null;
                if (File.Exists(target))
                {
                    previous = SafeChild(backup, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                    File.Move(target, previous);
                }
                applied.Add((target, previous));
                File.Move(SafeChild(incoming, entry.Path), target);
                if (OperatingSystem.IsWindows()) PreserveWindowsSecurity(target, security);
            }
            if (verify is not null) await verify().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (activate is not null) await activate().ConfigureAwait(false);
        }
        catch (Exception original)
        {
            List<Exception> failures = [original];
            foreach ((string target, string? previous) in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (previous is not null) File.Move(previous, target);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rollbackSucceeded = false; failures.Add(error); }
            }
            if (!rollbackSucceeded) throw new AggregateException("更新失败；原文件备份仍保留在安装目录，请勿删除。", failures);
            throw;
        }
        finally { if (rollbackSucceeded) TryDeleteDirectory(transaction); }
    }

    [SupportedOSPlatform("windows")]
    private static void PreserveWindowsSecurity(string target, FileSecurity? original)
    {
        FileInfo file = new(target);
        if (original is not null) file.SetAccessControl(original);
        else
        {
            // Moving from a private staging directory retains its inherited ACEs.
            // Remove those and ask Windows to inherit from the real installation parent.
            FileSecurity security = file.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(true, false);
            security.SetAccessRuleProtection(false, false);
            file.SetAccessControl(security);
        }
    }

    internal static FileStream OpenInstallLock(string directory)
    {
        string path = Path.Combine(directory, ".drcom-update.lock");
        RejectLinks(path);
        FileStreamOptions options = new() { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(path, options); }
        catch (IOException) { throw new IOException("另一个更新正在进行，或安装文件仍被占用。请关闭其他客户端后重试。"); }
    }

    internal static async Task<List<UpdateFile>> DescribeTreeAsync(string root, CancellationToken cancellationToken)
    {
        List<UpdateFile> entries = [];
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file);
            ValidateGuiRelativePath(relative);
            entries.Add(await DescribeAsync(file, relative, cancellationToken).ConfigureAwait(false));
        }
        if (entries.Count is 0 or > 20000) throw new InvalidDataException("更新载荷文件数量无效。");
        return entries;
    }

    private static async Task<UpdateFile> DescribeAsync(string file, string relative, CancellationToken cancellationToken)
    {
        RejectLinks(file);
        return new(relative, new FileInfo(file).Length, await HashAsync(file, cancellationToken).ConfigureAwait(false),
            OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(file));
    }

    internal static async Task VerifyFileAsync(string file, UpdateFile entry, CancellationToken cancellationToken)
    {
        RejectLinks(file);
        if (new FileInfo(file).Length != entry.Length || await HashAsync(file, cancellationToken).ConfigureAwait(false) != entry.Sha256)
            throw new InvalidDataException("暂存更新文件已改变。");
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    internal static string SafeChild(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') && Path.DirectorySeparatorChar != '\\'
            || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "" or "." or ".." || part.Contains(':')))
            throw new InvalidDataException("更新计划包含无效文件路径。");
        string canonicalRoot = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(canonicalRoot)) canonicalRoot += Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(canonicalRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("更新文件离开了目标目录。");
        return path;
    }

    internal static void ValidateGuiRelativePath(string relative)
    {
        _ = SafeChild(Path.GetTempPath(), relative);
        if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(PrivateNames.Contains))
            throw new InvalidDataException("GUI 更新不能包含核心或账号配置。");
    }

    internal static void RejectLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    // Apple exposes these system directories through fixed aliases.
                    string? expected = OperatingSystem.IsMacOS() ? current switch { "/var" => "/private/var", "/tmp" => "/private/tmp", "/etc" => "/private/etc", _ => null } : null;
                    if (expected is null || new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName != expected)
                        throw new InvalidDataException("更新路径不能包含符号链接或重解析点。");
                }
            }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        RejectLinks(path);
        DirectoryInfo directory = Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User ?? throw new IOException("无法确定当前用户。");
            DirectorySecurity security = new();
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    internal static async Task CopyTreeAsync(string source, string target, CancellationToken cancellationToken)
    {
        RejectLinks(source);
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            RejectLinks(directory);
            Directory.CreateDirectory(SafeChild(target, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            RejectLinks(file);
            await CopyFileAsync(file, SafeChild(target, Path.GetRelativePath(source, file)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileAsync(string source, string target, CancellationToken cancellationToken)
    {
        await using (FileStream from = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
        await using (FileStream to = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal sealed record CommandResult(int ExitCode, string Output);
    internal static async Task<CommandResult> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using Process process = new() { StartInfo = new(executable)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new IOException("无法启动更新验证程序。");
            process.StandardInput.Close();
            Task<string> output = ReadLimitedAsync(process.StandardOutput, deadline.Token);
            Task<string> error = ReadLimitedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[4097];
        int used = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (count == 0) return new string(buffer, 0, used);
            used += count;
            if (used > 4096) throw new InvalidDataException("更新验证程序输出超过允许长度。");
        }
    }

    // statx has the same documented layout on the supported Linux architectures.
    internal sealed record UnixIdentity(uint User, uint Group, ulong Inode, uint DeviceMajor, uint DeviceMinor);
    internal static UnixIdentity? ReadIdentity(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        byte[] data = new byte[512];
        if (OperatingSystem.IsLinux())
        {
            if (Statx(-100, path, 0x100, 0x7ff, data) != 0) throw new IOException("无法读取核心文件所有权。", new Win32Exception(Marshal.GetLastPInvokeError()));
            return LinuxIdentity(data);
        }
        if (LStat(path, data) != 0) throw new IOException("无法读取核心文件所有权。");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(data), 0);
    }

    private static UnixIdentity LinuxIdentity(byte[] data) => new(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)),
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)), BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32)),
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(136)), BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(140)));

    internal static UnixIdentity ReadDescriptorIdentity(int descriptor)
    {
        byte[] data = new byte[256];
        if (Statx(descriptor, "", 0x1000, 0x7ff, data) != 0) throw new IOException("无法核对核心文件描述符。");
        return LinuxIdentity(data);
    }

    private static void PreserveOwner(string path, UnixIdentity expected)
    {
        UnixIdentity current = ReadIdentity(path)!;
        if ((current.User != expected.User || current.Group != expected.Group) && Chown(path, expected.User, expected.Group) != 0)
            throw new IOException("无法保留核心所有权，请由管理员更新。当前版本没有改变。");
    }

    internal static bool ReadRawCapability(string path)
    {
        byte[] value = new byte[64];
        nint length = GetXattr(path, "security.capability", value, (nuint)value.Length);
        if (length == -1)
        {
            if (Marshal.GetLastPInvokeError() is 61 or 95) return false;
            throw new IOException("无法读取核心的 Linux 文件权限。");
        }
        return ValidateRawCapability(value.AsSpan(0, checked((int)length)));
    }

    internal static bool ValidateRawCapability(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0) return false;
        uint flags = value.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(value) : 0;
        if ((value.Length != 20 || flags != 0x02000001) && (value.Length != 24 || flags != 0x03000001))
            throw new IOException("核心含有其他 Linux capabilities，请由管理员更新以保留原权限。");
        if (BinaryPrimitives.ReadUInt32LittleEndian(value[4..]) != 1u << 13 || value[8..20].IndexOfAnyExcept((byte)0) >= 0
            || value.Length == 24 && BinaryPrimitives.ReadUInt32LittleEndian(value[20..]) != 0)
            throw new IOException("核心含有其他 Linux capabilities，请由管理员更新以保留原权限。");
        return true;
    }

    [SupportedOSPlatform("linux")]
    private static async Task RestoreRawCapabilityAsync(string target, string expectedHash, CancellationToken cancellationToken)
    {
        const string pkexec = "/usr/bin/pkexec";
        string helper = Environment.ProcessPath ?? throw new IOException("无法定位授权程序。");
        if (!File.Exists(pkexec) || Path.GetFileName(helper) != "drcom4scutGUI") throw new IOException("保留有线网络权限需要 PolicyKit 和完整 GUI 安装；旧核心已保留。");
        string parent = Path.GetDirectoryName(target)!;
        if ((File.GetUnixFileMode(parent) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new IOException("核心目录允许其他用户写入，无法安全授权更新；请先修正目录权限。");
        await using FileStream held = new(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        int descriptor = held.SafeFileHandle.DangerousGetHandle().ToInt32();
        UnixIdentity identity = ReadDescriptorIdentity(descriptor);
        if (identity != ReadIdentity(target) || Convert.ToHexString(await SHA256.HashDataAsync(held, cancellationToken).ConfigureAwait(false)) != expectedHash)
            throw new InvalidDataException("授权前核心文件发生变化。");
        using Process current = Process.GetCurrentProcess();
        // libcap's setcap tool deliberately rejects /proc magic links. The dedicated
        // helper duplicates this descriptor and uses fsetxattr on the verified inode.
        CommandResult result = await RunAsync(pkexec, [helper, UpdateHelper.CapabilitySwitch,
            current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture), target, expectedHash],
            TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        held.Position = 0;
        byte[] capability = new byte[64];
        nint length = FGetXattr(descriptor, "security.capability", capability, (nuint)capability.Length);
        if (result.ExitCode != 0 || identity != ReadIdentity(target) || identity != ReadDescriptorIdentity(descriptor)
            || Convert.ToHexString(await SHA256.HashDataAsync(held, cancellationToken).ConfigureAwait(false)) != expectedHash
            || length < 0 || !ValidateRawCapability(capability.AsSpan(0, checked((int)length))))
            throw new IOException("系统授权未完成，更新已取消并恢复原核心。");
    }

    [SupportedOSPlatform("linux")]
    internal static async Task RestoreCapabilityInHelperAsync(string[] args)
    {
        if (args.Length != 6 || args[0] != UpdateHelper.CapabilitySwitch || GetEffectiveUserId() != 0
            || !uint.TryParse(Environment.GetEnvironmentVariable("PKEXEC_UID"), out uint callerUser) || callerUser == 0
            || !int.TryParse(args[1], out int callerId) || callerId <= 0 || GetParentProcessId() != callerId
            || !long.TryParse(args[2], out long started) || !int.TryParse(args[3], out int descriptor) || descriptor < 0
            || args[5].Length != 64 || !args[5].All(Uri.IsHexDigit))
            throw new InvalidDataException("系统授权请求身份无效。");
        using Process caller = Process.GetProcessById(callerId);
        if (caller.StartTime.ToUniversalTime().Ticks != started) throw new InvalidDataException("授权请求的客户端进程已改变。");
        string self = Environment.ProcessPath ?? throw new IOException("无法定位授权程序。");
        string? callerExecutable = new FileInfo($"/proc/{callerId}/exe").ResolveLinkTarget(true)?.FullName;
        if (callerExecutable != self || Path.GetFileName(self) != "drcom4scutGUI") throw new InvalidDataException("授权程序与客户端不一致。");
        string target = RequireExecutable(args[4], UpdateComponent.Core, UpdateService.NativeRuntime);
        if (target != Path.Combine(Path.GetDirectoryName(self)!, "drcom4scut")) throw new InvalidDataException("授权仅限本客户端旁的核心。");
        UnixIdentity parent = ReadIdentity(Path.GetDirectoryName(target)!)!;
        if (parent.User != callerUser || (File.GetUnixFileMode(Path.GetDirectoryName(target)!) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new InvalidDataException("核心安装目录所有权不安全。");
        await using FileStream held = new($"/proc/{callerId}/fd/{descriptor}", FileMode.Open, FileAccess.Read, FileShare.Read);
        int ownDescriptor = held.SafeFileHandle.DangerousGetHandle().ToInt32();
        UnixIdentity identity = ReadDescriptorIdentity(ownDescriptor);
        if (identity.User != callerUser || identity != ReadIdentity(target)
            || (File.GetUnixFileMode(target) & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0
            || Convert.ToHexString(await SHA256.HashDataAsync(held).ConfigureAwait(false)) != args[5])
            throw new InvalidDataException("授权核心的所有权、文件或校验值发生变化。");
        await UpdateArchive.VerifyExecutableAsync($"/proc/{callerId}/fd/{descriptor}", UpdateService.NativeRuntime, true, CancellationToken.None).ConfigureAwait(false);
        byte[] capability = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(capability, 0x02000001);
        BinaryPrimitives.WriteUInt32LittleEndian(capability.AsSpan(4), 1u << 13);
        if (FSetXattr(ownDescriptor, "security.capability", capability, (nuint)capability.Length, 0) != 0
            || identity != ReadIdentity(target) || identity != ReadDescriptorIdentity(ownDescriptor))
            throw new IOException("系统无法恢复核心的有线网络权限。");
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, [Out] byte[] data);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, [Out] byte[] data);
    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);
    [DllImport("libc", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr(string path, string name, [Out] byte[] value, nuint size);
    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)]
    private static extern nint FGetXattr(int descriptor, string name, [Out] byte[] value, nuint size);
    [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)]
    private static extern int FSetXattr(int descriptor, string name, byte[] value, nuint size, int flags);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId();
}

internal sealed record UpdateFile(string Path, long Length, string Sha256, int Mode);
internal sealed class UpdatePlan
{
    public int Schema { get; init; } = 1;
    public UpdateRelease Release { get; init; } = null!;
    public string Nonce { get; init; } = "";
    public int ParentId { get; init; }
    public long ParentStartUtcTicks { get; init; }
    public long CreatedUtcTicks { get; init; }
    public string TargetExecutable { get; init; } = "";
    public string TargetSha256 { get; init; } = "";
    public List<UpdateFile> Files { get; init; } = [];
}
