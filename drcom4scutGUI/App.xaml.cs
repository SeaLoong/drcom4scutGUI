using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace drcom4scutGUI;

public partial class App : Application
{
    private Mutex? instance;
    private bool ownsInstance;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, @"Local\SeaLoong.drcom4scutGUI", out ownsInstance);
        if (!ownsInstance)
        {
            using Process self = Process.GetCurrentProcess();
            foreach (Process process in Process.GetProcessesByName(self.ProcessName))
            {
                using (process)
                    if (process.Id != self.Id && process.MainWindowHandle != IntPtr.Zero)
                        SetForegroundWindow(process.MainWindowHandle);
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
    }
}
