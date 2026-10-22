using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class ControlPipeTests
{
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "helpers", OperatingSystem.IsWindows() ? "FakeCore.exe" : "FakeCore");

    [Fact]
    public async Task CancellationDeliversStopBeforeWaitingForOwnedChildToExit()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new ConcurrentQueue<string>();
        Task<int> running = new CoreProcessSession(Helper).RunAsync(new("wait-for-cancellation", "synthetic-pipe-secret"), line =>
        {
            lines.Enqueue(line);
            if (line.StartsWith("ready:", StringComparison.Ordinal)) ready.TrySetResult();
        }, _ => { }, cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken));
        Assert.Contains("graceful-stop", lines);
        Assert.DoesNotContain("synthetic-pipe-secret", string.Join('\n', lines));
        string[] args = System.Text.Json.JsonSerializer.Deserialize<string[]>(lines.First())!;
        int index = Array.IndexOf(args, OperatingSystem.IsWindows() ? "--control-pipe" : "--control-socket");
        Assert.True(index >= 0);
        if (OperatingSystem.IsWindows()) Assert.StartsWith(@"\\.\pipe\drcom4scut-", args[index + 1]);
        else
        {
            Assert.StartsWith("/tmp/d4s-", args[index + 1]);
            Assert.True(Encoding.UTF8.GetByteCount(args[index + 1]) <= 103);
            Assert.False(File.Exists(args[index + 1]));
        }
    }

    [Fact]
    public async Task CoreCanFinishCleanupThatExceedsTheOldTwoSecondBudget()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new ConcurrentQueue<string>();
        Task<int> running = new CoreProcessSession(Helper).RunAsync(new("slow-stop", "synthetic-pipe-secret"), line =>
        {
            lines.Enqueue(line);
            if (line.StartsWith("ready:", StringComparison.Ordinal)) ready.TrySetResult();
        }, _ => { }, cancellation.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
        Assert.Contains("graceful-stop", lines);
        Assert.Contains("cleanup-completed", lines);
    }

    [Fact]
    public async Task NoncooperatingOwnedChildIsKilledAfterTheBoundedGracePeriod()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> running = new CoreProcessSession(Helper).RunAsync(new("ignore-stop", "synthetic-pipe-secret"), line =>
        {
            if (line.StartsWith("ready:", StringComparison.Ordinal)) ready.TrySetResult(int.Parse(line[6..]));
        }, _ => { }, cancellation.Token);
        int id = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken));
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(1.7), TimeSpan.FromSeconds(7));
        try { using Process child = Process.GetProcessById(id); Assert.True(child.HasExited); }
        catch (ArgumentException) { }
    }

    [Fact]
    public async Task ParentPipeDisconnectionIsVisibleToTheChildWithoutSendingCredentialsAgain()
    {
        string directory = OperatingSystem.IsWindows()
            ? Directory.CreateTempSubdirectory("drcom-control-tests-").FullName
            : Directory.CreateDirectory(Path.Combine("/tmp", "d4s-test-" + Guid.NewGuid().ToString("N")[..16]),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        try
        {
        ProcessStartInfo info = CoreProcessSession.CreateStartInfo(Helper, new("control-eof", "synthetic-pipe-secret"));
        await using var server = new PrivateControlServer(info, directory);
        using var child = new Process { StartInfo = info };
        Assert.True(child.Start());
        Task connection = server.Connection;
        await child.StandardInput.WriteLineAsync("synthetic-pipe-secret".AsMemory(), TestContext.Current.CancellationToken);
        child.StandardInput.Close();
        Task<string> error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await connection.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var lines = new List<string>();
        while (await child.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) is string line)
        {
            lines.Add(line);
            if (line.StartsWith("ready:", StringComparison.Ordinal)) break;
        }
        await server.DisposeAsync();
        string remaining = await child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, child.ExitCode);
        Assert.Contains("parent-disconnected", remaining);
        Assert.DoesNotContain("synthetic-pipe-secret", string.Join('\n', lines) + remaining + await error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
