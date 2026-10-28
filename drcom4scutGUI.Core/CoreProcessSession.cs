using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

public sealed record CoreLaunchOptions(string Username, [property: JsonIgnore] string Password, string Mac = "", string IP = "", string ConfigPath = "", bool NoUdp = false, string Mode = "eap", string PortalBase = "", string PortalApi = "", string PortalAcIp = "", string PortalClientIp = "", bool AllowHttp = false)
{
    public override string ToString() => $"CoreLaunchOptions {{ Username = {Username}, Password = [redacted] }}";
}

/// <summary>A completed environment check can report unmet prerequisites.</summary>
public sealed record CoreDoctorReport(string Report, bool PrerequisitesMet);

/// <summary>
/// Owns one child only. Passwords travel through its private redirected stdin,
/// never through the process argument vector or environment variables.
/// </summary>
public sealed class CoreProcessSession(string executable)
{
    public static ProcessStartInfo CreateStartInfo(string executable, CoreLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("密码不能包含换行或 NUL 字符。", nameof(options));
        PortalOptions.ValidateLaunch(options);
        ProcessStartInfo info = CreateBaseStartInfo(executable);
        info.RedirectStandardInput = true;
        info.StandardInputEncoding = new UTF8Encoding(false);
        info.ArgumentList.Add("--password-stdin");
        info.ArgumentList.Add("--username");
        info.ArgumentList.Add(options.Username);
        AddOption(info, "--mac", options.Mac);
        AddOption(info, "--ip", options.IP);
        AddOption(info, "--config", options.ConfigPath);
        AddOption(info, "--mode", options.Mode);
        if (options.Mode == "portal")
        {
            AddOption(info, "--portal-base", options.PortalBase);
            AddOption(info, "--portal-api", options.PortalApi);
            AddOption(info, "--portal-ac-ip", options.PortalAcIp);
            AddOption(info, "--portal-client-ip", options.PortalClientIp);
            info.ArgumentList.Add(options.AllowHttp ? "--allow-insecure-portal" : "--require-https-portal");
        }
        else info.ArgumentList.Add(options.NoUdp ? "--no-udp" : "--with-udp");
        return info;
    }

    public async Task<int> RunAsync(CoreLaunchOptions options, Action<string> stdout, Action<string> stderr, CancellationToken cancellationToken,
        Action<CoreConnectionStatus>? connectionChanged = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo start = CreateStartInfo(executable, options);
        CredentialRedactor redactor = new(options.Username, options.Password);
        using PrivateStatusDirectory statusDirectory = new();
        start.ArgumentList.Add("--status-file");
        start.ArgumentList.Add(statusDirectory.StatusPath);
        using CancellationTokenSource statusLifetime = new();
        await using PrivateControlServer control = new(start, statusDirectory.DirectoryPath);
        using Process process = new() { StartInfo = start };
        Start(process);
        // The parent retains this private connection for the entire session. Its OS
        // handle closes on GUI crash, so the core can log off instead of orphaning.
        Task output = CoreOutputReader.ReadLinesAsync(process.StandardOutput, stdout, redactor);
        Task error = CoreOutputReader.ReadLinesAsync(process.StandardError, stderr, redactor);
        Task status = MonitorStatusAsync(statusDirectory.StatusPath, process.Id, options, connectionChanged, statusLifetime.Token);
        using CancellationTokenSource inputLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task input = WritePasswordAsync(process, options.Password, inputLifetime.Token);
        try
        {
            Task exit = process.WaitForExitAsync(cancellationToken);
            List<Task> pending = [exit, input, output, error, status];
            while (pending.Count > 0)
            {
                // A failed diagnostic/status consumer must stop its owned core,
                // including while that core has not yet read the private input.
                Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                if (completed == exit) break;
                pending.Remove(completed);
            }
            await input.ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            inputLifetime.Cancel();
            // StreamWriter.Close may flush a buffered tail to an already dead
            // child. That cleanup failure must never skip stopping the child,
            // cancelling/awaiting its status reader, or removing private files.
            CloseStandardInput(process);
            try
            {
                await StopOwnedGracefullyAsync(process, control).ConfigureAwait(false);
            }
            finally
            {
                statusLifetime.Cancel();
                // Observe every reader even if another reader/callback failed.
                // WhenAll waits for all tasks and still propagates real faults.
                try { await Task.WhenAll(input, status, output, error).ConfigureAwait(false); }
                catch (OperationCanceledException) when (inputLifetime.IsCancellationRequested && statusLifetime.IsCancellationRequested) { }
            }
        }
    }

