using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace drcom4scutGUI;

public sealed class App : Application
{
    private FileStream? instance;

    public override void Initialize()
    {
        FluentTheme theme = new();
        theme.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = Color.Parse("#185685") };
        theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#A7D5E2") };
        Styles.Add(theme);
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
                Window window = desktop.MainWindow;
                window.Opened += (_, _) => DispatcherTimer.RunOnce(async () =>
                {
                    bool opened = window.IsVisible && window.ClientSize.Width > 0;
                    if (opened && OperatingSystem.IsWindows()) opened = await CheckWindowRestoreAsync(window);
                    Console.WriteLine(opened ? "Desktop window smoke test passed" : "Desktop window smoke test failed");
                    desktop.Shutdown(opened ? 0 : 1);
                }, TimeSpan.FromMilliseconds(500));
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<bool> CheckWindowRestoreAsync(Window window)
    {
        foreach (Size requested in new Size[] { new(475, 550), new(640, 680) })
        {
            window.Width = requested.Width;
            window.Height = requested.Height;
            await Task.Delay(200);
            Size before = window.ClientSize;
            window.WindowState = WindowState.Maximized;
            await Task.Delay(200);
            bool maximized = window.WindowState == WindowState.Maximized && window.ClientSize != before;
            window.WindowState = WindowState.Normal;
            await Task.Delay(200);
            Size after = window.ClientSize;
            Console.WriteLine($"Restore {before} -> {after}");
            if (!maximized || window.WindowState != WindowState.Normal
                || Math.Abs(before.Width - after.Width) > 1 || Math.Abs(before.Height - after.Height) > 1)
                return false;
        }
        return true;
    }
}
