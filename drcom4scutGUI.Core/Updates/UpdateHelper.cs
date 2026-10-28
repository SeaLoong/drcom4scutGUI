using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

/// <summary>A fixed-purpose native helper entry point. Invoke before initializing the desktop framework.</summary>
public static class UpdateHelper
{
    internal const string Switch = "--apply-update";
    internal const string CapabilitySwitch = "--restore-core-capability";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Contains(CapabilitySwitch, StringComparer.Ordinal))
        {
            try
            {
                if (!OperatingSystem.IsLinux()) throw new InvalidDataException("此系统不使用 Linux 文件权限。");
                UpdateInstaller.RestoreCapabilityInHelperAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                or ArgumentException or System.ComponentModel.Win32Exception)
            { Console.Error.WriteLine("核心网络权限授权失败，客户端将恢复原版本。"); exitCode = 1; }
            return true;
        }
        if (!args.Contains(Switch, StringComparer.Ordinal))
        {
            CleanupCompleted();
            return false;
        }
        if (args.Length != 3 || args[0] != Switch) { exitCode = 2; return true; }
        try { RunAsync(args[1], args[2]).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException or AggregateException or OverflowException)
        {
            Console.Error.WriteLine("更新未完成，原版本和账号配置已保留。请重新打开客户端后重试。");
            exitCode = 1;
        }
        return true;
    }

    private static async Task RunAsync(string planPath, string nonce)
    {
        string directory = ValidatePlanPath(planPath);
        UpdatePlan plan = ReadPlan(planPath);
        ValidatePlan(plan, nonce);
        string ownPayload = UpdateInstaller.GuiPayloadPath(Path.Combine(directory, "helper"), plan.Release.Runtime);
        if (!SamePath(Environment.ProcessPath ?? "", ownPayload)) throw new InvalidDataException("更新程序不是独立暂存副本。");
        string source = Path.Combine(directory, "new");
        await VerifyPayloadAsync(source, plan, CancellationToken.None).ConfigureAwait(false);
        await UpdateArchive.VerifyAsync(source, plan.Release, CancellationToken.None).ConfigureAwait(false);
        Process? parent = null;
        bool parentExited = false;
        try
        {
            parent = Process.GetProcessById(plan.ParentId);
            ValidateParentIdentity(parent.Id, parent.StartTime.ToUniversalTime().Ticks, plan);
            string expectedProcess = plan.Release.Runtime.StartsWith("osx-", StringComparison.Ordinal)
                ? Path.Combine(UpdateInstaller.MacBundle(plan.TargetExecutable), "Contents", "Resources", "publish", "drcom4scutGUI")
                : plan.TargetExecutable;
            if (!SamePath(parent.MainModule?.FileName ?? "", expectedProcess)) throw new InvalidDataException("更新等待目标不是原客户端。");
            if (await UpdateInstaller.HashAsync(plan.TargetExecutable, CancellationToken.None).ConfigureAwait(false) != plan.TargetSha256)
                throw new InvalidDataException("安装目标已改变。");
            WriteStatus(directory, "ready");
            using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            parentExited = true;
            // Never kill a reused PID, and never resume the former command line.
            if (await UpdateInstaller.HashAsync(plan.TargetExecutable, CancellationToken.None).ConfigureAwait(false) != plan.TargetSha256)
                throw new InvalidDataException("等待退出时安装目标已改变。");
            await VerifyPayloadAsync(source, plan, CancellationToken.None).ConfigureAwait(false);
            await ApplyGuiUpdateAsync(source, plan, () => RestartAsync(plan.TargetExecutable)).ConfigureAwait(false);
            WriteStatus(directory, "complete");
        }
        catch
        {
            WriteStatus(directory, "failed");
            if (parentExited)
            {
                // A failed transaction restores the old executable. Restart it without
                // account names, passwords, connection flags or any inherited arguments.
                try { await RestartAsync(plan.TargetExecutable).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            throw;
        }
        finally
        {
            parent?.Dispose();
            // Windows keeps the helper's own runtime files open. A later normal startup
            // removes finished private stages after their helper has exited.
            if (!OperatingSystem.IsWindows() && File.Exists(Path.Combine(directory, "complete"))) UpdateInstaller.TryDeleteDirectory(directory);
        }
    }

    internal static string ValidatePlanPath(string planPath, string? allowedRoot = null)
    {
        if (!Path.IsPathFullyQualified(planPath) || Path.GetFullPath(planPath) != planPath || Path.GetFileName(planPath) != "plan.json")
            throw new InvalidDataException("更新计划路径无效。");
        string directory = Path.GetDirectoryName(planPath)!;
        string root = allowedRoot ?? UpdateInstaller.UpdatesRoot;
        if (!SamePath(Path.GetDirectoryName(directory) ?? "", root)
            || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), @"^update-[0-9a-f]{32}$"))
            throw new InvalidDataException("更新计划不在私有暂存目录中。");
        UpdateInstaller.RejectLinks(planPath);
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidDataException("更新计划目录不属于当前用户的私有空间。");
        return directory;
    }

    internal static UpdatePlan ReadPlan(string path)
    {
        if (new FileInfo(path).Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException("更新计划大小无效。");
        return JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("更新计划为空。");
    }

    internal static void ValidatePlan(UpdatePlan plan, string nonce)
    {
        if (plan is null || plan.Release is null || plan.Files is null || plan.Files.Any(file => file is null))
            throw new InvalidDataException("更新计划缺少必需字段。");
        UpdateService.ValidateRelease(plan.Release);
        if (plan.Schema != 1 || plan.Release.Component != UpdateComponent.Gui || plan.Release.Runtime != UpdateService.NativeRuntime
            || plan.ParentId <= 0 || plan.ParentId == Environment.ProcessId || plan.ParentStartUtcTicks <= 0
            || plan.CreatedUtcTicks < DateTime.UtcNow.AddMinutes(-10).Ticks || plan.CreatedUtcTicks > DateTime.UtcNow.AddMinutes(1).Ticks
            || !IsHash(plan.TargetSha256) || !IsHash(plan.Nonce) || !IsHash(nonce)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(plan.Nonce), Convert.FromHexString(nonce))
            || plan.Files.Count is 0 or > 20000)
            throw new InvalidDataException("更新计划身份、时间或组件无效。");
        _ = UpdateInstaller.RequireExecutable(plan.TargetExecutable, UpdateComponent.Gui, plan.Release.Runtime);
        if (plan.Files.Select(file => file.Path).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != plan.Files.Count)
            throw new InvalidDataException("更新计划包含重复文件。");
        long bytes = 0;
        foreach (UpdateFile file in plan.Files)
        {
            UpdateInstaller.ValidateGuiRelativePath(file.Path);
            if (file.Length < 0 || file.Length > UpdateService.MaximumArchiveBytes || !IsHash(file.Sha256)
                || (file.Mode & ~0x1ff) != 0) throw new InvalidDataException("更新文件身份或权限无效。");
            if (file.Length > 3L * UpdateService.MaximumArchiveBytes - bytes) throw new InvalidDataException("更新载荷超过允许大小。");
            bytes += file.Length;
        }
        if (bytes > 3L * UpdateService.MaximumArchiveBytes) throw new InvalidDataException("更新载荷超过允许大小。");
        string executable = Path.GetRelativePath(".", UpdateInstaller.GuiLaunchPath(".", plan.Release.Runtime));
        if (!plan.Files.Any(file => file.Path == executable)) throw new InvalidDataException("更新计划缺少原生启动程序。");
    }

    private static bool IsHash(string value) => value is not null && value.Length == 64 && value.All(Uri.IsHexDigit);
    internal static void ValidateParentIdentity(int actualId, long actualStartTicks, UpdatePlan plan)
    {
        if (actualId != plan.ParentId || actualStartTicks != plan.ParentStartUtcTicks)
            throw new InvalidDataException("原客户端进程已改变，拒绝等待或结束其他进程。");
    }

    internal static async Task VerifyPayloadAsync(string source, UpdatePlan plan, CancellationToken cancellationToken, bool installed = false)
    {
        if (!installed)
        {
            string[] actual = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(source, path)).Order(StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(plan.Files.Select(file => file.Path).Order(StringComparer.Ordinal))) throw new InvalidDataException("暂存目录包含未校验的文件。");
        }
        foreach (UpdateFile entry in plan.Files)
            await UpdateInstaller.VerifyFileAsync(UpdateInstaller.SafeChild(source, entry.Path), entry, cancellationToken).ConfigureAwait(false);
        // Existing installations can contain an adjacent core and private configuration;
        // the locally hashed plan defines only the GUI files this transaction may touch.
    }

    internal static async Task ApplyGuiUpdateAsync(string source, UpdatePlan plan, Func<Task> activate,
        Func<string, CancellationToken, Task<string>>? versionProbe = null)
    {
        // A core updater can replace and then roll back its executable. Probe
        // compatibility only while holding the shared installation lock.
        Task BeforeApply() => UpdateInstaller.ValidateGuiInstallAsync(new PreparedUpdate(plan.Release, source, source),
            plan.TargetExecutable, versionProbe, CancellationToken.None);
        if (plan.Release.Runtime.StartsWith("osx-", StringComparison.Ordinal))
            await ApplyMacAppAsync(source, plan.TargetExecutable, activate, BeforeApply).ConfigureAwait(false);
        else
            await UpdateInstaller.ApplyFilesAsync(source, Path.GetDirectoryName(plan.TargetExecutable)!, plan.Files,
                () => VerifyPayloadAsync(Path.GetDirectoryName(plan.TargetExecutable)!, plan, CancellationToken.None, installed: true),
                activate, CancellationToken.None, beforeApply: BeforeApply).ConfigureAwait(false);
    }

    internal static async Task ApplyMacAppAsync(string source, string nativeExecutable, Func<Task> activate, Func<Task>? beforeApply = null)
    {
        string target = UpdateInstaller.MacBundle(nativeExecutable);
        string parent = Path.GetDirectoryName(target)!;
        using FileStream gate = UpdateInstaller.OpenInstallLock(parent);
        if (beforeApply is not null) await beforeApply().ConfigureAwait(false);
        string incoming = Path.Combine(parent, ".drcom-gui-new-" + Guid.NewGuid().ToString("N") + ".app");
        string backup = Path.Combine(parent, ".drcom-gui-old-" + Guid.NewGuid().ToString("N") + ".app");
        bool movedOriginal = false, installed = false, rollbackSucceeded = true;
        try
        {
            await UpdateInstaller.CopyTreeAsync(Path.Combine(source, "drcom4scutGUI.app"), incoming, CancellationToken.None).ConfigureAwait(false);
            await VerifyMacSignatureAsync(incoming).ConfigureAwait(false);
            Directory.Move(target, backup);
            movedOriginal = true;
            Directory.Move(incoming, target);
            installed = true;
            await VerifyMacSignatureAsync(target).ConfigureAwait(false);
            await activate().ConfigureAwait(false);
        }
        catch (Exception original)
        {
            try
            {
                if (installed) Directory.Delete(target, true);
                if (movedOriginal) Directory.Move(backup, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                rollbackSucceeded = false;
                throw new AggregateException("应用程序更新失败，原应用备份保留在安装目录。", original, error);
            }
            throw;
        }
        finally
        {
            UpdateInstaller.TryDeleteDirectory(incoming);
            if (rollbackSucceeded) UpdateInstaller.TryDeleteDirectory(backup);
        }
    }

    private static async Task VerifyMacSignatureAsync(string bundle)
    {
        if (!OperatingSystem.IsMacOS()) return;
        UpdateInstaller.CommandResult result = await UpdateInstaller.RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", bundle],
            TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidDataException("应用程序签名校验失败。");
    }

    private static async Task RestartAsync(string executable)
    {
        using Process restarted = Process.Start(UpdateInstaller.RestartStartInfo(executable)) ?? throw new IOException("无法重新打开客户端。");
        await Task.Delay(1000).ConfigureAwait(false);
        if (restarted.HasExited) throw new IOException("更新后的客户端未能启动，正在恢复原版本。");
    }

    private static void WriteStatus(string directory, string name)
    {
        try
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { if (name == "ready") throw; }
    }

    internal static void CleanupCompleted(string? allowedRoot = null)
    {
        try
        {
            string root = allowedRoot ?? UpdateInstaller.UpdatesRoot;
            if (!Directory.Exists(root)) return;
            UpdateInstaller.RejectLinks(root);
            foreach (string directory in Directory.EnumerateDirectories(root, "update-*"))
            {
                try
                {
                    string marker = Path.Combine(directory, "complete");
                    if (!File.Exists(marker)) marker = Path.Combine(directory, "failed");
                    if (!File.Exists(marker) || DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromMinutes(5)) continue;
                    _ = ValidatePlanPath(Path.Combine(directory, "plan.json"), root);
                    UpdateInstaller.RejectLinks(marker);
                    if (new FileInfo(marker).Length is <= 0 or > 32
                        || !int.TryParse(File.ReadAllText(marker), out int id) || id <= 0) continue;
                    try { using Process helper = Process.GetProcessById(id); if (!helper.HasExited) continue; }
                    catch (ArgumentException) { }
                    // Installation rollback backups live outside this private stage.
                    // Keep recent failures for diagnostics and never remove a live helper.
                    UpdateInstaller.TryDeleteDirectory(directory);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
                    or System.ComponentModel.Win32Exception) { }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception) { }
    }

    private static bool SamePath(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
