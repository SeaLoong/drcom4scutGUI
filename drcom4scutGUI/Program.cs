using System;
using System.Linq;
#if SCREENSHOT_CAPTURE
using System.IO;
using System.Text.Json;
#endif
using Avalonia;

namespace drcom4scutGUI;

internal static class Program
{
    internal static bool SmokeTest { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (UpdateHelper.TryRun(args, out int updateExitCode)) return updateExitCode;
#if SCREENSHOT_CAPTURE
        try
        {
            if (MainWindow.TryConfigureUpdateCapture(args))
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
#endif
        SmokeTest = args.Contains("--smoke-test", StringComparer.Ordinal);
        if (args.Any(argument => argument != "--smoke-test"))
        {
            Console.Error.WriteLine("Usage: drcom4scutGUI [--smoke-test]");
            return 2;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
