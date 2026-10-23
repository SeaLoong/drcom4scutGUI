using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class CoreProcessSessionTests
{
    private static string Helper => System.IO.Path.Combine(AppContext.BaseDirectory, "helpers", OperatingSystem.IsWindows() ? "FakeCore.exe" : "FakeCore");

    [Fact]
    public async Task OversizedProbeOutputStopsTheChildWithoutWaitingForItsNormalExit()
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Helper)!, "DoctorOversized" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(Helper, fixture);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new CoreProcessSession(fixture)
                .DoctorAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken));
        }
        finally { File.Delete(fixture); }
    }

    [Fact]
    public async Task GiantChildLinesDoNotLeakPartialPasswordsOrPreventFollowingDiagnostics()
    {
        var output = new ConcurrentQueue<string>();
        var error = new ConcurrentQueue<string>();
        int code = await new CoreProcessSession(Helper).RunAsync(new("oversized-output", "synthetic-private-password"),
            output.Enqueue, error.Enqueue, TestContext.Current.CancellationToken);
        Assert.Equal(17, code);
        Assert.Contains("following-diagnostic", output);
        Assert.Contains("[核心日志过长，已省略]", output);
        Assert.Contains("[核心日志过长，已省略]", error);
        Assert.DoesNotContain("synthetic-private", string.Join('\n', output));
        Assert.All(output.Concat(error), line => Assert.True(line.Length <= CoreOutputReader.MaximumLineCharacters));
    }

    [Fact]
    public async Task CapabilityProbeAndDoctorNeedNoStoredCredential()
    {
        var session = new CoreProcessSession(Helper);
        Assert.Equal("drcom4scut 0.5.0", await session.VerifyCoreAsync(TestContext.Current.CancellationToken));
        CoreDoctorReport report = await session.DoctorAsync(TestContext.Current.CancellationToken);
        Assert.True(report.PrerequisitesMet);
        Assert.Contains("no real capture opened", report.Report);
    }

    [Fact]
    public async Task UnmetPrerequisitesPreserveBothDiagnosticStreams()
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Helper)!, "DoctorUnavailable" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(Helper, fixture);
        try
        {
            CoreDoctorReport report = await new CoreProcessSession(fixture).DoctorAsync(TestContext.Current.CancellationToken);
            Assert.False(report.PrerequisitesMet);
            Assert.Contains("capture=unavailable", report.Report);
            Assert.Contains("install capture support", report.Report);
        }
        finally { File.Delete(fixture); }
    }

    [Fact]
    public async Task UnexpectedDiagnosticExitStillReportsFailure()
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Helper)!, "DoctorFailure" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(Helper, fixture);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProcessSession(fixture).DoctorAsync(TestContext.Current.CancellationToken));
            Assert.Contains("unexpected diagnostic failure", error.Message);
        }
        finally { File.Delete(fixture); }
    }

    [Fact]
    public async Task LegacyCoreIsRefusedInsteadOfFallingBackToPasswordArguments()
    {
        string renamed = Path.Combine(Path.GetDirectoryName(Helper)!, "Legacy" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(Helper, renamed);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProcessSession(renamed).VerifyCoreAsync(TestContext.Current.CancellationToken));
            Assert.Contains("0.5.0", error.Message);
            Assert.Contains("不会回退", error.Message);
        }
        finally { File.Delete(renamed); }
    }

    [Theory]
    [InlineData("OldCore")]
    [InlineData("PreviewCore")]
    [InlineData("UnknownCore")]
    public async Task CapabilityNamesAloneCannotOverrideTheSupportedReleaseVersion(string prefix)
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Helper)!, prefix + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(Helper, fixture);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProcessSession(fixture).VerifyCoreAsync(TestContext.Current.CancellationToken));
            Assert.Contains("0.5.0", error.Message);
        }
        finally { File.Delete(fixture); }
    }

    [Fact]
    public async Task SupportedNewerReleaseCanCarrySemverBuildMetadata()
    {
        string fixture = Path.Combine(Path.GetDirectoryName(Helper)!, "FutureCore" + Guid.NewGuid().ToString("N") + (OperatingSystem.IsWindows() ? ".exe" : ""));
        File.Copy(Helper, fixture);
        try
        {
            Assert.Equal("drcom4scut 1.2.3+release.build", await new CoreProcessSession(fixture).VerifyCoreAsync(TestContext.Current.CancellationToken));
        }
        finally { File.Delete(fixture); }
    }

    [Fact]
    public void ProcessArgumentsKeepEachValueSeparateAndNeverContainPassword()
    {
        var options = new CoreLaunchOptions("user \" --password bad", "synthetic-secret", "02:00:00:00:00:01", "192.0.2.1", "configuration with spaces.yml");
        var info = CoreProcessSession.CreateStartInfo(Helper, options);
        Assert.Equal("", info.Arguments);
        Assert.Contains("--password-stdin", info.ArgumentList);
        Assert.DoesNotContain("--password", info.ArgumentList);
        Assert.DoesNotContain("-p", info.ArgumentList);
        Assert.DoesNotContain(options.Password, info.ArgumentList);
        string[] args = [.. info.ArgumentList];
        Assert.Equal(options.Username, args[Array.IndexOf(args, "--username") + 1]);
        Assert.Equal(options.ConfigPath, args[Array.IndexOf(args, "--config") + 1]);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public void LaunchOptionsFormattingAndSerializationCannotExposePassword()
    {
        var options = new CoreLaunchOptions("student", "private-runtime-options-secret");
        Assert.DoesNotContain(options.Password, options.ToString());
        Assert.Contains("redacted", options.ToString());
        string json = JsonSerializer.Serialize(options);
        Assert.DoesNotContain(options.Password, json);
        Assert.DoesNotContain("\"Password\"", json);
        Assert.Contains("student", json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EapOnlySelectionAddsExactlyTheIntendedCoreFlag(bool noUdp)
    {
        var options = new CoreLaunchOptions("student", "secret", NoUdp: noUdp);
        var info = CoreProcessSession.CreateStartInfo(Helper, options);
        Assert.Equal(noUdp ? 1 : 0, info.ArgumentList.Count(argument => argument == "--no-udp"));
        Assert.Contains("--password-stdin", info.ArgumentList);
        Assert.DoesNotContain(options.Password, info.ArgumentList);
    }

    [Fact]
    public async Task RealChildReceivesSecretOnlyThroughStdinAndBothStreamsAreDrained()
    {
        var output = new ConcurrentQueue<string>();
        var error = new ConcurrentQueue<string>();
        var options = new CoreLaunchOptions("username with \"quotes\" and spaces", "Unicode🔐 space\\\"secret");
        int code = await new CoreProcessSession(Helper).RunAsync(options, output.Enqueue, error.Enqueue, TestContext.Current.CancellationToken);
        Assert.Equal(17, code);
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Password)));
        Assert.Contains("stdin-sha256:" + expected, output);
        Assert.Contains("stderr-drained", error);
        string[] args = JsonSerializer.Deserialize<string[]>(output.First())!;
        Assert.DoesNotContain(options.Password, args);
        Assert.Contains("[账号已隐藏]", args);
        Assert.Contains("username-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Username))), output);
        Assert.DoesNotContain(options.Username, string.Join("\n", output));
        Assert.DoesNotContain(options.Password, string.Join("\n", output));
    }

    [Theory]
    [InlineData("student123", "123", "[凭据已隐藏]")]
    [InlineData("123", "student123", "[账号已隐藏]")]
    [InlineData("student123", "student123", "[凭据已隐藏]")]
    public async Task ChildOutputRedactsOverlappingCredentialsBeforeDeliveringCallbacks(string username, string password, string hidden)
    {
        var output = new ConcurrentQueue<string>();
        int code = await new CoreProcessSession(Helper).RunAsync(new(username, password), output.Enqueue,
            _ => { }, TestContext.Current.CancellationToken);
        Assert.Equal(17, code);
        string[] arguments = JsonSerializer.Deserialize<string[]>(output.First())!;
        Assert.Equal(hidden, arguments[Array.IndexOf(arguments, "--username") + 1]);
        Assert.DoesNotContain("student", string.Join('\n', output));
    }

    [Fact]
    public async Task MisbehavingCoreCannotExposeSubmittedPasswordInGuiLogs()
    {
        var output = new ConcurrentQueue<string>();
        var error = new ConcurrentQueue<string>();
        string password = "do-not-display-synthetic-secret";
        int code = await new CoreProcessSession(Helper).RunAsync(new CoreLaunchOptions("echo-password", password), output.Enqueue, error.Enqueue, TestContext.Current.CancellationToken);
        Assert.Equal(17, code);
        Assert.DoesNotContain(password, string.Join("\n", output));
        Assert.DoesNotContain(password, string.Join("\n", error));
        Assert.Contains(output, line => line.StartsWith("external-core-echo:", StringComparison.Ordinal) && line.Contains("已隐藏", StringComparison.Ordinal));
        Assert.Contains(error, line => line.StartsWith("external-error-echo:", StringComparison.Ordinal) && line.Contains("已隐藏", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AlreadyCanceledStartDoesNotAttemptToLaunchMissingExecutable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CoreProcessSession("missing-core").RunAsync(new CoreLaunchOptions("student", "secret"), _ => { }, _ => { }, cancellation.Token));
    }

    [Fact]
    public async Task EarlyExitDuringSecretWriteStillStopsPollingAndRemovesPrivateFiles()
    {
        // Exceed the OS pipe buffer so the write deterministically remains
        // pending until the helper exits without reading any stdin bytes.
        string secret = "synthetic-do-not-leak-" + new string('x', 256 * 1024);
        var arguments = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<CoreConnectionStatus>();
        var errors = new ConcurrentQueue<string>();
        Task<int> run = new CoreProcessSession(Helper).RunAsync(new("exit-before-stdin", secret), line =>
        {
            if (line.StartsWith("[\"", StringComparison.Ordinal)) arguments.TrySetResult(JsonSerializer.Deserialize<string[]>(line)!);
        }, errors.Enqueue, TestContext.Current.CancellationToken, state =>
        {
            states.Enqueue(state);
            if (state.Phase == CoreConnectionPhase.WaitingInterface) startup.TrySetResult();
        });
        string[] args = await arguments.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        string path = args[Array.IndexOf(args, "--status-file") + 1];
        string directory = Path.GetDirectoryName(path)!;
        try
        {
            await startup.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            int pid;
            using (JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)))
                pid = document.RootElement.GetProperty("pid").GetInt32();
            await File.WriteAllTextAsync(path + ".startup-observed", "observed", TestContext.Current.CancellationToken);
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                run.WaitAsync(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken));
            Assert.Contains("核心在接收密码前已关闭输入", failure.Message);
            Assert.DoesNotContain(secret, failure.ToString());
            Assert.DoesNotContain(secret, string.Join('\n', errors));
            Assert.Contains("synthetic startup configuration rejected", errors);
            Assert.False(Directory.Exists(directory));
            Assert.DoesNotContain("--password", args);
            Assert.DoesNotContain(secret, args);

            // A leaked status task would accept this replacement for its old
            // child's PID and invoke another callback after RunAsync completed.
            int completedCount = states.Count;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                schema = 1, pid, phase = "online", @interface = "synthetic-adapter",
                eap_authenticated = true, online = true
            }), TestContext.Current.CancellationToken);
            await Task.Delay(350, TestContext.Current.CancellationToken);
            Assert.Equal(completedCount, states.Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SimultaneousLargeStdoutAndStderrCannotDeadlock()
    {
        var output = new ConcurrentQueue<string>();
        var error = new ConcurrentQueue<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        int code = await new CoreProcessSession(Helper).RunAsync(new CoreLaunchOptions("flood-output", "secret"), output.Enqueue, error.Enqueue, deadline.Token);
        Assert.Equal(17, code);
        Assert.Contains("stdout-line-4999", output);
        Assert.Contains("stderr-line-4999", error);
    }

    [Fact]
    public async Task CancellationKillsOnlyTheOwnedChildAndAwaitsItsExit()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> run = new CoreProcessSession(Helper).RunAsync(new CoreLaunchOptions("wait-for-cancellation", "secret"), line =>
        {
            if (line.StartsWith("ready:", StringComparison.Ordinal)) ready.TrySetResult(int.Parse(line[6..]));
        }, _ => { }, cancellation.Token);
        int pid = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited);
        }
        catch (ArgumentException) { } // The OS has already reaped the owned child.
    }

    [Theory]
    [InlineData("line1\nline2")]
    [InlineData("line1\rline2")]
    public async Task MultilinePasswordIsRejectedBeforeLaunchingProcess(string password)
    {
        var output = new ConcurrentQueue<string>();
        await Assert.ThrowsAsync<ArgumentException>(() => new CoreProcessSession(Helper).RunAsync(new CoreLaunchOptions("student", password), output.Enqueue, _ => { }, TestContext.Current.CancellationToken));
        Assert.Empty(output);
    }

    [Fact]
    public async Task MissingExecutableProducesActionableFailureWithoutEchoingSecret()
    {
        using var directory = new TemporaryDirectory();
        string secret = "synthetic-do-not-echo";
        Exception error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CoreProcessSession(directory.File("missing-core")).RunAsync(new CoreLaunchOptions("student", secret), _ => { }, _ => { }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(secret, error.ToString());
    }
}
