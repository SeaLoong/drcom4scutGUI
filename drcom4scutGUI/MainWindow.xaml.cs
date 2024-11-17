using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
        private string ip = null;
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
            this.comboBox_MAC.Items.Add(String.Empty);
            this.comboBox_IP.Items.Add(String.Empty);
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
                    UnicastIPAddressInformationCollection addressInfoColl = adapter.GetIPProperties().UnicastAddresses;
                    if (addressInfoColl.Count > 0)
                    {
                        foreach (UnicastIPAddressInformation addressInfo in addressInfoColl)
                        {
                            IPAddress address = addressInfo.Address;
                            if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                                IPAddress.IsLoopback(address) ||
                                (address.Address & 0x0000ffff) == (169l + (254l << 8)))
                            {
                                continue;
                            }
                            String ip = address.ToString();
                            int ip_id = this.comboBox_IP.Items.Add(ip);
                            if (this.ip != null && ip == this.ip)
                            {
                                this.comboBox_IP.SelectedIndex = ip_id;
                            }
                        }
                    }
                }
            }
            if (this.mac != null && this.comboBox_MAC.SelectedIndex < 0)
            {
                this.comboBox_MAC.SelectedIndex = this.comboBox_MAC.Items.Add(this.mac);
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
                t = o["ip"];
                if (t != null)
                {
                    this.ip = t.ToString().Trim();
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
                    ["ip"] = this.ip,
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
            if (Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length > 1)
            {
                this.IsEnabled = false;
                MessageBox.Show("程序已在运行！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown(0);
                return;
            }
            GetCoreVersion();
            QuitPreviousCore();
            if (this.core == null)
            {
                this.IsEnabled = false;
                MessageBox.Show("未找到核心程序 drcom4scut.exe，无法使用！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown(0);
                return;
            }
            else
            {
                this.Title = "drcom4scutGUI -   core: " + this.core;
            }
            LoadConfig();
            InitTray();
            InitUI();
            if (this.autoLogin)
            {
                StartCoreProcess();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (this.notifyIcon != null)
            {
                this.notifyIcon.Visible = false;
            }
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
            this.comboBox_IP.IsEnabled = enable;
            this.textBox_username.IsEnabled = enable;
            this.passwordBox_password.IsEnabled = enable;
            this.checkBox_autoLogin.IsEnabled = enable;
        }

        private void Button_login_Click(object sender, RoutedEventArgs e)
        {
            this.mac = this.comboBox_MAC.Text;
            this.ip = this.comboBox_IP.Text;
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
            StringBuilder argumentsSb = new StringBuilder();
            if (!String.IsNullOrEmpty(this.mac))
            {
                argumentsSb.Append($"--mac \"{this.mac}\" ");
            }
            if (!String.IsNullOrEmpty(this.ip))
            {
                argumentsSb.Append($"--ip \"{this.ip}\" ");
            }
            argumentsSb.Append($"--username \"{this.username}\" --password \"{this.password}\"");
            thread = new Thread(new ThreadStart(() =>
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo("drcom4scut.exe", argumentsSb.ToString())
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
                        if (s.Contains("ignore"))
                        {
                            return;
                        }
                        string text = "出现错误，但是保持核心程序运行。";
                        if (s.Contains("Will try reconnect at the next"))
                        {
                            text = s;
                        }
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(() =>
                        {
                            label.Content = text;
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
