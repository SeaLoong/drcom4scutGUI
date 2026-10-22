using System;
using System.Linq;
using Avalonia;

namespace drcom4scutGUI;

internal static class Program
{
    internal static bool SmokeTest { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
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
