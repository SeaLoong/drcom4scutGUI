using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace drcom4scutGUI;

public sealed class MainWindow : Window
{
    private readonly bool skipBackend;
    private readonly ConfigurationStore store;
    private readonly CoreProcessSession core;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DiagnosticsBuffer diagnostics = new();
    private Configuration config;
    private Account? current;
    private TrayIcon? tray;
    private CancellationTokenSource? authentication;
    private Task<int>? sessionTask;
    private bool loading;
    private bool closing;
    private bool closeAllowed;
    private bool authenticated;
    private bool canHideToTray;
    private int sessionGeneration;
    private string coreVersion = "";
    private static string GuiVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private readonly ComboBox modeComboBox = new() { Name = "modeComboBox", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox macComboBox = new() { Name = "macComboBox", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AutoCompleteBox nameComboBox = new() { Name = "nameComboBox", FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0 };
    private readonly TextBox passwordTextBox = new() { Name = "passwordTextBox", PasswordChar = '●' };
    private readonly CheckBox rememberCheckBox = new() { Name = "rememberCheckBox", Content = "记住密码" };
    private readonly TextBox ipTextBox = new() { Name = "ipTextBox", PlaceholderText = "自动选择时留空" };
    private readonly StackPanel portalPanel = new() { Name = "portalPanel", Spacing = 7 };
    private readonly TextBox portalBaseTextBox = new() { Name = "portalBaseTextBox", PlaceholderText = "https://校园门户地址" };
    private readonly TextBox portalApiTextBox = new() { Name = "portalApiTextBox", PlaceholderText = "可选，同一可信主机" };
    private readonly TextBox portalAcIpTextBox = new() { Name = "portalAcIpTextBox", PlaceholderText = "可选" };
    private readonly TextBox portalClientIpTextBox = new() { Name = "portalClientIpTextBox", PlaceholderText = "可选，不改变实际网络出口" };
    private readonly CheckBox allowHttpCheckBox = new() { Name = "allowHttpCheckBox", Content = "允许 HTTP 门户明文网络传输" };
    private readonly CheckBox autoCheckBox = new() { Name = "autoCheckBox", Content = "打开 GUI 后自动认证" };
    private readonly CheckBox startupCheckBox = new() { Name = "startupCheckBox", Content = "登录系统时启动" };
    private readonly Button loginButton = new() { Name = "loginButton", Content = "认证", MinWidth = 86, IsDefault = true };
    private readonly Button deleteButton = new() { Name = "deleteButton", Content = "删除", IsEnabled = false };
    private readonly Button doctorButton = new() { Name = "doctorButton", Content = "环境自检" };
    private readonly TextBlock label = new() { Name = "statusLabel", Text = "正在检查核心和账号配置…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox logTextBox = new() { Name = "logTextBox", IsReadOnly = true, AcceptsReturn = true, Height = 150, TextWrapping = TextWrapping.Wrap };

    private sealed record Choice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    /// <summary>skipBackend is used by native rendering probes and headless UI tests.</summary>
    public MainWindow(bool skipBackend = false, Configuration? initialConfiguration = null)
    {
        this.skipBackend = skipBackend;
        config = initialConfiguration ?? new Configuration();
        string directory = PlatformPaths.ConfigurationDirectory;
        string legacy = Path.Combine(AppContext.BaseDirectory, "drcom4scutGUI.json");
        if (!File.Exists(legacy)) legacy = Path.Combine(Environment.CurrentDirectory, "gui.json");
        store = new ConfigurationStore(Path.Combine(directory, "config.json"),
            skipBackend ? new DisabledProtector() : SecretProtectorFactory.Create(), legacy);
        core = new CoreProcessSession(PlatformPaths.CoreExecutablePath);
        Title = $"drcom4scutGUI {GuiVersion}";
        Width = 560;
        Height = 740;
        MinWidth = 430;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (Stream stream = AssetLoader.Open(new Uri("avares://drcom4scutGUI/DrClient.ico"))) Icon = new WindowIcon(stream);
        BuildContent();
        nameComboBox.TextChanged += (_, _) => AccountTextChanged();
        ipTextBox.TextChanged += (_, _) => ValidateIp();
        modeComboBox.SelectionChanged += (_, _) => { UpdateModeUI(); SavePreferences(); };
        rememberCheckBox.IsCheckedChanged += (_, _) => SavePreferences();
        autoCheckBox.IsCheckedChanged += (_, _) => SavePreferences();
        allowHttpCheckBox.IsCheckedChanged += (_, _) => SavePreferences();
        startupCheckBox.IsCheckedChanged += (_, _) => StartupChanged();
        loginButton.Click += async (_, _) => { if (sessionTask is null) await StartCoreAsync(); else await StopCoreAsync(); };
        deleteButton.Click += async (_, _) => await DeleteAccountAsync();
        doctorButton.Click += async (_, _) => await DoctorAsync();
        Opened += async (_, _) => await InitializeAsync();
        Closing += WindowClosing;
        if (skipBackend) InitUI();
    }

    private void BuildContent()
    {
        StackPanel fields = new() { Spacing = 9, Margin = new Thickness(20, 16) };
        fields.Children.Add(Row("模式", modeComboBox));
        fields.Children.Add(Row("网卡", macComboBox));
        Grid accountRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 7 };
        accountRow.Children.Add(nameComboBox);
        Grid.SetColumn(deleteButton, 1);
        accountRow.Children.Add(deleteButton);
        fields.Children.Add(Row("账号", accountRow));
        fields.Children.Add(Row("密码", passwordTextBox));
        fields.Children.Add(Row("", rememberCheckBox));
        fields.Children.Add(Row("本机源 IP", ipTextBox));
        portalPanel.Children.Add(new TextBlock { Text = "无线门户", FontWeight = FontWeight.SemiBold });
        portalPanel.Children.Add(Row("门户地址", portalBaseTextBox));
        portalPanel.Children.Add(Row("API 地址", portalApiTextBox));
        portalPanel.Children.Add(Row("AC IPv4", portalAcIpTextBox));
        portalPanel.Children.Add(Row("载荷 IPv4", portalClientIpTextBox));
        portalPanel.Children.Add(allowHttpCheckBox);
        portalPanel.Children.Add(new TextBlock
        {
            Text = "HTTP 会将账号密码明文发送到远端门户。仅在确认校园门户必须使用 HTTP 且接受此风险时开启；不会自动回退 HTTP。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick
        });
        fields.Children.Add(portalPanel);
        StackPanel preferences = new() { Spacing = 3 };
        preferences.Children.Add(autoCheckBox);
        preferences.Children.Add(startupCheckBox);
        Grid controls = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 5) };
        controls.Children.Add(preferences);
        Grid.SetColumn(loginButton, 1);
        controls.Children.Add(loginButton);
        fields.Children.Add(controls);
        fields.Children.Add(new Border { Child = label, Padding = new Thickness(9), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, CornerRadius = new CornerRadius(4) });
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(doctorButton);
        Button export = new() { Name = "exportButton", Content = "导出诊断" };
        export.Click += async (_, _) => await ExportAsync();
        tools.Children.Add(export);
        fields.Children.Add(tools);
        fields.Children.Add(logTextBox);
        Content = new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static Grid Row(string caption, Control input)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("86,*"), ColumnSpacing = 8 };
        grid.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return grid;
    }

    private async Task InitializeAsync()
    {
        if (skipBackend) { label.Text = "界面测试：不会读取账号或启动认证。"; return; }
        loginButton.IsEnabled = false;
        try
        {
            if (Screens.Primary is { } screen) MaxHeight = Math.Max(420, screen.WorkingArea.Height / screen.Scaling - 35);
            try { config = store.Load(); }
            catch (Exception error)
            {
                config = new Configuration();
                await MessageAsync(error.Message + "\n原文件已保留。重新保存前请确认账号设置。\n" + store.FilePath, "账号配置未能读取");
            }
            InitUI();
            InitTray();
            coreVersion = await core.VerifyCoreAsync(lifetime.Token);
            Title = $"drcom4scutGUI {GuiVersion} — {coreVersion}";
            label.Text = "已就绪。密码通过私有输入传给核心。" + SecretProtectorFactory.Description;
            loginButton.IsEnabled = true;
            if (current is { Password.Length: > 0 } && config.Auto) await StartCoreAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            label.Text = error.Message;
            if (!closing) await MessageAsync(error.Message + "\n配置路径：" + store.FilePath, "初始化失败");
        }
    }

    private void InitUI()
    {
        loading = true;
        modeComboBox.ItemsSource = new[] { new Choice("有线 EAP + UDP", "eap"), new Choice("有线仅 EAP", "eap-only"), new Choice("无线 Portal", "portal") };
        var choices = new System.Collections.Generic.List<Choice> { new("自动选择", "") };
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.Wireless80211)) continue;
            byte[] address = adapter.GetPhysicalAddress().GetAddressBytes();
            if (address.Length != 6) continue;
            string mac = BitConverter.ToString(address).Replace('-', ':');
            choices.Add(new Choice($"{adapter.Name} ({mac})", mac));
        }
        if (choices.All(choice => choice.Value != config.Mac)) choices.Add(new Choice(config.Mac, config.Mac));
        macComboBox.ItemsSource = choices;
        macComboBox.SelectedItem = choices.First(choice => choice.Value == config.Mac);
        nameComboBox.ItemsSource = config.Accounts.Select(account => account.Name).ToArray();
        current = config.Find(config.Account);
        nameComboBox.Text = current?.Name ?? "";
        ShowAccount(current);
        autoCheckBox.IsChecked = config.Auto;
        string mode = config.Mode == "portal" ? "portal" : config.NoUdp ? "eap-only" : "eap";
        modeComboBox.SelectedItem = modeComboBox.Items.OfType<Choice>().Single(choice => choice.Value == mode);
        portalBaseTextBox.Text = config.PortalBase;
        portalApiTextBox.Text = config.PortalApi;
        portalAcIpTextBox.Text = config.PortalAcIp;
        portalClientIpTextBox.Text = config.PortalClientIp;
        allowHttpCheckBox.IsChecked = config.AllowHttp;
        UpdateModeUI();
        startupCheckBox.IsChecked = !skipBackend && DesktopStartup.IsEnabled();
        loading = false;
    }

    private void InitTray()
    {
        NativeMenu menu = new();
        NativeMenuItem open = new("打开");
        open.Click += (_, _) => ShowAndActivate();
        NativeMenuItem stop = new("停止认证");
        stop.Click += async (_, _) => await StopCoreAsync();
        NativeMenuItem exit = new("退出");
        exit.Click += (_, _) => Close();
        menu.Items.Add(open);
        menu.Items.Add(stop);
        menu.Items.Add(exit);
        tray = new TrayIcon { Icon = Icon, ToolTipText = $"drcom4scutGUI {GuiVersion}", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowAndActivate();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
        // Linux desktops can omit the status-notifier host while still exposing
        // a tray implementation. Keep the normal window reachable there.
        canHideToTray = !OperatingSystem.IsLinux() && tray.NativeMenuExporter is not null;
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
        passwordTextBox.Text = account?.Password ?? "";
        ipTextBox.Text = account?.IP ?? "";
        rememberCheckBox.IsChecked = account?.RememberPassword ?? true;
    }

    private void AccountTextChanged()
    {
        if (loading) return;
        Account? account = config.Find((nameComboBox.Text ?? "").Trim());
        if (account == current) return;
        if (current is not null)
        {
            current.Password = passwordTextBox.Text ?? "";
            current.RememberPassword = rememberCheckBox.IsChecked == true;
            if (ValidIp(ipTextBox.Text ?? "")) current.IP = (ipTextBox.Text ?? "").Trim();
        }
        loading = true;
        ShowAccount(account);
        loading = false;
    }

    private bool SaveInput(bool all = false)
    {
        string username = (nameComboBox.Text ?? "").Trim();
        string password = passwordTextBox.Text ?? "";
        string ip = (ipTextBox.Text ?? "").Trim();
        if (!ValidIp(ip)) ip = current?.IP ?? "";
        if (current is null && username.Length > 0 && password.Length > 0)
        {
            current = new Account { Name = username, Password = password, IP = ip, RememberPassword = rememberCheckBox.IsChecked == true };
            config.Accounts.Add(current);
            config.Accounts.Sort();
            loading = true;
            nameComboBox.ItemsSource = config.Accounts.Select(account => account.Name).ToArray();
            nameComboBox.Text = username;
            loading = false;
        }
        else if (current is not null)
        {
            current.Password = password;
            current.IP = ip;
            current.RememberPassword = rememberCheckBox.IsChecked == true;
        }
        if (all)
        {
            config.Account = current?.Name ?? "";
            config.Mac = (macComboBox.SelectedItem as Choice)?.Value ?? "";
            config.Auto = autoCheckBox.IsChecked == true;
            string selected = (modeComboBox.SelectedItem as Choice)?.Value ?? "eap";
            config.Mode = selected == "portal" ? "portal" : "eap";
            config.NoUdp = selected == "eap-only";
            config.PortalBase = (portalBaseTextBox.Text ?? "").Trim();
            config.PortalApi = (portalApiTextBox.Text ?? "").Trim();
            config.PortalAcIp = (portalAcIpTextBox.Text ?? "").Trim();
            config.PortalClientIp = (portalClientIpTextBox.Text ?? "").Trim();
            config.AllowHttp = allowHttpCheckBox.IsChecked == true;
        }
        if (skipBackend) return true;
        try { store.Save(config); return true; }
        catch (Exception error)
        {
            label.Text = "配置未能安全保存。可取消“记住密码”后继续本次认证。";
            _ = MessageAsync(label.Text + "\n" + error.Message, "保存失败");
            return false;
        }
    }

    private async Task DeleteAccountAsync()
    {
        if (current is null || !await ConfirmAsync($"删除账号 {current.Name}？", "删除账号")) return;
        loading = true;
        config.Accounts.Remove(current);
        if (config.Account == current.Name) config.Account = "";
        nameComboBox.ItemsSource = config.Accounts.Select(account => account.Name).ToArray();
        nameComboBox.Text = "";
        ShowAccount(null);
        loading = false;
        SaveInput();
    }

    private static bool ValidIp(string text) => text.Trim().Length == 0 || IPAddress.TryParse(text.Trim(), out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    private void ValidateIp()
    {
        bool valid = ValidIp(ipTextBox.Text ?? "");
        if (valid) ipTextBox.ClearValue(BackgroundProperty);
        else ipTextBox.Background = Brushes.MistyRose;
        ToolTip.SetTip(ipTextBox, valid ? null : "请输入 IPv4 地址，不指定时留空。");
    }

    private async Task StartCoreAsync()
    {
        if (skipBackend || sessionTask is not null || closing) return;
        string username = (nameComboBox.Text ?? "").Trim();
        string password = passwordTextBox.Text ?? "";
        string ip = (ipTextBox.Text ?? "").Trim();
        if (password.IndexOfAny(['\r', '\n', '\0']) >= 0)
        { await MessageAsync("密码不能包含换行或 NUL 字符。", "密码格式不正确"); return; }
        if (username.Length == 0 || password.Length == 0)
        { await MessageAsync("请输入账号和密码。", "输入不完整"); return; }
        if (!ValidIp(ip))
        { await MessageAsync("请输入 IPv4 地址，不指定时留空。", "地址不正确"); return; }
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
            label.Text = $"核心程序已退出（代码 {code}）。可使用“环境自检”查看环境。";
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
        Dispatcher.UIThread.Post(() =>
        {
            if (closing || generation != sessionGeneration || sessionTask is null) return;
            logTextBox.Text = diagnostics.Export(current?.Name ?? "", current?.Password ?? "");
            logTextBox.CaretIndex = logTextBox.Text.Length;
        });
    }

    private void ReceiveStatus(CoreConnectionStatus state, string mode, bool eapOnly, int generation) => Dispatcher.UIThread.Post(() =>
    {
        if (closing || generation != sessionGeneration || sessionTask is null || authentication?.IsCancellationRequested == true) return;
        ApplyConnectionStatus(state, mode, eapOnly);
    });

    internal void ApplyConnectionStatus(CoreConnectionStatus state, string mode, bool eapOnly)
    {
        if (state.IsOnline)
        {
            label.Text = mode == "portal" ? "无线门户已确认在线。" : eapOnly ? "802.1X 认证成功（仅 EAP）。" : "认证和 UDP 心跳均成功。";
            if (!authenticated)
            {
                authenticated = true;
                if (canHideToTray) Hide();
            }
        }
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
        macComboBox.IsEnabled = enabled;
        nameComboBox.IsEnabled = enabled;
        passwordTextBox.IsEnabled = enabled;
        rememberCheckBox.IsEnabled = enabled;
        ipTextBox.IsEnabled = enabled;
        autoCheckBox.IsEnabled = enabled;
        modeComboBox.IsEnabled = enabled;
        portalPanel.IsEnabled = enabled;
        deleteButton.IsEnabled = enabled && current is not null;
    }

    private async Task DoctorAsync()
    {
        if (skipBackend) return;
        doctorButton.IsEnabled = false;
        try
        {
            CoreDoctorReport result = await core.DoctorAsync(lifetime.Token);
            string summary = result.PrerequisitesMet ? "自检通过。" : "自检已完成，部分运行条件尚未满足。";
            diagnostics.Add(summary);
            foreach (string line in result.Report.Split('\n')) diagnostics.Add(line.TrimEnd('\r'));
            logTextBox.Text = diagnostics.Export(current?.Name ?? "", current?.Password ?? "");
            await MessageAsync(summary + Environment.NewLine + result.Report, "环境自检");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!closing) await MessageAsync(error.Message, "自检失败"); }
        finally { doctorButton.IsEnabled = true; }
    }

    private async Task ExportAsync()
    {
        if (skipBackend) return;
        try
        {
            IStorageFile? selected = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "drcom4scut-diagnostics.txt",
                FileTypeChoices = [new FilePickerFileType("文本文件") { Patterns = ["*.txt"] }]
            });
            if (selected is null) return;
            await using Stream stream = await selected.OpenWriteAsync();
            stream.SetLength(0);
            await using StreamWriter writer = new(stream);
            await writer.WriteAsync($"GUI {GuiVersion} / " + coreVersion + Environment.NewLine + diagnostics.Export(current?.Name ?? "", current?.Password ?? ""));
        }
        catch (Exception error) { await MessageAsync(error.Message, "导出失败"); }
    }

    private void UpdateModeUI() => portalPanel.IsVisible = (modeComboBox.SelectedItem as Choice)?.Value == "portal";

    private void SavePreferences()
    {
        if (!loading && !closing) SaveInput(all: true);
    }

    private void StartupChanged()
    {
        if (loading || skipBackend) return;
        try { DesktopStartup.SetEnabled(startupCheckBox.IsChecked == true); }
        catch (Exception error)
        {
            loading = true;
            startupCheckBox.IsChecked = DesktopStartup.IsEnabled();
            loading = false;
            _ = MessageAsync(error.Message, "无法修改当前用户的登录启动设置");
        }
    }

    private async Task MessageAsync(string message, string title) => await DialogAsync(message, title, confirm: false);
    private Task<bool> ConfirmAsync(string message, string title) => DialogAsync(message, title, confirm: true);

    private async Task<bool> DialogAsync(string message, string title, bool confirm)
    {
        if (closing || !IsVisible) { label.Text = message; return false; }
        Window dialog = new() { Title = title, Width = 450, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        StackPanel content = new() { Spacing = 15, Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        Button ok = new() { Content = confirm ? "确定" : "关闭", IsDefault = true };
        ok.Click += (_, _) => dialog.Close(true);
        actions.Children.Add(ok);
        if (confirm)
        {
            Button cancel = new() { Content = "取消", IsCancel = true };
            cancel.Click += (_, _) => dialog.Close(false);
            actions.Children.Add(cancel);
        }
        content.Children.Add(actions);
        dialog.Content = content;
        return await dialog.ShowDialog<bool>(this);
    }

    private async void WindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closeAllowed || skipBackend) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        lifetime.Cancel();
        await StopCoreAsync();
        tray?.Dispose();
        lifetime.Dispose();
        closeAllowed = true;
        Close();
    }

    private sealed class DisabledProtector : ISecretProtector
    {
        public string Protect(string plaintext) => throw new InvalidOperationException("UI probes must not persist credentials");
        public string Unprotect(string ciphertext) => throw new InvalidOperationException("UI probes must not read credentials");
    }
}
