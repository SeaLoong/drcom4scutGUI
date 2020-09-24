using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace drcom4scutGUI
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public const string FILE_PATH = "gui.json";
        private string mac = null;
        private string username = null;
        private string password = null;
        private bool autoLogin = false;
        private string core = null;
        private Process process = null;
        private bool success = false;
        private Thread thread = null;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void ShowAndActive()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        NotifyIcon notifyIcon;
        private void InitTray()
        {
            SystemTrayParameter pars = new SystemTrayParameter(Resource1.DrClient, this.Title, null, 0)
            {
                Click = (object _, MouseEventArgs e) =>
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        this.ShowAndActive();
                    }
                }
            };
            List<SystemTrayMenu> ls = new List<SystemTrayMenu>
            {
                new SystemTrayMenu("主界面", (_, __) =>
                {
                    this.ShowAndActive();
                }),
                new SystemTrayMenu("退出", (_, __) =>
                {
                    this.Close();
                })
            };
            this.notifyIcon = WPFSystemTray.SetSystemTray(pars, ls);
        }

        private void InitNetworkInterface()
        {
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface adapter in nics)
            {
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                {
                    byte[] bs = adapter.GetPhysicalAddress().GetAddressBytes();
                    StringBuilder sb = new StringBuilder(20);
                    for (int i = 0; i < bs.Length; i++)
                    {
                        if (i > 0) sb.Append(":");
                        sb.Append(string.Format("{0:x2}", bs[i]));
                    }
                    string s = sb.ToString();
                    int id = this.comboBox_MAC.Items.Add(s);
                    if (this.mac != null && s == this.mac)
                    {
                        this.comboBox_MAC.SelectedIndex = id;
                    }
                }
            }
        }

        private void InitUI()
        {
            InitNetworkInterface();
            this.textBox_username.Text = this.username;
            this.passwordBox_password.Password = this.password;
            this.checkBox_autoLogin.IsChecked = this.autoLogin;
        }

        private void LoadConfig()
        {
            JObject o = null;
            try
            {
                o = JObject.Parse(File.ReadAllText(FILE_PATH));
                JToken t = o["mac"];
                if (t != null)
                {
                    this.mac = t.ToString().Trim();
                }
                t = o["username"];
                if (t != null)
                {
                    this.username = t.ToString().Trim();
                }
                t = o["password"];
                if (t != null)
                {
                    this.password = t.ToString().Trim();
                }
                t = o["autoLogin"];
                if (t != null)
                {
                    this.autoLogin = (bool)t;
                }
            }
            catch (Exception) { }
        }

        private void SaveConfig()
        {
            try
            {
                JObject o = new JObject
                {
                    ["mac"] = this.mac,
                    ["username"] = this.username,
                    ["password"] = this.password,
                    ["autoLogin"] = this.autoLogin
                };
                File.WriteAllText(FILE_PATH, o.ToString());
            }
            catch (Exception) { }
        }

        private void QuitPreviousCore()
        {
            foreach (Process p in Process.GetProcessesByName("drcom4scut.exe"))
            {
                try
                {
                    p.Kill();
                }
                catch (Exception) { }
            }
        }

        private void GetCoreVersion()
        {
            Process process = new Process
            {
                StartInfo = new ProcessStartInfo("drcom4scut.exe", "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            try
            {
                process.Start();
                Regex regex = new Regex("drcom4scut\\s*(\\d\\S*)");
                this.core = regex.Match(process.StandardOutput.ReadToEnd()).Groups[1].Value;
                process.WaitForExit();
            }
            catch (Exception) { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadConfig();
            InitTray();
            InitUI();
            GetCoreVersion();
            QuitPreviousCore();
            if (this.core == null)
            {
                this.IsEnabled = false;
                MessageBox.Show("未找到核心程序 drcom4scut.exe，无法使用！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                label.Content = "未找到核心程序 drcom4scut.exe，无法使用！";
            }
            else
            {
                this.Title = "drcom4scutGUI -   core: " + this.core;
            }
            if (this.autoLogin)
            {
                StartCoreProcess();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            this.notifyIcon.Visible = false;
            try
            {
                if (process != null) process.Kill();
            }
            catch (Exception) { }
            try
            {
                if (thread != null) thread.Abort();
            }
            catch (Exception) { }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
            }
        }

        private delegate void EnableUIDelegate(bool enable);
        private void SetUIEnabled(bool enable)
        {
            this.button_login.Visibility = enable ? Visibility.Visible : Visibility.Hidden;
            this.button_logout.Visibility = !enable ? Visibility.Visible : Visibility.Hidden;
            this.comboBox_MAC.IsEnabled = enable;
            this.textBox_username.IsEnabled = enable;
            this.passwordBox_password.IsEnabled = enable;
            this.checkBox_autoLogin.IsEnabled = enable;
        }

        private void Button_login_Click(object sender, RoutedEventArgs e)
        {
            this.mac = this.comboBox_MAC.Text;
            this.username = this.textBox_username.Text;
            this.password = this.passwordBox_password.Password;
            this.autoLogin = this.checkBox_autoLogin.IsChecked.Value;
            SaveConfig();
            StartCoreProcess();
        }

        private void Button_logout_Click(object sender, RoutedEventArgs e)
        {
            QuitCoreProcess();
        }

        private delegate void NoArgDelegate();
        private void OnSuccess()
        {
            label.Content = "登录成功！";
            this.WindowState = WindowState.Minimized;
        }

        private void QuitCoreProcess()
        {
            label.Content = "正在断开...";
            try
            {
                if (process != null) process.Kill();
            }
            catch (Exception) { }
            this.process = null;
            try
            {
                if (thread != null) thread.Abort();
            }
            catch (Exception) { }
            this.thread = null;
            this.success = false;
            SetUIEnabled(true);
            this.ShowAndActive();
            label.Content = "已断开！";
        }

        private void StartCoreProcess()
        {
            label.Content = "正在登录...";
            SetUIEnabled(false);
            Regex regexError = new Regex("\\[.*?\\]\\[ERROR]\\[.*?\\](.+)");
            StringBuilder sb = new StringBuilder();
            thread = new Thread(new ThreadStart(() =>
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo("drcom4scut.exe", string.Format("--mac {0} --username {1} --password {2}", this.mac, this.username, this.password))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.OutputDataReceived += (sender, args) =>
                {
                    string data = args.Data;
                    if (data == null) return;
                    if (data.Contains("panic"))
                    {
                        MessageBox.Show(data, "PANIC");
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(this.QuitCoreProcess));
                        return;
                    }
                    if (data.Contains("802.1X Authorization success!"))
                    {
                        this.success = true;
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(this.OnSuccess));
                    }
                    Match match = regexError.Match(data);
                    if (!match.Success) return;
                    string s = match.Groups[1].Value;
                    if (this.success)
                    {
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(() =>
                        {
                            label.Content = "出现错误，但是保持连接。";
                            this.notifyIcon.ShowBalloonTip(5000, "错误", s, ToolTipIcon.Error);
                        }));
                        return;
                    }
                    sb.Append(s);
                    sb.Append("\n");
                    if (s.Contains("reconnect"))
                    {
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(() =>
                        {
                            MessageBox.Show(sb.ToString(), "错误");
                            label.Content = "登录失败！";
                        }));
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(this.QuitCoreProcess));
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.WaitForExit();
                this.Dispatcher.BeginInvoke(new NoArgDelegate(this.QuitCoreProcess));
            }));
            thread.Start();

        }
    }
}
