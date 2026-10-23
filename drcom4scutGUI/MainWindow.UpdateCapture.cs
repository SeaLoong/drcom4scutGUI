#if SCREENSHOT_CAPTURE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace drcom4scutGUI;

// Compiled only by the dedicated native screenshot workflow. Production
// packages cannot select a fixture, replace the update transport, or use these
// command-line arguments.
public sealed partial class MainWindow
{
    private sealed record CaptureSettings(string Scenario, string ReadyFile, string CoreVersion,
        Configuration Configuration, JsonElement GuiRelease, JsonElement CoreRelease);

    private static CaptureSettings? updateCapture;
    internal static bool UpdateCaptureConfigured => updateCapture is not null;

    internal static bool TryConfigureUpdateCapture(string[] arguments)
    {
        if (!arguments.Any(value => value.StartsWith("--capture-", StringComparison.Ordinal))) return false;
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        foreach (string argument in arguments)
        {
            int separator = argument.IndexOf('=');
            if (separator < 0 || !options.TryAdd(argument[..separator], argument[(separator + 1)..]))
                throw new ArgumentException("Native screenshot capture requires three unique capture arguments.");
        }
        if (options.Count != 3 || !options.TryGetValue("--capture-update", out string? scenario)
            || scenario is not ("idle" or "current" or "available" or "confirm")
            || !options.TryGetValue("--capture-update-fixture", out string? fixture)
            || !options.TryGetValue("--capture-ready-file", out string? ready)
            || !Path.IsPathFullyQualified(fixture) || !Path.IsPathFullyQualified(ready)
            || fixture == ready || File.Exists(ready))
            throw new ArgumentException("Invalid native screenshot capture arguments.");
        if (new FileInfo(fixture).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("The screenshot fixture exceeds its size budget.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(fixture));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("guiVersion").GetString() != GuiVersion)
            throw new InvalidDataException("The screenshot fixture does not match this GUI build.");
        string coreVersion = root.GetProperty("coreVersion").GetString()
            ?? throw new InvalidDataException("The screenshot fixture is missing its core version.");
        Configuration configuration = JsonSerializer.Deserialize<Configuration>(root.GetProperty("configuration"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The screenshot fixture is missing its example configuration.");
        if (configuration.Auto || configuration.Accounts.Any(account => account.Password.Length != 0 || account.RememberPassword))
            throw new InvalidDataException("Screenshots require password-free example accounts with automatic login disabled.");
        updateCapture = new(scenario, ready, coreVersion, configuration,
            root.GetProperty("gui").Clone(), root.GetProperty("core").Clone());
        return true;
    }

    internal static MainWindow CreateUpdateCaptureWindow()
    {
        CaptureSettings settings = updateCapture ?? throw new InvalidOperationException("Capture was not configured.");
        CaptureUpdateClient client = new(settings);
        MainWindow window = new(skipBackend: true, settings.Configuration, client, null, null)
        {
            coreVersion = "drcom4scut " + settings.CoreVersion
        };
        window.RefreshUpdateUI();
        window.Closed += (_, _) => client.Dispose();
        window.Opened += async (_, _) =>
        {
            // Match the production window's working-area constraint. Without
            // this, Windows' taskbar can cover the bottom of the photographed
            // client rectangle on a hosted runner's small desktop.
            if (window.Screens.Primary is { } screen)
                window.MaxHeight = Math.Max(420, screen.WorkingArea.Height / screen.Scaling - 35);
            window.label.Text = "示例账号 student-demo，密码为空，未发起校园网认证。";
            if (settings.Scenario == "idle")
            {
                DispatcherTimer.RunOnce(() => WriteReady(window), TimeSpan.FromMilliseconds(500));
                return;
            }
            if (settings.Scenario == "available")
            {
                await window.updates.CheckAsync(UpdateComponent.Gui, manual: false);
                await window.updates.CheckAsync(UpdateComponent.Core, manual: false);
                if (window.updates.Offers.Count != 2)
                    throw new InvalidDataException("The availability fixture did not produce both real update notices.");
                DispatcherTimer.RunOnce(() => WriteReady(window), TimeSpan.FromMilliseconds(500));
                return;
            }
            string expectedTitle = settings.Scenario == "current" ? "检查更新" : "更新图形界面";
            DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                Window? dialog = window.OwnedWindows.SingleOrDefault(value => value.IsVisible && value.Title == expectedTitle);
                if (dialog is null || dialog.ClientSize.Width <= 0) return;
                timer.Stop();
                DispatcherTimer.RunOnce(() => WriteReady(dialog), TimeSpan.FromMilliseconds(500));
            };
            window.Closed += (_, _) => timer.Stop();
            timer.Start();
            // The real manual-check handler owns the real native dialog. It is
            // deliberately left open; capture never accepts an installation.
            await window.updates.CheckAsync(UpdateComponent.Gui, manual: true);
        };
        return window;

        void WriteReady(Window target)
        {
            File.WriteAllText(settings.ReadyFile, JsonSerializer.Serialize(new
            {
                scenario = settings.Scenario, windowTitle = target.Title,
                requests = client.Requests, mockUpdateAvailability = true, campusAuthentication = false
            }), Encoding.UTF8);
        }
    }

    private sealed class CaptureUpdateClient : IUpdateClient, IDisposable
    {
        private readonly CaptureSettings settings;
        private readonly HttpClient http;
        private readonly UpdateService service;
        internal List<string> Requests { get; } = [];

        internal CaptureUpdateClient(CaptureSettings settings)
        {
            this.settings = settings;
            http = new HttpClient(new CaptureReleaseHandler(settings, Requests));
            service = new UpdateService(http, options: new UpdateOptions());
        }

        public async Task<UpdateOffer?> CheckAsync(UpdateComponent component, CancellationToken cancellationToken)
        {
            UpdateCheckResult result = await service.CheckAsync(component,
                component == UpdateComponent.Gui ? GuiVersion : settings.CoreVersion, cancellationToken);
            return result.IsUpdateAvailable ? new UpdateOffer(component, result.Release.Version, result.Release) : null;
        }

        public Task<IUpdateDownload> DownloadAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Screenshot fixtures never download or install an update.");

        public void Dispose() { service.Dispose(); http.Dispose(); }
    }

    private sealed class CaptureReleaseHandler(CaptureSettings settings, List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string address = request.RequestUri?.AbsoluteUri ?? "";
            JsonElement release = address switch
            {
                "https://gh-proxy.com/https://api.github.com/repos/SeaLoong/drcom4scutGUI/releases/latest" => settings.GuiRelease,
                "https://gh-proxy.com/https://api.github.com/repos/SeaLoong/drcom4scut/releases/latest" => settings.CoreRelease,
                _ => default
            };
            string body;
            if (release.ValueKind != JsonValueKind.Undefined) body = release.GetRawText();
            else
            {
                JsonElement[] releases = [settings.GuiRelease, settings.CoreRelease];
                JsonElement matched = releases.SingleOrDefault(value =>
                {
                    string api = value.GetProperty("url").GetString()!;
                    string repository = api[..api.LastIndexOf("/releases/", StringComparison.Ordinal)];
                    return address == "https://gh-proxy.com/" + repository + "/git/ref/tags/" + value.GetProperty("tag_name").GetString();
                });
                if (matched.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidOperationException("Screenshot fixture rejected an unexpected network request.");
                body = JsonSerializer.Serialize(new
                {
                    @ref = "refs/tags/" + matched.GetProperty("tag_name").GetString(),
                    @object = new { type = "commit", sha = matched.GetProperty("target_commitish").GetString() }
                });
            }
            requests.Add(address);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
#endif