    private static async Task WritePasswordAsync(Process process, string password, CancellationToken cancellationToken)
    {
        try
        {
            process.StandardInput.NewLine = "\n";
            await process.StandardInput.WriteLineAsync(password.AsMemory(), cancellationToken).ConfigureAwait(false);
            // Flush the final buffered characters with cancellation before the
            // synchronous Close sends EOF; it must not block on a full pipe.
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("核心在接收密码前已关闭输入，请检查核心版本、启动日志和运行权限。");
        }
    }

    private static void CloseStandardInput(Process process)
    {
        try
        {
            // This is cancellation/error cleanup (successful input already sent
            // EOF). Close the pipe first so a buffered writer tail cannot block
            // before the owned child is asked to stop.
            process.StandardInput.BaseStream.Close();
            process.StandardInput.Close();
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException) { }
    }

    private static async Task StopOwnedGracefullyAsync(Process process, PrivateControlServer control)
    {
        if (process.HasExited) return;
        // A single budget covers connection, STOP delivery and cooperative exit.
        // A non-cooperating child is killed only after the grace period expires.
        // The core has a three-second shutdown budget, including EAP Logoff and
        // offline hooks. Allow it to finish before considering a forced stop.
        using CancellationTokenSource grace = new(TimeSpan.FromSeconds(4));
        try
        {
            await control.SendStopAsync(grace.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidOperationException) { }
        KillOwned(process);
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    /// <summary>Refuse old cores instead of falling back to plaintext arguments.</summary>
    public async Task<string> VerifyCoreAsync(CancellationToken cancellationToken)
    {
        string help = (await ProbeAsync("--help", cancellationToken).ConfigureAwait(false)).Output;
        if (!help.Contains("--password-stdin", StringComparison.Ordinal) || !help.Contains(OperatingSystem.IsWindows() ? "--control-pipe" : "--control-socket", StringComparison.Ordinal) || !help.Contains("--status-file", StringComparison.Ordinal) || !help.Contains("--with-udp", StringComparison.Ordinal) || !help.Contains("--require-https-portal", StringComparison.Ordinal))
            throw new InvalidOperationException("核心不支持所需的安全密码输入、连接状态和生命周期控制。请将 drcom4scut 0.5.0 或更新版本放在 GUI 同目录，不会回退到命令行明文密码。");
        string reported = (await ProbeAsync("--version", cancellationToken).ConfigureAwait(false)).Output.Trim();
        Match release = Regex.Match(reported,
            @"\Adrcom4scut (?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
            RegexOptions.CultureInvariant);
        if (!release.Success || !Version.TryParse(release.Groups["version"].Value, out Version? version) || version < new Version(0, 5, 0))
            throw new InvalidOperationException("请使用 drcom4scut 0.5.0 或更新的正式版本；当前核心版本过旧或无法识别，认证尚未启动。");
        return reported;
    }

    public async Task<CoreDoctorReport> DoctorAsync(CancellationToken cancellationToken)
    {
        ProbeResult result = await ProbeAsync("--doctor", cancellationToken).ConfigureAwait(false);
        string report = result.Output.TrimEnd();
        if (result.Error.Length > 0)
            report += (report.Length > 0 ? Environment.NewLine : "") + result.Error.TrimEnd();
        return new CoreDoctorReport(report, result.ExitCode == 0);
    }

    private sealed record ProbeResult(int ExitCode, string Output, string Error);

    private async Task<ProbeResult> ProbeAsync(string argument, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using Process process = new() { StartInfo = CreateBaseStartInfo(executable) };
        process.StartInfo.ArgumentList.Add(argument);
        Start(process);
        using CancellationTokenRegistration registration = deadline.Token.Register(() => KillOwned(process));
        Task<string> output = CoreOutputReader.ReadProbeAsync(process.StandardOutput, deadline.Token);
        Task<string> error = CoreOutputReader.ReadProbeAsync(process.StandardError, deadline.Token);
        try
        {
            // Observe output failures while the child is still running. A core
            // that fills a pipe after exceeding the output budget must not
            // hold the environment check open until its normal timeout.
            List<Task> pending = [process.WaitForExitAsync(deadline.Token), output, error];
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            string text = await output.ConfigureAwait(false);
            string errors = await error.ConfigureAwait(false);
            if (process.ExitCode != 0 && !(argument == "--doctor" && process.ExitCode == 1))
                throw new InvalidOperationException("核心自检未成功，请检查版本和网卡权限；Windows 有线模式还需要 Npcap。" + (argument == "--doctor" ? "\n" + errors : ""));
            return new ProbeResult(process.ExitCode, text, errors);
        }
        finally
        {
            KillOwned(process);
            deadline.Cancel();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    private static ProcessStartInfo CreateBaseStartInfo(string executable) => new(Path.GetFullPath(executable))
    {
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = new UTF8Encoding(false),
        StandardErrorEncoding = new UTF8Encoding(false)
    };

    private static void AddOption(ProcessStartInfo info, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }

    private static void Start(Process process)
    {
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动核心程序。");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("无法启动安装目录中的 drcom4scut 核心。请安装对应系统和 CPU 的 0.5.0 或更新版本，并检查文件执行权限；Windows 有线模式还需要 Npcap。");
        }
    }

    private static async Task MonitorStatusAsync(string path, int pid, CoreLaunchOptions options,
        Action<CoreConnectionStatus>? changed, CancellationToken cancellationToken)
    {
        CoreConnectionStatus? previous = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CoreConnectionStatus? current = await ReadStatusAsync(path, pid, options, cancellationToken).ConfigureAwait(false);
            if (current is not null && current != previous)
            {
                previous = current;
                changed?.Invoke(current);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<CoreConnectionStatus?> ReadStatusAsync(string path, int pid, CoreLaunchOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is 0 or > AuthenticationStatus.MaximumStatusBytes) return null;
            byte[] bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (await file.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0) return null;
            return AuthenticationStatus.Parse(bytes, pid, options.Mode, options.NoUdp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Atomic publication may briefly leave no file. A malformed or
            // foreign session must never become an authentication success.
            return null;
        }
    }

    private sealed class PrivateStatusDirectory : IDisposable
    {
        private readonly DirectoryInfo directory;
        public string DirectoryPath => directory.FullName;
        public string StatusPath => Path.Combine(directory.FullName, "status.json");

        public PrivateStatusDirectory()
        {
            // macOS sockaddr_un allows only 103 path bytes. Its default TMPDIR
            // can already exceed that budget, so keep the private endpoint short.
            directory = OperatingSystem.IsWindows()
                ? Directory.CreateTempSubdirectory("drcom4scut-gui-")
                : Directory.CreateDirectory(Path.Combine("/tmp", "d4s-" + Guid.NewGuid().ToString("N")[..24]),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                    SecurityIdentifier user = identity.User
                        ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
                    DirectorySecurity security = new();
                    security.SetOwner(user);
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                    directory.SetAccessControl(security);
                }
                else File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                directory.Delete(recursive: true);
                throw;
            }
        }

        public void Dispose()
        {
            try { directory.Delete(recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
