using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace drcom4scutGUI;

public sealed class App : Application
{
    private FileStream? instance;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool isolatedRun = Program.SmokeTest;
#if SCREENSHOT_CAPTURE
            isolatedRun |= MainWindow.UpdateCaptureConfigured;
#endif
            if (!isolatedRun)
            {
                instance = DesktopInstance.TryAcquire(PlatformPaths.ConfigurationDirectory);
                if (instance is null)
                {
                    desktop.Shutdown();
                    return;
                }
            }
#if SCREENSHOT_CAPTURE
            desktop.MainWindow = MainWindow.UpdateCaptureConfigured
                ? MainWindow.CreateUpdateCaptureWindow()
                : new MainWindow(skipBackend: Program.SmokeTest);
#else
            desktop.MainWindow = new MainWindow(skipBackend: Program.SmokeTest);
#endif
            desktop.Exit += (_, _) =>
            {
                instance?.Dispose();
            };
            if (Program.SmokeTest)
            {
                // A native window is actually opened and laid out. Never read
                // accounts, invoke the core or change startup during this probe.
                desktop.MainWindow.Opened += (_, _) => DispatcherTimer.RunOnce(() =>
                {
                    bool opened = desktop.MainWindow.IsVisible && desktop.MainWindow.ClientSize.Width > 0;
                    Console.WriteLine(opened ? "Desktop window smoke test passed" : "Desktop window smoke test failed");
                    desktop.Shutdown(opened ? 0 : 1);
                }, TimeSpan.FromMilliseconds(500));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
