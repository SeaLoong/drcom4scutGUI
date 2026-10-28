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

public sealed partial class MainWindow : Window
{
    private readonly bool skipBackend;
    private readonly ConfigurationStore store;
    private readonly CoreProcessSession core;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DiagnosticsBuffer diagnostics = new();
    private readonly UpdateController updates;
    private readonly Func<string, string, bool, Task<bool>>? updateDialog;
    private bool reconnectAfterCoreUpdate;
    private bool installingUpdate;
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
    private int logRefreshPending;
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
    private readonly CheckBox allowHttpCheckBox = new() { Name = "allowHttpCheckBox", Content = "登录页使用 HTTP（明文传输）" };
    private readonly CheckBox autoCheckBox = new() { Name = "autoCheckBox", Content = "打开后自动连接" };
    private readonly CheckBox startupCheckBox = new() { Name = "startupCheckBox", Content = "登录系统时启动" };
    private readonly Button loginButton = new() { Name = "loginButton", Content = "连接", MinWidth = 86, IsDefault = true };
    private readonly Button deleteButton = new() { Name = "deleteButton", Content = "删除", IsEnabled = false };
    private readonly Button doctorButton = new() { Name = "doctorButton", Content = "环境自检" };
    private readonly Button checkUpdateButton = new() { Name = "checkUpdateButton", Content = "检查更新" };
    private readonly Button checkCoreUpdateButton = new() { Name = "checkCoreUpdateButton", Content = "检查核心更新" };
    private readonly StackPanel updatePanel = new() { Name = "updatePanel", Spacing = 6, IsVisible = false };
    private readonly Button guiUpdateNotice = new() { Name = "guiUpdateNotice", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Button coreUpdateNotice = new() { Name = "coreUpdateNotice", IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly TextBlock updateProgress = new() { Name = "updateProgress", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock label = new() { Name = "statusLabel", Text = "正在检查核心和账号配置…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox logTextBox = new() { Name = "logTextBox", IsReadOnly = true, AcceptsReturn = true, Height = 150, TextWrapping = TextWrapping.Wrap };

    private sealed record Choice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    /// <summary>skipBackend is used by native rendering probes and headless UI tests.</summary>
    public MainWindow(bool skipBackend = false, Configuration? initialConfiguration = null)
        : this(skipBackend, initialConfiguration, null, null, null) { }

    internal MainWindow(bool skipBackend, Configuration? initialConfiguration, IUpdateClient? updateClient,
        TimeProvider? timeProvider, Func<string, string, bool, Task<bool>>? updateDialog, ConfigurationStore? configurationStore = null)
    {
        this.skipBackend = skipBackend;
        this.updateDialog = updateDialog;
        config = initialConfiguration ?? new Configuration();
        string directory = PlatformPaths.ConfigurationDirectory;
        string legacy = Path.Combine(AppContext.BaseDirectory, "drcom4scutGUI.json");
        if (!File.Exists(legacy)) legacy = Path.Combine(Environment.CurrentDirectory, "gui.json");
        store = configurationStore ?? new ConfigurationStore(Path.Combine(directory, "config.json"),
            skipBackend ? new DisabledProtector() : SecretProtectorFactory.Create(), legacy);
        core = new CoreProcessSession(PlatformPaths.CoreExecutablePath);
        updates = new UpdateController(updateClient ?? (skipBackend ? new PreviewUpdateClient() : new DesktopUpdateClient(
            component => component == UpdateComponent.Gui ? GuiVersion : coreVersion)), timeProvider ?? TimeProvider.System,
            ConfirmUpdateAsync, MessageUpdateAsync, BeforeUpdateInstallAsync, AfterUpdateInstallAsync);
        updates.Changed += () =>
        {
            if (Dispatcher.UIThread.CheckAccess()) RefreshUpdateUI();
            else Dispatcher.UIThread.Post(RefreshUpdateUI);
        };
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
        checkUpdateButton.Click += async (_, _) => await updates.CheckAsync(UpdateComponent.Gui, manual: true);
        checkCoreUpdateButton.Click += async (_, _) => await updates.CheckAsync(UpdateComponent.Core, manual: true);
        guiUpdateNotice.Click += async (_, _) => await updates.InstallAsync(UpdateComponent.Gui);
        coreUpdateNotice.Click += async (_, _) => await updates.InstallAsync(UpdateComponent.Core);
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
        fields.Children.Add(Row("本机 IP（可选）", ipTextBox));
        portalPanel.Children.Add(new TextBlock { Text = "校园网页登录", FontWeight = FontWeight.SemiBold });
        portalPanel.Children.Add(Row("登录页地址", portalBaseTextBox));
        StackPanel advancedPortal = new() { Spacing = 7 };
        advancedPortal.Children.Add(Row("API 地址", portalApiTextBox));
        advancedPortal.Children.Add(Row("AC IPv4", portalAcIpTextBox));
        advancedPortal.Children.Add(Row("载荷 IPv4", portalClientIpTextBox));
        portalPanel.Children.Add(new Expander
        {
            Name = "advancedPortalExpander", Header = "更多网页设置（通常留空）",
            Content = advancedPortal, HorizontalAlignment = HorizontalAlignment.Stretch
        });
        portalPanel.Children.Add(allowHttpCheckBox);
        portalPanel.Children.Add(new TextBlock
        {
            Text = "先在浏览器中打开校园登录页，再填写该页地址。HTTPS 可加密传输；学校提供 HTTP 登录页时，勾选上面的选项后连接，账号密码会以明文传输。",
            TextWrapping = TextWrapping.Wrap
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
        updatePanel.Children.Add(guiUpdateNotice);
        updatePanel.Children.Add(coreUpdateNotice);
        updatePanel.Children.Add(updateProgress);
        fields.Children.Add(updatePanel);
        fields.Children.Add(new Border { Child = label, Padding = new Thickness(9), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, CornerRadius = new CornerRadius(4) });
        StackPanel tools = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(doctorButton);
        Button export = new() { Name = "exportButton", Content = "导出诊断" };
        export.Click += async (_, _) => await ExportAsync();
        tools.Children.Add(export);
        fields.Children.Add(tools);
        StackPanel updateTools = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        updateTools.Children.Add(checkUpdateButton);
        updateTools.Children.Add(checkCoreUpdateButton);
        fields.Children.Add(updateTools);
        fields.Children.Add(logTextBox);
        Content = new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private static Grid Row(string caption, Control input)
    {
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("110,*"), ColumnSpacing = 8 };
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
                await MessageAsync(error.Message + "\n原文件已保留。请解锁密码库或修复配置后重启；若要建立新配置，请先备份并移走原文件后重启。"
                    + "\n也可取消“记住密码”，仅在本次会话中临时连接，账号和连接选项不会保存。\n"
                    + (store.LoadFailurePath ?? store.FilePath), "账号配置未能读取");
            }
            InitUI();
            if (store.LoadWarning is { } warning) await MessageAsync(warning, "旧账号文件需要清理");
            InitTray();
            coreVersion = await core.VerifyCoreAsync(lifetime.Token);
            RefreshUpdateUI();
            label.Text = "准备好了。选择网络，填写账号和密码，然后点击“连接”。";
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
        modeComboBox.ItemsSource = new[] { new Choice("有线网络", "eap"), new Choice("有线网络（仅 802.1X）", "eap-only"), new Choice("校园网页认证", "portal") };
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
        NativeMenuItem stop = new("断开连接");
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

    internal bool SaveInput(bool all = false, bool allowSessionOnly = false)
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
        if (skipBackend && !store.HasLoadFailure) return true;
        try { store.Save(config); return true; }
        catch (Exception error)
        {
            if (store.HasLoadFailure)
            {
                bool sessionOnly = allowSessionOnly && rememberCheckBox.IsChecked != true;
                label.Text = sessionOnly ? "本次连接仅在内存中使用账号和连接选项，不会更改原配置。"
                    : "原账号配置尚未恢复，当前改动不会保存。可取消“记住密码”后临时连接；解锁或修复后请重启。";
                return sessionOnly;
            }
            label.Text = "配置未能安全保存，当前修改尚未写入。";
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
        if (skipBackend || sessionTask is not null || closing || installingUpdate) return;
        string username = (nameComboBox.Text ?? "").Trim();
        string password = passwordTextBox.Text ?? "";
        string ip = (ipTextBox.Text ?? "").Trim();
        if (password.IndexOfAny(['\r', '\n', '\0']) >= 0)
        { await MessageAsync("密码不能包含换行或 NUL 字符。", "密码格式不正确"); return; }
        if (username.Length == 0 || password.Length == 0)
        { await MessageAsync("请输入账号和密码。", "输入不完整"); return; }
        if (!ValidIp(ip))
        { await MessageAsync("请输入 IPv4 地址，不指定时留空。", "地址不正确"); return; }
        if (!SaveInput(all: true, allowSessionOnly: true)) return;
        authenticated = false;
        authentication = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetUIEnabled(false);
        label.Text = config.Mode == "portal" ? "正在登录校园网页…" : "正在连接校园网…";
        var options = new CoreLaunchOptions(username, password, config.Mac, ip,
            Path.Combine(Path.GetDirectoryName(store.FilePath)!, "core.yml"), config.NoUdp,
            config.Mode, config.PortalBase, config.PortalApi, config.PortalAcIp, config.PortalClientIp, config.AllowHttp);
        int generation = ++sessionGeneration;
        try
        {
            sessionTask = core.RunAsync(options, ReceiveLine, ReceiveLine, authentication.Token,
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
            if (!closing && !installingUpdate) SetUIEnabled(true);
        }
    }

    internal void ReceiveLine(string line)
    {
        // CoreProcessSession has already hidden both credentials together,
        // before any line reaches the UI or the retained diagnostics buffer.
        diagnostics.Add(line);
        if (Interlocked.Exchange(ref logRefreshPending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref logRefreshPending, 0);
            // A failed child can exit before this queued refresh runs. Its
            // final diagnostics remain useful after the session has ended.
            if (closing) return;
            logTextBox.Text = diagnostics.Export();
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
            // Online is emitted only after every stage of the selected mode succeeds.
            updates.ConnectionSucceeded();
            label.Text = eapOnly && mode == "eap" ? "网线认证已通过。" : "已连接，可以上网了。";
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
                CoreConnectionPhase.WaitingInterface => "正在等待网络就绪，请检查网线或 Wi-Fi 连接…",
                CoreConnectionPhase.EapAuthentication => "正在连接校园网…",
                CoreConnectionPhase.UdpAuthentication => "账号已通过验证，正在确认连接…",
                CoreConnectionPhase.PortalAuthentication => "正在登录校园网页…",
                _ => "连接中断，正在自动重连…"
            };
            if (authenticated) { authenticated = false; ShowAndActivate(); }
        }
    }

    internal async Task StopCoreAsync()
    {
        Task<int>? running = sessionTask;
        if (!closing && !installingUpdate) { label.Text = "正在注销并停止…"; loginButton.IsEnabled = false; }
        authentication?.Cancel();
        if (running is not null)
        {
            try { await running; }
            catch (OperationCanceledException) { }
            catch (Exception error) { diagnostics.Add(error.Message); }
        }
        if (!closing)
        {
            if (!installingUpdate) { label.Text = "已停止。"; loginButton.IsEnabled = true; }
            ShowAndActivate();
        }
    }

    private Task<bool> ConfirmUpdateAsync(UpdateComponent component, string prompt)
    {
        ShowAndActivate();
        string details = component == UpdateComponent.Gui
            ? "\n更新期间将断开连接并重启图形界面。未记住的密码需在重启后重新输入。"
            : "\n更新期间将短暂断开连接；更新完成后会恢复本次连接。";
        string title = component == UpdateComponent.Gui ? "更新图形界面" : "更新核心";
        return updateDialog is null ? ConfirmAsync(prompt + details, title) : updateDialog(prompt + details, title, true);
    }

    private async Task MessageUpdateAsync(string message, string title)
    {
        if (closing) return;
        ShowAndActivate();
        if (updateDialog is null) await MessageAsync(message, title);
        else await updateDialog(message, title, false);
    }

    private async Task BeforeUpdateInstallAsync(UpdateComponent component)
    {
        reconnectAfterCoreUpdate = component == UpdateComponent.Core && sessionTask is not null;
        installingUpdate = true;
        loginButton.IsEnabled = false;
        await StopCoreAsync();
        SetUIEnabled(false);
        loginButton.IsEnabled = false;
    }

    private async Task AfterUpdateInstallAsync(UpdateComponent component)
    {
        if (component == UpdateComponent.Gui)
        {
            Close();
            return;
        }
        try
        {
            if (!skipBackend) coreVersion = await core.VerifyCoreAsync(lifetime.Token);
            RefreshUpdateUI();
            label.Text = "核心已更新，可以连接。";
        }
        finally
        {
            installingUpdate = false;
            SetUIEnabled(true);
            loginButton.IsEnabled = true;
        }
        if (reconnectAfterCoreUpdate && !closing) _ = StartCoreAsync();
        reconnectAfterCoreUpdate = false;
    }

    private void RefreshUpdateUI()
    {
        if (closing) return;
        Title = $"drcom4scutGUI {GuiVersion}" + (coreVersion.Length == 0 ? "" : $" — {coreVersion}");
        bool guiAvailable = updates.Offers.TryGetValue(UpdateComponent.Gui, out UpdateOffer? guiOffer);
        bool coreAvailable = updates.Offers.TryGetValue(UpdateComponent.Core, out UpdateOffer? coreOffer);
        guiUpdateNotice.IsVisible = guiAvailable;
        coreUpdateNotice.IsVisible = coreAvailable;
        if (guiAvailable)
        {
            guiUpdateNotice.Content = $"图形界面 {guiOffer!.Version} 可更新，点击查看";
            Title += $" · GUI {guiOffer.Version} 可更新";
        }
        if (coreAvailable)
        {
            coreUpdateNotice.Content = $"核心 {coreOffer!.Version} 可更新，点击查看";
            Title += $" · 核心 {coreOffer.Version} 可更新";
        }
        updateProgress.Text = updates.ProgressText;
        updateProgress.IsVisible = updates.ProgressText is not null;
        updatePanel.IsVisible = guiAvailable || coreAvailable || updateProgress.IsVisible;
        checkUpdateButton.IsEnabled = !updates.IsBusy;
        checkCoreUpdateButton.IsEnabled = !updates.IsBusy;
        doctorButton.IsEnabled = !updates.IsBusy;
        guiUpdateNotice.IsEnabled = !updates.IsBusy;
        coreUpdateNotice.IsEnabled = !updates.IsBusy;
        if (!updates.IsBusy && installingUpdate)
        {
            // Failed installation keeps the existing password only in this process.
            installingUpdate = false;
            reconnectAfterCoreUpdate = false;
            SetUIEnabled(true);
            loginButton.IsEnabled = true;
        }
    }

    private void SetUIEnabled(bool enabled)
    {
        loginButton.Content = enabled ? "连接" : "断开";
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
            foreach (string line in result.Report.Split('\n'))
                diagnostics.Add(line.TrimEnd('\r'), nameComboBox.Text ?? "", passwordTextBox.Text ?? "");
            logTextBox.Text = diagnostics.Export();
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
            await writer.WriteAsync($"GUI {GuiVersion} / " + coreVersion + Environment.NewLine + diagnostics.Export());
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
        if (closeAllowed) return;
        if (skipBackend) { updates.Dispose(); lifetime.Cancel(); return; }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        updates.Dispose();
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
