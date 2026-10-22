using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

public sealed record CoreLaunchOptions(string Username, [property: JsonIgnore] string Password, string Mac = "", string IP = "", string ConfigPath = "", bool NoUdp = false, string Mode = "eap", string PortalBase = "", string PortalApi = "", string PortalAcIp = "", string PortalClientIp = "", bool AllowHttp = false)
{
    public override string ToString() => $"CoreLaunchOptions {{ Username = {Username}, Password = [redacted] }}";
}

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

    public async Task<int> RunAsync(CoreLaunchOptions options, Action<string> stdout, Action<string> stderr, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo start = CreateStartInfo(executable, options);
        string pipeName = "drcom4scut-" + Guid.NewGuid().ToString("N");
        start.ArgumentList.Add("--control-pipe");
        start.ArgumentList.Add(@"\\.\pipe\" + pipeName);
        using CancellationTokenSource controlLifetime = new();
        await using NamedPipeServerStream control = new(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using Process process = new() { StartInfo = start };
        Start(process);
        Task connection = control.WaitForConnectionAsync(controlLifetime.Token);
        // The parent retains this private pipe for the entire session. Its OS
        // handle closes on GUI crash, so the core can log off instead of orphaning.
        Task output = DrainAsync(process.StandardOutput, stdout, options.Password);
        Task error = DrainAsync(process.StandardError, stderr, options.Password);
        try
        {
            process.StandardInput.NewLine = "\n";
            await process.StandardInput.WriteLineAsync(options.Password.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            process.StandardInput.Close();
            try
            {
                await StopOwnedGracefullyAsync(process, control, connection).ConfigureAwait(false);
            }
            finally
            {
                controlLifetime.Cancel();
                try { await connection.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
        }
    }

    private static async Task StopOwnedGracefullyAsync(Process process, NamedPipeServerStream control, Task connection)
    {
        if (process.HasExited) return;
        // A single budget covers connection, STOP delivery and cooperative exit.
        // A non-cooperating child is killed only after the grace period expires.
        using CancellationTokenSource grace = new(TimeSpan.FromSeconds(2));
        try
        {
            await connection.WaitAsync(grace.Token).ConfigureAwait(false);
            await control.WriteAsync("stop\n"u8.ToArray(), grace.Token).ConfigureAwait(false);
            await control.FlushAsync(grace.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            return;
        }
        catch (Exception failure) when (failure is OperationCanceledException or IOException or InvalidOperationException) { }
        KillOwned(process);
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
    }

    /// <summary>Refuse old cores instead of falling back to plaintext arguments.</summary>
    public async Task<string> VerifyCoreAsync(CancellationToken cancellationToken)
    {
        string help = await ProbeAsync("--help", cancellationToken).ConfigureAwait(false);
        if (!help.Contains("--password-stdin", StringComparison.Ordinal) || !help.Contains("--control-pipe", StringComparison.Ordinal) || !help.Contains("--with-udp", StringComparison.Ordinal) || !help.Contains("--require-https-portal", StringComparison.Ordinal))
            throw new InvalidOperationException("核心不支持所需的安全密码输入和生命周期控制。请将 drcom4scut 0.4.0 或更新版本放在 GUI 同目录，不会回退到命令行明文密码。");
        return (await ProbeAsync("--version", cancellationToken).ConfigureAwait(false)).Trim();
    }

    public Task<string> DoctorAsync(CancellationToken cancellationToken) => ProbeAsync("--doctor", cancellationToken);

    private async Task<string> ProbeAsync(string argument, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using Process process = new() { StartInfo = CreateBaseStartInfo(executable) };
        process.StartInfo.ArgumentList.Add(argument);
        Start(process);
        using CancellationTokenRegistration registration = deadline.Token.Register(() => KillOwned(process));
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false);
            string errors = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("核心自检未成功，请检查版本、Npcap 安装和网卡权限。" + (argument == "--doctor" ? "\n" + errors : ""));
            return text;
        }
        finally
        {
            KillOwned(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
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
            throw new InvalidOperationException("无法启动同目录的 drcom4scut.exe。请从可信的发布来源安装 0.4.0 或更新版本，并安装 Npcap。");
        }
    }

    private static async Task DrainAsync(StreamReader stream, Action<string> onLine, string password)
    {
        while (await stream.ReadLineAsync().ConfigureAwait(false) is string line)
        {
            // Even a misconfigured external core must not display the submitted
            // password in the GUI log or exported diagnostics.
            onLine(password.Length == 0 ? line : line.Replace(password, "[已隐藏]", StringComparison.Ordinal));
        }
    }

    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
