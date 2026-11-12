using Xunit;

namespace drcom4scutGUI.UiTests;

public sealed class StartupRecoveryTests
{
    [Fact]
    public async Task TemporaryStartupFailuresRecoverBeforeAuthentication()
    {
        int probes = 0;
        List<int> retries = [];
        await StartupRecovery.RunAsync(_ =>
        {
            probes++;
            return probes switch
            {
                1 => Task.FromException(new CoreProbeException("driver not ready")),
                2 => Task.FromException(new TimeoutException("boot is busy")),
                _ => Task.CompletedTask
            };
        }, (_, attempt) => retries.Add(attempt), TestContext.Current.CancellationToken, retryDelay: TimeSpan.Zero);
        Assert.Equal(3, probes);
        Assert.Equal([1, 2], retries);
    }

    [Fact]
    public async Task PermanentEnvironmentFailureHasABoundedRetryCountAndCanBeTriedAgain()
    {
        int probes = 0;
        await Assert.ThrowsAsync<CoreProbeException>(() => StartupRecovery.RunAsync(_ =>
        {
            probes++;
            throw new CoreProbeException("missing driver");
        }, (_, _) => { }, TestContext.Current.CancellationToken, attempts: 3, retryDelay: TimeSpan.Zero));
        Assert.Equal(3, probes);
        await StartupRecovery.RunAsync(_ => { probes++; return Task.CompletedTask; },
            (_, _) => Assert.Fail("A successful retry must not wait."), TestContext.Current.CancellationToken);
        Assert.Equal(4, probes);
    }

    [Fact]
    public async Task IncompatibleCoreIsNotRetried()
    {
        int probes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => StartupRecovery.RunAsync(_ =>
        {
            probes++;
            throw new InvalidOperationException("unsupported core");
        }, (_, _) => Assert.Fail("Incompatible versions must stop immediately."), TestContext.Current.CancellationToken));
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task StopDuringStartupWaitPreventsAnotherProbe()
    {
        using CancellationTokenSource stop = new();
        int probes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupRecovery.RunAsync(_ =>
        {
            probes++;
            throw new CoreProbeException("not ready");
        }, (_, _) => stop.Cancel(), stop.Token));
        Assert.Equal(1, probes);
    }
}
