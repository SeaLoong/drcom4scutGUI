using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using MessageBox = System.Windows.MessageBox;

namespace drcom4scutGUI;

public partial class MainWindow : Window
{
    private readonly ConfigurationStore store;
    private readonly CoreProcessSession core;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DiagnosticsBuffer diagnostics = new();
    private Configuration config = new();
    private Account? current;
    private NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private CancellationTokenSource? authentication;
    private Task<int>? sessionTask;
    private bool loading;
    private bool closing;
    private bool closeAllowed;
    private bool authenticated;
    private int sessionGeneration;
    private string coreVersion = "";
    private static string GuiVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public MainWindow()
    {
        InitializeComponent();
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "drcom4scutGUI");
        string legacy = Path.Combine(AppContext.BaseDirectory, "drcom4scutGUI.json");
        if (!File.Exists(legacy)) legacy = Path.Combine(Environment.CurrentDirectory, "gui.json");
        store = new ConfigurationStore(Path.Combine(directory, "config.json"), new DpapiSecretProtector(), legacy);
        core = new CoreProcessSession(Path.Combine(AppContext.BaseDirectory, "drcom4scut.exe"));
        DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ComboBox.TextProperty, typeof(System.Windows.Controls.ComboBox))
            .AddValueChanged(nameComboBox, NameComboBox_TextChanged);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
        loginButton.IsEnabled = false;
        try
        {
            try { config = store.Load(); }
            catch (Exception error)
            {
                config = new Configuration();
                MessageBox.Show(error.Message + "\n原文件已保留。重新输入账号并认证时，将用新的加密配置替换它。\n" + store.FilePath,
                    "账号配置未能读取", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            InitUI();
            InitTray();
            coreVersion = await core.VerifyCoreAsync(lifetime.Token);
            Title = $"drcom4scutGUI {GuiVersion} — {coreVersion}";
            if (tray is not null) tray.Text = $"drcom4scutGUI {GuiVersion}";
            label.Text = "已就绪。密码通过专用管道传递，保存的密码仅当前 Windows 用户可解密。";
            loginButton.IsEnabled = true;
            if (current is not null && config.Auto) await StartCoreAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            label.Text = error.Message;
            if (!closing) MessageBox.Show(error.Message + "\n配置路径：" + store.FilePath, "初始化失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InitUI()
    {
        loading = true;
        macComboBox.Items.Add(new ComboBoxItem { Content = "自动选择", Tag = "" });
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211)) continue;
            byte[] address = adapter.GetPhysicalAddress().GetAddressBytes();
            if (address.Length != 6) continue;
            string mac = BitConverter.ToString(address).Replace('-', ':');
            macComboBox.Items.Add(new ComboBoxItem { Content = $"{adapter.Name} ({mac})", Tag = mac, ToolTip = adapter.Description });
        }
        macComboBox.SelectedValue = config.Mac;
        if (macComboBox.SelectedIndex < 0)
            macComboBox.SelectedIndex = macComboBox.Items.Add(new ComboBoxItem { Content = config.Mac, Tag = config.Mac });
        foreach (Account account in config.Accounts) nameComboBox.Items.Add(account.Name);
        current = config.Find(config.Account);
        nameComboBox.Text = current?.Name ?? "";
        ShowAccount(current);
        autoCheckBox.IsChecked = config.Auto;
        modeComboBox.SelectedValue = config.Mode == "portal" ? "portal" : config.NoUdp ? "eap-only" : "eap";
        portalBaseTextBox.Text = config.PortalBase;
        portalApiTextBox.Text = config.PortalApi;
        portalAcIpTextBox.Text = config.PortalAcIp;
        portalClientIpTextBox.Text = config.PortalClientIp;
        allowHttpCheckBox.IsChecked = config.AllowHttp;
        UpdateModeUI();
        startupCheckBox.IsChecked = WindowsStartup.IsEnabled();
        loading = false;
    }

    private void InitTray()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add(new ToolStripMenuItem("打开", null, (_, _) => ShowAndActivate()));
        menu.Items.Add(new ToolStripMenuItem("停止认证", null, async (_, _) => await StopCoreAsync()));
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Close()));
        using (Stream stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/DrClient.ico")).Stream)
        using (System.Drawing.Icon source = new(stream)) trayIcon = (System.Drawing.Icon)source.Clone();
        tray = new NotifyIcon { ContextMenuStrip = menu, Icon = trayIcon, Text = $"drcom4scutGUI {GuiVersion}", Visible = true };
        tray.MouseClick += (_, args) => { if (args.Button == MouseButtons.Left) ShowAndActivate(); };
    }

    private void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowAccount(Account? account)
    {
        current = account;
        deleteButton.IsEnabled = account is not null;
        passwordTextBox.Password = account?.Password ?? "";
        ipTextBox.Text = account?.IP ?? "";
    }

    private void NameComboBox_TextChanged(object? sender, EventArgs e)
    {
        if (loading) return;
        Account? account = config.Find(nameComboBox.Text.Trim());
        if (account == current) return;
        if (current is not null) SaveInput();
        ShowAccount(account);
    }

    private bool SaveInput(bool all = false)
    {
        string username = nameComboBox.Text.Trim();
        string password = passwordTextBox.Password;
        string ip = ipTextBox.Text.Trim();
        if (ip.Length > 0 && (!IPAddress.TryParse(ip, out IPAddress? entered) || entered.AddressFamily != AddressFamily.InterNetwork)) ip = current?.IP ?? "";
        if (current is null && username.Length > 0 && password.Length > 0)
        {
            current = new Account { Name = username, Password = password, IP = ip };
            config.Accounts.Add(current);
            config.Accounts.Sort();
            if (!nameComboBox.Items.Contains(username)) nameComboBox.Items.Add(username);
        }
        else if (current is not null)
        {
            current.Password = password;
            current.IP = ip;
        }
        if (all)
        {
            config.Account = current?.Name ?? "";
            config.Mac = macComboBox.SelectedValue as string ?? "";
            config.Auto = autoCheckBox.IsChecked == true;
            string selected = modeComboBox.SelectedValue as string ?? "eap";
            config.Mode = selected == "portal" ? "portal" : "eap";
            config.NoUdp = selected == "eap-only";
            config.PortalBase = portalBaseTextBox.Text.Trim();
            config.PortalApi = portalApiTextBox.Text.Trim();
            config.PortalAcIp = portalAcIpTextBox.Text.Trim();
            config.PortalClientIp = portalClientIpTextBox.Text.Trim();
            config.AllowHttp = allowHttpCheckBox.IsChecked == true;
        }
        try { store.Save(config); return true; }
        catch (Exception error)
        {
            MessageBox.Show("配置未能安全保存，认证尚未启动。\n" + error.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (current is null || MessageBox.Show($"删除账号 {current.Name}？", "删除账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        loading = true;
        config.Accounts.Remove(current);
        nameComboBox.Items.Remove(current.Name);
        if (config.Account == current.Name) config.Account = "";
        nameComboBox.Text = "";
        ShowAccount(null);
        loading = false;
        SaveInput();
    }

    private void IpTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ipTextBox.Text.Trim();
        bool valid = text.Length == 0 || IPAddress.TryParse(text, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        if (valid) ipTextBox.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        else ipTextBox.Background = Brushes.MistyRose;
        ipTextBox.ToolTip = valid ? null : "请输入 IPv4 地址，不指定时留空。";
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (sessionTask is null) await StartCoreAsync();
        else await StopCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        if (sessionTask is not null || closing) return;
        string username = nameComboBox.Text.Trim();
        string password = passwordTextBox.Password;
        string ip = ipTextBox.Text.Trim();
        if (password.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            MessageBox.Show("密码不能包含换行或 NUL 字符。", "密码格式不正确", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (username.Length == 0 || password.Length == 0)
        {
            MessageBox.Show("请输入账号和密码。", "输入不完整", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ip.Length > 0 && (!IPAddress.TryParse(ip, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork))
        {
            MessageBox.Show("请输入 IPv4 地址，不指定时留空。", "地址不正确", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!SaveInput(all: true)) return;
        authenticated = false;
        authentication = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetUIEnabled(false);
        label.Text = config.Mode == "portal" ? "正在进行无线门户认证…" : "正在进行 802.1X 认证…";
        var options = new CoreLaunchOptions(username, password, config.Mac, ip,
            Path.Combine(Path.GetDirectoryName(store.FilePath)!, "core.yml"), config.NoUdp,
            config.Mode, config.PortalBase, config.PortalApi, config.PortalAcIp, config.PortalClientIp, config.AllowHttp);
        int generation = ++sessionGeneration;
        try
        {
            sessionTask = core.RunAsync(options, line => ReceiveLine(line, generation), line => ReceiveLine(line, generation), authentication.Token,
                state => ReceiveStatus(state, options.Mode, options.NoUdp, generation));
            int code = await sessionTask;
            label.Text = $"核心程序已退出（代码 {code}）。可使用“自检”查看环境。";
        }
        catch (OperationCanceledException) when (authentication.IsCancellationRequested) { label.Text = "已停止。"; }
        catch (Exception error) { label.Text = error.Message; ShowAndActivate(); }
        finally
        {
            sessionTask = null;
            authentication.Dispose();
            authentication = null;
            authenticated = false;
            if (!closing) SetUIEnabled(true);
        }
    }

    private void ReceiveLine(string line, int generation)
    {
        diagnostics.Add(line);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closing || generation != sessionGeneration || sessionTask is null) return;
            logTextBox.Text = diagnostics.Export(current?.Name ?? "", current?.Password ?? "");
            logTextBox.ScrollToEnd();
        }));
    }

    private void ReceiveStatus(CoreConnectionStatus state, string mode, bool eapOnly, int generation)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closing || generation != sessionGeneration || sessionTask is null) return;
            if (authentication?.IsCancellationRequested == true) return;
            if (state.IsOnline) MarkAuthenticated(mode, eapOnly);
            else
            {
                label.Text = state.Phase switch
                {
                    CoreConnectionPhase.WaitingInterface => "正在等待可用网卡和 IPv4 地址…",
                    CoreConnectionPhase.EapAuthentication => "正在进行 802.1X 认证…",
                    CoreConnectionPhase.UdpAuthentication => "802.1X 已通过，正在建立 UDP 会话…",
                    CoreConnectionPhase.PortalAuthentication => "正在进行无线门户认证…",
                    _ => "连接已断开，核心程序将按配置重试。详情见日志。"
                };
                if (authenticated) { authenticated = false; ShowAndActivate(); }
            }
        }));
    }

    private void MarkAuthenticated(string mode, bool eapOnly)
    {
        label.Text = mode == "portal" ? "无线门户已确认在线。" : eapOnly ? "802.1X 认证成功（仅 EAP）。" : "认证和 UDP 心跳均成功。";
        if (!authenticated) { authenticated = true; WindowState = WindowState.Minimized; }
    }

    private async Task StopCoreAsync()
    {
        Task<int>? running = sessionTask;
        if (!closing) { label.Text = "正在注销并停止…"; loginButton.IsEnabled = false; }
        authentication?.Cancel();
        if (running is not null)
        {
            try { await running; }
            catch (OperationCanceledException) { }
            catch (Exception error) { diagnostics.Add(error.Message); }
        }
        if (!closing) { label.Text = "已停止。"; loginButton.IsEnabled = true; ShowAndActivate(); }
    }

    private void SetUIEnabled(bool enabled)
    {
        loginButton.Content = enabled ? "认证" : "停止";
        loginButton.Background = enabled ? Brushes.ForestGreen : Brushes.OrangeRed;
        macComboBox.IsEnabled = enabled;
        nameComboBox.IsEnabled = enabled;
        passwordTextBox.IsEnabled = enabled;
        ipTextBox.IsEnabled = enabled;
        autoCheckBox.IsEnabled = enabled;
        modeComboBox.IsEnabled = enabled;
        portalPanel.IsEnabled = enabled;
        deleteButton.IsEnabled = enabled && current is not null;
    }

    private async void DoctorButton_Click(object sender, RoutedEventArgs e)
    {
        doctorButton.IsEnabled = false;
        try
        {
            CoreDoctorReport result = await core.DoctorAsync(lifetime.Token);
            string summary = result.PrerequisitesMet ? "自检通过。" : "自检已完成，部分运行条件尚未满足。";
            diagnostics.Add(summary);
            foreach (string line in result.Report.Split('\n')) diagnostics.Add(line.TrimEnd('\r'));
            logTextBox.Text = diagnostics.Export(current?.Name ?? "", current?.Password ?? "");
            MessageBox.Show(summary + Environment.NewLine + result.Report, "环境自检", MessageBoxButton.OK,
                result.PrerequisitesMet ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!closing) MessageBox.Show(error.Message, "自检失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { doctorButton.IsEnabled = true; }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { FileName = "drcom4scut-diagnostics.txt", Filter = "文本文件|*.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, $"GUI {GuiVersion} / " + coreVersion + Environment.NewLine + diagnostics.Export(current?.Name ?? "", current?.Password ?? "")); }
        catch (Exception error) { MessageBox.Show(error.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void UpdateModeUI()
    {
        portalPanel.Visibility = modeComboBox.SelectedValue as string == "portal" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModeUI();
        if (!loading && !closing) SaveInput(all: true);
    }

    private void Preferences_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading && !closing) SaveInput(all: true);
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        try { WindowsStartup.SetEnabled(startupCheckBox.IsChecked == true); }
        catch (Exception error)
        {
            loading = true;
            startupCheckBox.IsChecked = WindowsStartup.IsEnabled();
            loading = false;
            MessageBox.Show(error.Message, "无法修改当前用户的登录启动设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_StateChanged(object sender, EventArgs e) { if (WindowState == WindowState.Minimized) Hide(); }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeAllowed) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        lifetime.Cancel();
        await StopCoreAsync();
        tray?.Dispose();
        trayIcon?.Dispose();
        lifetime.Dispose();
        closeAllowed = true;
        Close();
    }
}
