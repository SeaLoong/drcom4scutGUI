using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class StructuredStatusTests
{
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "helpers", OperatingSystem.IsWindows() ? "FakeCore.exe" : "FakeCore");

    [Theory]
    [InlineData("eap", false)]
    [InlineData("eap", true)]
    [InlineData("portal", false)]
    public async Task StructuredOnlineStateNeedsNoConsoleLog(string mode, bool noUdp)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        Task<int> run = new CoreProcessSession(Helper).RunAsync(new("quiet-status", "synthetic-status-secret", Mode: mode,
            NoUdp: noUdp, PortalBase: mode == "portal" ? "https://portal.example.test" : ""), output.Enqueue, errors.Enqueue, cancellation.Token,
            state => { if (state.IsOnline) online.TrySetResult(); });
        await online.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(output);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task SuccessPhrasesInsideServerMessagesCannotConfirmAuthentication()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new ConcurrentQueue<CoreConnectionStatus>();
        var lines = new ConcurrentQueue<string>();
        Task<int> run = new CoreProcessSession(Helper).RunAsync(new("forged-success", "synthetic-status-secret"), lines.Enqueue, lines.Enqueue,
            cancellation.Token, state =>
            {
                states.Enqueue(state);
                if (state.Phase == CoreConnectionPhase.Offline) offline.TrySetResult();
            });
        await offline.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Contains(lines, line => line.Contains("UDP heartbeat completed.", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Portal authorization confirmed online.", StringComparison.Ordinal));
        Assert.DoesNotContain(states, state => state.IsOnline);
    }

    [Fact]
    public async Task PasswordRedactionCannotEraseTheIndependentOnlineState()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new ConcurrentQueue<string>();
        Task<int> run = new CoreProcessSession(Helper).RunAsync(new("status-with-redacted-marker", "completed"), lines.Enqueue, lines.Enqueue,
            cancellation.Token, state => { if (state.IsOnline) online.TrySetResult(); });
        await online.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.DoesNotContain("completed", string.Join('\n', lines));
        Assert.Contains(lines, line => line == "UDP heartbeat [密码已隐藏].");
    }

    [Fact]
    public async Task OnlineToOfflineTransitionIsObservedAndPrivateSessionFilesAreRemoved()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusPath = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> run = new CoreProcessSession(Helper).RunAsync(new("status-transition", "synthetic-status-secret"), line =>
        {
            if (!line.StartsWith("[\"", StringComparison.Ordinal)) return;
            string[] args = JsonSerializer.Deserialize<string[]>(line)!;
            statusPath.TrySetResult(args[Array.IndexOf(args, "--status-file") + 1]);
        }, _ => { }, cancellation.Token, state =>
        {
            if (state.IsOnline) online.TrySetResult();
            if (state.Phase == CoreConnectionPhase.Offline) offline.TrySetResult();
        });
        string path = await statusPath.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await online.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        string directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
        {
            DirectorySecurity security = new DirectoryInfo(directory).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Assert.Equal(identity.User, rule.IdentityReference);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            string socket = Path.Combine(directory, "c.sock");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(socket) <= 103);
        }
        // The helper holds its Online state until this acknowledgement, avoiding
        // timing guesses about the GUI polling interval or host scheduling.
        await File.WriteAllTextAsync(path + ".online-observed", "observed", TestContext.Current.CancellationToken);
        await offline.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(Directory.Exists(directory));
    }
}
