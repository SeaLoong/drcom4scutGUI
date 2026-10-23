using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

internal sealed record UpdateOffer(UpdateComponent Component, string Version, UpdateRelease? Release = null);

internal interface IUpdateClient
{
    Task<UpdateOffer?> CheckAsync(UpdateComponent component, CancellationToken cancellationToken);
    Task<IUpdateDownload> DownloadAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken cancellationToken);
}

internal interface IUpdateDownload : IDisposable
{
    Task InstallAsync(CancellationToken cancellationToken);
}

/// <summary>Checks never install; every install passes through the same explicit confirmation.</summary>
internal sealed class UpdateController : IDisposable
{
    private readonly IUpdateClient client;
    private readonly TimeProvider clock;
    private readonly Func<UpdateComponent, string, Task<bool>> confirm;
    private readonly Func<string, string, Task> message;
    private readonly Func<UpdateComponent, Task> beforeInstall;
    private readonly Func<UpdateComponent, Task> afterInstall;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly Dictionary<UpdateComponent, UpdateOffer> offers = new();
    private bool scheduleStarted;
    private bool disposed;

    internal UpdateController(IUpdateClient client, TimeProvider clock,
        Func<UpdateComponent, string, Task<bool>> confirm, Func<string, string, Task> message,
        Func<UpdateComponent, Task> beforeInstall, Func<UpdateComponent, Task> afterInstall)
    {
        this.client = client;
        this.clock = clock;
        this.confirm = confirm;
        this.message = message;
        this.beforeInstall = beforeInstall;
        this.afterInstall = afterInstall;
    }

    internal event Action? Changed;
    internal IReadOnlyDictionary<UpdateComponent, UpdateOffer> Offers => offers;
    internal bool IsBusy { get; private set; }
    internal string? ProgressText { get; private set; }

    internal void ConnectionSucceeded()
    {
        if (disposed || scheduleStarted) return;
        scheduleStarted = true;
        _ = RunScheduleAsync();
    }

    private async Task RunScheduleAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), clock, lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                await CheckAsync(UpdateComponent.Gui, manual: false);
                await CheckAsync(UpdateComponent.Core, manual: false);
                await Task.Delay(TimeSpan.FromHours(24), clock, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    internal async Task CheckAsync(UpdateComponent component, bool manual)
    {
        if (disposed) return;
        if (manual)
        {
            if (!await operation.WaitAsync(0)) return;
        }
        else
        {
            try { await operation.WaitAsync(lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        }
        try
        {
            if (disposed) return;
            SetBusy(true);
            UpdateOffer? offer;
            try { offer = await client.CheckAsync(component, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                if (manual && !disposed) await message("无法完成检查，请确认网络连接后重试。\n" + error.Message, "检查更新失败");
                return;
            }
            if (disposed) return;
            if (offer is null) offers.Remove(component);
            else offers[component] = offer;
            Changed?.Invoke();
            if (!manual) return;
            if (offer is null) await message("当前版本已是最新", component == UpdateComponent.Gui ? "检查更新" : "检查核心更新");
            else await ConfirmAndInstallAsync(offer);
        }
        finally { SetBusy(false); operation.Release(); }
    }

    internal async Task InstallAsync(UpdateComponent component)
    {
        if (disposed || !await operation.WaitAsync(0)) return;
        try
        {
            if (!offers.TryGetValue(component, out UpdateOffer? offer)) return;
            SetBusy(true);
            await ConfirmAndInstallAsync(offer);
        }
        finally { SetBusy(false); operation.Release(); }
    }

    private async Task ConfirmAndInstallAsync(UpdateOffer offer)
    {
        string prompt = $"发现新版本{offer.Version}，是否更新";
        if (!await confirm(offer.Component, prompt) || disposed) return;
        try
        {
            ProgressText = "正在下载并校验更新…";
            Changed?.Invoke();
            Progress<double> progress = new(value =>
            {
                if (disposed || ProgressText is null) return;
                ProgressText = $"正在下载并校验更新… {Math.Clamp(value, 0, 1):P0}";
                Changed?.Invoke();
            });
            using IUpdateDownload download = await client.DownloadAsync(offer, progress, lifetime.Token);
            if (disposed) return;
            ProgressText = "正在注销连接并安装更新…";
            Changed?.Invoke();
            await beforeInstall(offer.Component);
            lifetime.Token.ThrowIfCancellationRequested();
            await download.InstallAsync(lifetime.Token);
            await afterInstall(offer.Component);
            offers.Remove(offer.Component);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!disposed) await message(error.Message + "\n可重新连接后再重试更新。", "更新失败");
        }
        finally { ProgressText = null; Changed?.Invoke(); }
    }

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        // Pending async work owns the token until it observes cancellation.
    }
}

internal sealed class DesktopUpdateClient : IUpdateClient
{
    private readonly Func<UpdateComponent, string> currentVersion;

    internal DesktopUpdateClient(Func<UpdateComponent, string> currentVersion) => this.currentVersion = currentVersion;

    public async Task<UpdateOffer?> CheckAsync(UpdateComponent component, CancellationToken cancellationToken)
    {
        using UpdateService service = new();
        UpdateCheckResult result = await service.CheckAsync(component, currentVersion(component), cancellationToken);
        return result.IsUpdateAvailable ? new UpdateOffer(component, result.Release.Version, result.Release) : null;
    }

    public async Task<IUpdateDownload> DownloadAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken cancellationToken)
    {
        UpdateService service = new();
        PreparedUpdate? prepared = null;
        try
        {
            prepared = await service.DownloadAsync(offer.Release ?? throw new InvalidOperationException("更新信息已失效，请重新检查。"), progress, cancellationToken);
            if (prepared.Release.Component == UpdateComponent.Gui)
                await service.ValidateGuiInstallAsync(prepared, PlatformPaths.GuiExecutablePath, cancellationToken);
            return new DesktopUpdateDownload(service, prepared);
        }
        catch
        {
            try { prepared?.Dispose(); }
            finally { service.Dispose(); }
            throw;
        }
    }

    private sealed class DesktopUpdateDownload(UpdateService service, PreparedUpdate prepared) : IUpdateDownload
    {
        public Task InstallAsync(CancellationToken cancellationToken) => prepared.Release.Component == UpdateComponent.Core
            ? service.InstallCoreAsync(prepared, PlatformPaths.CoreExecutablePath, cancellationToken)
            : service.ScheduleGuiInstallAsync(prepared, PlatformPaths.GuiExecutablePath, cancellationToken);

        public void Dispose()
        {
            try { prepared.Dispose(); }
            finally { service.Dispose(); }
        }
    }
}

internal sealed class PreviewUpdateClient : IUpdateClient
{
    public Task<UpdateOffer?> CheckAsync(UpdateComponent component, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("界面预览模式不执行更新检查。");
    public Task<IUpdateDownload> DownloadAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("界面预览模式不下载更新。");
}
