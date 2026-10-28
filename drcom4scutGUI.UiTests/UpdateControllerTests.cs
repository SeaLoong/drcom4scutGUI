using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace drcom4scutGUI.UiTests;

[CollectionDefinition("Avalonia UI", DisableParallelization = true)]
public sealed class AvaloniaUiCollection;

[Collection("Avalonia UI")]
public sealed class UpdateControllerTests
{
    [Fact]
    public async Task FirstFullConnectionWaitsOneMinuteAndReconnectDoesNotResetDailySchedule()
    {
        ManualClock clock = new();
        FakeClient client = new();
        List<string> dialogs = [];
        using UpdateController controller = Controller(client, clock, dialogs);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Empty(client.Checks);
        controller.ConnectionSucceeded();
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(client.Checks);
        controller.ConnectionSucceeded();
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => client.Checks.Count == 2 && clock.PendingTimers == 1);
        Assert.Equal([UpdateComponent.Gui, UpdateComponent.Core], client.Checks);
        controller.ConnectionSucceeded();
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, client.Checks.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => client.Checks.Count == 4 && clock.PendingTimers == 1);
        Assert.Empty(dialogs);
        Assert.Equal(0, client.Downloads);
    }

    [Fact]
    public async Task AutomaticErrorsAreSilentAndOnlyNewVersionsProduceIndependentOffers()
    {
        ManualClock clock = new();
        FakeClient client = new() { Error = new IOException("network failed") };
        List<string> dialogs = [];
        using UpdateController controller = Controller(client, clock, dialogs);
        await controller.CheckAsync(UpdateComponent.Gui, manual: false);
        Assert.Empty(dialogs);
        Assert.Empty(controller.Offers);
        client.Error = null;
        client.Available[UpdateComponent.Gui] = "3.3.0";
        client.Available[UpdateComponent.Core] = "0.7.0";
        await controller.CheckAsync(UpdateComponent.Gui, manual: false);
        await controller.CheckAsync(UpdateComponent.Core, manual: false);
        Assert.Equal(2, controller.Offers.Count);
        Assert.Empty(dialogs);
        Assert.Equal(0, client.Downloads);
        client.Available.Remove(UpdateComponent.Gui);
        await controller.CheckAsync(UpdateComponent.Gui, manual: false);
        Assert.Equal("0.7.0", Assert.Single(controller.Offers).Value.Version);
    }

    [Fact]
    public async Task ManualCurrentAndFailureExplainTheResultAndDecliningNeverDownloads()
    {
        FakeClient client = new();
        List<string> dialogs = [];
        using UpdateController controller = Controller(client, new ManualClock(), dialogs);
        await controller.CheckAsync(UpdateComponent.Gui, manual: true);
        Assert.Contains("当前版本已是最新", Assert.Single(dialogs));
        dialogs.Clear();
        client.Error = new IOException("无法连接更新服务器");
        await controller.CheckAsync(UpdateComponent.Core, manual: true);
        Assert.Contains("检查更新失败", Assert.Single(dialogs));
        Assert.Contains("无法连接更新服务器", dialogs[0]);
        dialogs.Clear();
        client.Error = null;
        client.Available[UpdateComponent.Gui] = "3.3.0";
        await controller.CheckAsync(UpdateComponent.Gui, manual: true);
        Assert.Contains("发现新版本3.3.0，是否更新", Assert.Single(dialogs));
        Assert.Equal(0, client.Downloads);
        Assert.Single(controller.Offers);
    }

    [Fact]
    public async Task ConfirmationDownloadsThenStopsThenInstallsAndKeepsOtherComponentOffer()
    {
        FakeClient client = new();
        List<string> events = client.Events;
        client.Available[UpdateComponent.Gui] = "3.3.0";
        client.Available[UpdateComponent.Core] = "0.7.0";
        using UpdateController controller = new(client, new ManualClock(), (_, prompt) =>
        {
            events.Add(prompt);
            return Task.FromResult(true);
        }, (_, _) => throw new InvalidOperationException("Unexpected failure"),
            component => { events.Add("stop:" + component); return Task.CompletedTask; },
            component => { events.Add("verified:" + component); return Task.CompletedTask; });
        await controller.CheckAsync(UpdateComponent.Gui, manual: false);
        await controller.CheckAsync(UpdateComponent.Core, manual: false);
        await controller.InstallAsync(UpdateComponent.Core);
        Assert.Equal(["发现新版本0.7.0，是否更新", "download:Core", "stop:Core", "install:Core", "verified:Core", "dispose:Core"], events);
        Assert.Equal(UpdateComponent.Gui, Assert.Single(controller.Offers).Key);
        Assert.False(controller.IsBusy);
        Assert.Null(controller.ProgressText);
    }

    [Fact]
    public async Task ManualAcceptUsesTheSameConfirmationAndInstallFailureCanBeRetried()
    {
        FakeClient client = new() { InstallError = new IOException("安装目录不可写") };
        client.Available[UpdateComponent.Gui] = "3.3.0";
        List<string> messages = [];
        using UpdateController controller = Controller(client, new ManualClock(), messages, accept: true);
        await controller.CheckAsync(UpdateComponent.Gui, manual: true);
        Assert.Contains(messages, item => item.Contains("安装目录不可写") && item.Contains("更新失败"));
        Assert.Single(controller.Offers);
        Assert.False(controller.IsBusy);
        client.InstallError = null;
        await controller.InstallAsync(UpdateComponent.Gui);
        Assert.Empty(controller.Offers);
        Assert.Equal(2, client.Downloads);
    }

    [Fact]
    public async Task ClosingCancelsTheMinuteTimerAndAnyActiveCheck()
    {
        ManualClock clock = new();
        FakeClient client = new();
        UpdateController controller = Controller(client, clock, []);
        controller.ConnectionSucceeded();
        controller.Dispose();
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Empty(client.Checks);
        Assert.Equal(0, clock.PendingTimers);

        client.WaitForCancellation = true;
        controller = Controller(client, clock, []);
        Task check = controller.CheckAsync(UpdateComponent.Core, manual: true);
        Assert.Single(client.Checks);
        controller.Dispose();
        await check;
        Assert.True(client.CheckWasCancelled);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task FailedVerificationKeepsTheOfferAndDoesNotClaimSuccess()
    {
        FakeClient client = new();
        client.Available[UpdateComponent.Core] = "0.7.0";
        List<string> messages = [];
        using UpdateController controller = new(client, new ManualClock(), (_, _) => Task.FromResult(true),
            (message, title) => { messages.Add(title + ":" + message); return Task.CompletedTask; },
            _ => Task.CompletedTask, _ => throw new IOException("新核心未通过运行验证"));
        await controller.CheckAsync(UpdateComponent.Core, manual: true);
        Assert.Contains("新核心未通过运行验证", Assert.Single(messages));
        Assert.Equal("0.7.0", Assert.Single(controller.Offers).Value.Version);
        Assert.False(controller.IsBusy);
        Assert.Null(controller.ProgressText);
    }

    [Fact]
    public async Task QueuedDownloadProgressCannotReplaceInstallationOrFailedCompletion()
    {
        FakeClient client = new();
        client.Available[UpdateComponent.Core] = "0.7.0";
        QueuedSynchronizationContext callbacks = new();
        TaskCompletionSource installation = new();
        List<string> messages = [];
        using UpdateController controller = new(client, new ManualClock(), (_, _) => Task.FromResult(true),
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            _ => installation.Task, _ => throw new IOException("新核心未通过运行验证"));

        Task update = WithSynchronizationContext(callbacks, () => controller.CheckAsync(UpdateComponent.Core, manual: true));
        Assert.False(update.IsCompleted);
        Assert.Equal("正在注销连接并安装更新…", controller.ProgressText);
        // DownloadAsync queued this report before returning. Deliver it only
        // after the controller has moved to the installation phase.
        Assert.True(callbacks.RunNext());
        Assert.Equal("正在注销连接并安装更新…", controller.ProgressText);

        installation.SetResult();
        callbacks.Drain();
        Assert.True(update.IsCompleted);
        await update;
        Assert.Contains("新核心未通过运行验证", Assert.Single(messages));
        Assert.Null(controller.ProgressText);

        client.ProgressReports[0].Report(0.9);
        Assert.True(callbacks.RunNext());
        Assert.Null(controller.ProgressText);
        Assert.False(controller.IsBusy);
    }

    [Fact]
    public async Task PreviousDownloadsQueuedReportsCannotReplaceTheNextDownloadsProgress()
    {
        FakeClient client = new();
        client.Available[UpdateComponent.Core] = "0.7.0";
        QueuedSynchronizationContext callbacks = new();
        using UpdateController controller = Controller(client, new ManualClock(), [], accept: true);
        Task first = WithSynchronizationContext(callbacks, () => controller.CheckAsync(UpdateComponent.Core, manual: true));
        Assert.True(first.IsCompleted);
        await first;
        Assert.Null(controller.ProgressText);

        client.DownloadGate = new TaskCompletionSource();
        Task second = WithSynchronizationContext(callbacks, () => controller.CheckAsync(UpdateComponent.Core, manual: true));
        Assert.False(second.IsCompleted);
        Assert.Equal("正在下载并校验更新…", controller.ProgressText);
        // Deliver the first operation's queued report while the next download
        // is active, before delivering that download's own report.
        Assert.True(callbacks.RunNext());
        Assert.Equal("正在下载并校验更新…", controller.ProgressText);
        Assert.True(callbacks.RunNext());
        string? currentProgress = controller.ProgressText;
        Assert.Contains("50", currentProgress);
        client.ProgressReports[0].Report(0.9);
        Assert.True(callbacks.RunNext());
        Assert.Equal(currentProgress, controller.ProgressText);

        client.DownloadGate.SetResult();
        callbacks.Drain();
        Assert.True(second.IsCompleted);
        await second;
        Assert.Null(controller.ProgressText);
        Assert.Empty(controller.Offers);
    }

    [Fact]
    public async Task FailedInstallationRestoresConnectionControlsAndLeavesTheNoticeForRetry()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            FakeClient client = new() { InstallError = new IOException("安装目录不可写") };
            client.Available[UpdateComponent.Core] = "0.7.0";
            List<string> dialogs = [];
            MainWindow window = new(true, new Configuration(), client, new ManualClock(), (message, title, confirm) =>
            {
                dialogs.Add(title + ":" + message);
                return Task.FromResult(confirm);
            });
            window.Show();
            try
            {
                Find<Button>(window, "checkCoreUpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Eventually(() => dialogs.Any(dialog => dialog.StartsWith("更新失败:")));
                Assert.True(Find<Button>(window, "loginButton").IsEnabled);
                Assert.Equal("连接", Find<Button>(window, "loginButton").Content);
                Assert.True(Find<AutoCompleteBox>(window, "nameComboBox").IsEnabled);
                Assert.True(Find<Button>(window, "coreUpdateNotice").IsVisible);
                Assert.DoesNotContain("核心已更新", Find<TextBlock>(window, "statusLabel").Text);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrayStopCannotUnlockConnectionControlsDuringUpdateInstallation(bool failInstallation)
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            FakeClient client = new()
            {
                InstallGate = new TaskCompletionSource(),
                InstallError = failInstallation ? new IOException("安装目录不可写") : null
            };
            client.Available[UpdateComponent.Core] = "0.7.0";
            List<string> dialogs = [];
            MainWindow window = new(true, new Configuration(), client, new ManualClock(), (message, title, confirm) =>
            {
                dialogs.Add(title + ":" + message);
                return Task.FromResult(confirm);
            });
            window.Show();
            try
            {
                Find<Button>(window, "checkCoreUpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Eventually(() => client.Events.Contains("install:Core"));
                Assert.False(Find<Button>(window, "loginButton").IsEnabled);
                Assert.False(Find<AutoCompleteBox>(window, "nameComboBox").IsEnabled);

                // The tray's stop command remains available while the window's
                // connection controls are locked for replacement.
                await window.StopCoreAsync();
                Assert.False(Find<Button>(window, "loginButton").IsEnabled);
                Assert.False(Find<AutoCompleteBox>(window, "nameComboBox").IsEnabled);
                Assert.DoesNotContain("核心已更新", Find<TextBlock>(window, "statusLabel").Text);

                client.InstallGate.SetResult();
                await Eventually(() => client.Events.Contains("dispose:Core")
                    && Find<Button>(window, "checkCoreUpdateButton").IsEnabled);
                Assert.True(Find<Button>(window, "loginButton").IsEnabled);
                Assert.True(Find<AutoCompleteBox>(window, "nameComboBox").IsEnabled);
                Assert.Equal("连接", Find<Button>(window, "loginButton").Content);
                Assert.Equal(failInstallation, Find<Button>(window, "coreUpdateNotice").IsVisible);
                Assert.Equal(failInstallation, dialogs.Any(dialog => dialog.StartsWith("更新失败:")));
            }
            finally
            {
                client.InstallGate.TrySetResult();
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ActualControlsKeepEmptyUpdatesHiddenAndShowBothVersionNoticesWithoutDialogs()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            FakeClient client = new();
            List<string> dialogs = [];
            MainWindow window = new(true, new Configuration(), client, new ManualClock(), (message, title, _) =>
            {
                dialogs.Add(title + ":" + message);
                return Task.FromResult(false);
            });
            window.Show();
            try
            {
                Assert.False(Find<StackPanel>(window, "updatePanel").IsVisible);
                Assert.False(Find<Button>(window, "guiUpdateNotice").IsVisible);
                Assert.False(Find<Button>(window, "coreUpdateNotice").IsVisible);
                Find<Button>(window, "checkUpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Eventually(() => dialogs.Count == 1);
                Assert.Contains("当前版本已是最新", dialogs[0]);
                Assert.False(Find<StackPanel>(window, "updatePanel").IsVisible);
                dialogs.Clear();
                client.Available[UpdateComponent.Gui] = "3.3.0";
                client.Available[UpdateComponent.Core] = "0.7.0";
                Find<Button>(window, "checkUpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Find<Button>(window, "checkCoreUpdateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Eventually(() => dialogs.Count == 2);
                Assert.Contains("3.3.0", window.Title);
                Assert.Contains("0.7.0", window.Title);
                Assert.True(Find<Button>(window, "guiUpdateNotice").IsVisible);
                Assert.True(Find<Button>(window, "coreUpdateNotice").IsVisible);
                Assert.Contains("3.3.0", Find<Button>(window, "guiUpdateNotice").Content?.ToString());
                Assert.Contains("0.7.0", Find<Button>(window, "coreUpdateNotice").Content?.ToString());
                Assert.Equal(0, client.Downloads);
                dialogs.Clear();
                Find<Button>(window, "coreUpdateNotice").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Eventually(() => dialogs.Count == 1);
                Assert.StartsWith("更新核心:发现新版本0.7.0，是否更新", dialogs[0]);
                Assert.Equal(0, client.Downloads);
                Assert.True(Find<Button>(window, "guiUpdateNotice").IsVisible);
            }
            finally { window.Close(); }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PartialStatusWaitsButCompletedEapOnlyStartsTheAutomaticUpdateTimer()
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(async () =>
        {
            FakeClient client = new();
            ManualClock clock = new();
            MainWindow window = new(true, new Configuration(), client, clock, (_, _, _) => throw new InvalidOperationException("Automatic checks must be silent"));
            window.Show();
            try
            {
                window.ApplyConnectionStatus(new(CoreConnectionPhase.UdpAuthentication), "eap", false);
                clock.Advance(TimeSpan.FromDays(1));
                Assert.Empty(client.Checks);
                window.ApplyConnectionStatus(new(CoreConnectionPhase.Online), "eap", true);
                clock.Advance(TimeSpan.FromSeconds(60));
                await Eventually(() => client.Checks.Count == 2);
                Assert.False(Find<StackPanel>(window, "updatePanel").IsVisible);
            }
            finally { window.Close(); }
            clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(2, client.Checks.Count);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static T Find<T>(MainWindow window, string name) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static UpdateController Controller(FakeClient client, TimeProvider clock, List<string> dialogs, bool accept = false) =>
        new(client, clock, (_, prompt) => { dialogs.Add(prompt); return Task.FromResult(accept); },
            (message, title) => { dialogs.Add(title + ":" + message); return Task.CompletedTask; },
            _ => Task.CompletedTask, _ => Task.CompletedTask);

    private static async Task Eventually(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static Task WithSynchronizationContext(SynchronizationContext context, Func<Task> action)
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { return action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (callbacks) callbacks.Enqueue((callback, state));
        }

        internal bool RunNext()
        {
            (SendOrPostCallback Callback, object? State) next;
            lock (callbacks)
            {
                if (callbacks.Count == 0) return false;
                next = callbacks.Dequeue();
            }
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try { next.Callback(next.State); }
            finally { SetSynchronizationContext(previous); }
            return true;
        }

        internal void Drain() { while (RunNext()) { } }
    }

    private sealed class FakeClient : IUpdateClient
    {
        internal readonly Dictionary<UpdateComponent, string> Available = new();
        internal readonly List<UpdateComponent> Checks = [];
        internal readonly List<string> Events = [];
        internal readonly List<IProgress<double>> ProgressReports = [];
        internal Exception? Error;
        internal Exception? InstallError;
        internal bool WaitForCancellation;
        internal bool CheckWasCancelled;
        internal int Downloads;
        internal TaskCompletionSource? DownloadGate;
        internal TaskCompletionSource? InstallGate;

        public async Task<UpdateOffer?> CheckAsync(UpdateComponent component, CancellationToken cancellationToken)
        {
            Checks.Add(component);
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { CheckWasCancelled = true; throw; }
            }
            if (Error is not null) throw Error;
            return Available.TryGetValue(component, out string? version) ? new(component, version) : null;
        }

        public async Task<IUpdateDownload> DownloadAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            Events.Add("download:" + offer.Component);
            ProgressReports.Add(progress);
            progress.Report(0.5);
            if (DownloadGate is not null) await DownloadGate.Task.WaitAsync(cancellationToken);
            return new Download(this, offer.Component);
        }

        private sealed class Download(FakeClient owner, UpdateComponent component) : IUpdateDownload
        {
            public async Task InstallAsync(CancellationToken cancellationToken)
            {
                owner.Events.Add("install:" + component);
                if (owner.InstallGate is not null) await owner.InstallGate.Task.WaitAsync(cancellationToken);
                if (owner.InstallError is not null) throw owner.InstallError;
            }
            public void Dispose() => owner.Events.Add("dispose:" + component);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        internal int PendingTimers { get { lock (timers) return timers.Count(timer => !timer.Disposed); } }
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state, dueTime, period);
            lock (timers) timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan amount)
        {
            now += amount;
            ManualTimer[] due;
            lock (timers) due = timers.Where(timer => !timer.Disposed && timer.Due <= now).ToArray();
            foreach (ManualTimer timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            internal DateTimeOffset Due = due == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + due;
            internal bool Disposed;
            private TimeSpan interval = period;
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + dueTime;
                interval = newPeriod;
                return true;
            }
            internal void Fire()
            {
                if (Disposed) return;
                Due = interval == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + interval;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
