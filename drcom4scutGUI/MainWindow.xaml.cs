using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace drcom4scutGUI
{
    public partial class MainWindow : Window
    {
        private Configuration config = null;
        private Account current = null;
        private bool loading = false;
        private string version = "";
        private Process process = null;
        private bool success = false;
        private Thread thread = null;

        public MainWindow()
        {
            InitializeComponent();
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ComboBox.TextProperty, typeof(System.Windows.Controls.ComboBox))
                .AddValueChanged(this.nameComboBox, NameComboBox_TextChanged);
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
            ContextMenuStrip menuStrip = new ContextMenuStrip();
            menuStrip.Items.Add(new ToolStripMenuItem("主页", null, (sender, e) => { this.ShowAndActive(); }));
            menuStrip.Items.Add(new ToolStripMenuItem("退出", null, (sender, e) => { this.Close(); }));
            this.notifyIcon = new NotifyIcon()
            {
                ContextMenuStrip = menuStrip,
                Icon = drcom4scutGUI.Resources.DrClient,
                Text = this.Title,
                Visible = true
            };
            notifyIcon.MouseClick += (sender, e) => { if (e.Button == MouseButtons.Left) { this.ShowAndActive(); } };
        }

        private void InitNetworkInterface()
        {
            this.macComboBox.Items.Add(new ComboBoxItem { Content = string.Empty, Tag = string.Empty });
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface adapter in nics)
            {
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet)
                    continue;
                string mac = BitConverter.ToString(adapter.GetPhysicalAddress().GetAddressBytes()).Replace('-', ':');
                this.macComboBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{adapter.Name} ({mac})",
                    Tag = mac,
                    ToolTip = adapter.Description
                });
            }
            this.macComboBox.SelectedValue = config.Mac;
            if (this.macComboBox.SelectedIndex < 0)
            {
                this.macComboBox.SelectedIndex = this.macComboBox.Items.Add(
                    new System.Windows.Controls.ComboBoxItem { Content = $"({config.Mac})", Tag = config.Mac });
            }
        }

        private void InitUI()
        {
            InitNetworkInterface();
            loading = true;
            foreach (Account account in config.Accounts)
                this.nameComboBox.Items.Add(account.Name);
            current = config.Find(config.Account);
            this.nameComboBox.Text = current?.Name ?? "";
            ShowAccount(current);
            this.autoCheckBox.IsChecked = config.Auto;
            loading = false;
        }

        private void LoadConfig()
        {
            try
            {
                config = Configuration.Load();
            }
            catch (Exception e)
            {
                config = new Configuration();
                MessageBox.Show($"读取配置文件失败，将使用空配置！\n{Configuration.FilePath}\n{e.Message}",
                    "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowAccount(Account account)
        {
            current = account;
            this.deleteButton.IsEnabled = account != null;
            this.passwordTextBox.Password = account?.Password ?? "";
            this.ipTextBox.Text = account?.IP ?? "";
        }

        private void NameComboBox_TextChanged(object sender, EventArgs e)
        {
            string username = this.nameComboBox.Text.Trim();
            if (loading)
                return;
            Account account = config.Find(username);
            if (account != current)
            {
                if (current != null)
                {
                    SaveInput();
                }
                ShowAccount(account);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (current == null) return;
            if (MessageBox.Show($"确定删除账号 {current.Name} 吗？", "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            loading = true;
            config.Accounts.Remove(current);
            if (config.Account == current.Name)
            {
                config.Account = "";
            }
            this.nameComboBox.Items.Remove(current.Name);
            this.nameComboBox.Text = "";
            ShowAccount(null);
            loading = false;
            SaveConfig();
        }

        private void IpTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string ip = this.ipTextBox.Text.Trim();
            if (ip.Length == 0 || IPAddress.TryParse(ip, out IPAddress ipaddress))
            {
                this.ipTextBox.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
                this.ipTextBox.ToolTip = null;
            }
            else
            {
                this.ipTextBox.Background = System.Windows.Media.Brushes.MistyRose;
                this.ipTextBox.ToolTip = "IP地址格式不正确，不需要指定时请留空";
            }
        }

        private void SaveInput(bool all = false)
        {
            bool changed = false;
            string username = this.nameComboBox.Text.Trim();
            string password = this.passwordTextBox.Password;
            string ip = this.ipTextBox.Text.Trim();
            if (ip.Length == 0)
            {
                ip = "";
            }
            else if (IPAddress.TryParse(ip, out IPAddress ipaddress))
            {
                ip = ipaddress.ToString();
            }
            else
            {
                ip = current?.IP ?? "";
            }
            if (current == null && username.Length > 0 && password.Length > 0)
            {
                changed = true;
                current = new Account { Name = username, Password = password, IP = ip };
                config.Accounts.Add(current);
                config.Accounts.Sort();
                this.nameComboBox.Items.Add(username);
                this.deleteButton.IsEnabled = true;
            }
            else if (current != null && (current.Password != password || current.IP != ip))
            {
                changed = true;
                current.Password = password;
                current.IP = ip;
            }
            if (all)
            {
                string mac = (string)this.macComboBox.SelectedValue ?? "";
                bool auto = this.autoCheckBox.IsChecked == true;
                if (config.Mac != mac || config.Account != current.Name || config.Auto != auto)
                {
                    changed = true;
                    config.Mac = mac;
                    config.Account = current.Name;
                    config.Auto = auto;
                }
            }
            if (changed)
                SaveConfig();
        }

        private void SaveConfig()
        {
            try
            {
                config.Save();
            }
            catch (Exception e)
            {
                MessageBox.Show($"保存配置文件失败！\n{Configuration.FilePath}\n{e.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void KillCoreProcess()
        {
            foreach (Process process in Process.GetProcessesByName("drcom4scut"))
            {
                try { process.Kill(); }
                catch (Exception) { }
            }
        }

        private static Process BuildCoreProcess(string arguments)
        {
            return new Process
            {
                StartInfo = new ProcessStartInfo("drcom4scut.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
        }

        private void GetCoreVersion()
        {
            Process process = BuildCoreProcess("--version");
            Regex versionRegex = new(@"drcom4scut\s*(\d\S*)");
            try
            {
                process.Start();
                version = versionRegex.Match(process.StandardOutput.ReadToEnd()).Groups[1].Value;
                process.WaitForExit();
            }
            catch (Exception) { }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            GetCoreVersion();
            KillCoreProcess();
            if (version.Length == 0)
            {
                this.IsEnabled = false;
                MessageBox.Show("未找到核心程序 drcom4scut.exe，无法使用！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown(0);
                return;
            }
            else
            {
                this.Title = $"drcom4scutGUI ({version})";
            }
            LoadConfig();
            InitTray();
            InitUI();
            if (current != null && config.Auto)
            {
                StartCore();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            this.notifyIcon?.Visible = false;
            KillCoreProcessOwned();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
            }
        }

        private void SetUIEnabled(bool enable)
        {
            this.loginButton.Content = enable ? "认证" : "停止";
            this.loginButton.Background = enable ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.OrangeRed;
            this.macComboBox.IsEnabled = enable;
            this.nameComboBox.IsEnabled = enable;
            this.deleteButton.IsEnabled = enable && current != null;
            this.passwordTextBox.IsEnabled = enable;
            this.ipTextBox.IsEnabled = enable;
            this.autoCheckBox.IsEnabled = enable;
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            if (thread == null) StartCore();
            else StopCore();
        }

        private delegate void NoArgDelegate();

        private void KillCoreProcessOwned()
        {
            try { process?.Kill(); }
            catch (Exception) { }
            process = null;
            try { thread?.Abort(); }
            catch (Exception) { }
            thread = null;
        }

        private void StopCore()
        {
            this.label.Text = "正在停止...";
            KillCoreProcessOwned();
            this.label.Text = "已停止！";
            this.success = false;
            SetUIEnabled(true);
            this.ShowAndActive();
        }

        private void StartCore()
        {
            if (this.nameComboBox.Text.Trim().Length == 0 || this.passwordTextBox.Password.Length == 0)
            {
                MessageBox.Show("请输入账号和密码！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            string ip = this.ipTextBox.Text.Trim();
            if (ip.Length > 0 && !IPAddress.TryParse(ip, out IPAddress ipaddress))
            {
                MessageBox.Show("IP地址格式不正确，不需要指定时请留空", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            SaveInput(true);
            SetUIEnabled(false);
            this.label.Text = "正在认证...";

            StringBuilder arguments = new();
            if (!string.IsNullOrEmpty(config.Mac))
            {
                arguments.Append($" --mac \"{config.Mac}\"");
            }
            if (!string.IsNullOrEmpty(current.IP))
            {
                arguments.Append($" --ip \"{current.IP}\"");
            }
            arguments.Append($" --username \"{current.Name}\" --password \"{current.Password}\"");

            thread = new Thread(new ThreadStart(() =>
            {
                process = BuildCoreProcess(arguments.ToString());

                Regex errorRegex = new(@"\[.*?\]\[ERROR\]\[.*?\]\s?(.+)");
                StringBuilder message = new();
                process.OutputDataReceived += (sender, args) =>
                {
                    string data = args.Data;
                    if (data == null) return;
                    if (data.Contains("panic"))
                    {
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(() =>
                        {
                            StopCore();
                            this.label.Text = data;
                            MessageBox.Show(data, "崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
                        }));
                        return;
                    }
                    if (data.Contains("802.1X Authorization success!"))
                    {
                        this.success = true;
                        message.Clear();
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(() =>
                        {
                            this.label.Text = "认证成功！";
                            this.WindowState = WindowState.Minimized;
                        }));
                    }

                    string errorMsg = errorRegex.Match(data).Groups[1].Value;
                    if (errorMsg.Length == 0) return;

                    if (this.success)
                    {
                        if (errorMsg.Contains("ignored"))
                            return;

                        this.Dispatcher.BeginInvoke(new NoArgDelegate(() =>
                        {
                            this.label.Text = errorMsg.Contains("Will try reconnect at the next")
                                ? errorMsg : "出现错误，但是保持核心程序运行。";
                            ShowAndActive();
                        }));
                        return;
                    }

                    message.Append(errorMsg).Append('\n');
                    if (errorMsg.Contains("reconnect"))
                    {
                        this.Dispatcher.BeginInvoke(new NoArgDelegate(() =>
                        {
                            StopCore();
                            string text = "认证失败，已停止！\n" + message.ToString().TrimEnd();
                            this.label.Text = text;
                            MessageBox.Show(text, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        }));
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.WaitForExit();
                this.Dispatcher.BeginInvoke(new NoArgDelegate(StopCore));
            }));
            thread.Start();
        }
    }
}
