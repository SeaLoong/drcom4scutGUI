using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace drcom4scutGUI
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        [DllImport("user32.dll")]
        protected static extern bool SetForegroundWindow(IntPtr hWnd);

        protected static Mutex mutex = new Mutex(true, "{67C0C622-65A4-45DA-9485-070C490922AA}");

        /// <summary>
        /// Application Entry Point.
        /// </summary>
        [System.STAThreadAttribute()]
        public static void Main()
        {
            Process currentProcess = Process.GetCurrentProcess();
            Process[] processes = Process.GetProcessesByName(currentProcess.ProcessName);
            if (mutex.WaitOne(TimeSpan.Zero, true) && processes.Length == 1)
            {
                App app = new App();
                app.InitializeComponent();
                app.Run();
                mutex.ReleaseMutex();
            }
            else
            {
                MessageBox.Show("程序已在运行！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                foreach (Process process in processes)
                {
                    if (process.MainWindowHandle != IntPtr.Zero &&
                        process.MainWindowHandle != currentProcess.MainWindowHandle)
                    {
                        SetForegroundWindow(process.MainWindowHandle);
                    }
                }
            }
            mutex.Close();
        }
    }
}
