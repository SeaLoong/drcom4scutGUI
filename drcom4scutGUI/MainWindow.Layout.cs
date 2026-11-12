using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace drcom4scutGUI;

public sealed partial class MainWindow
{
    private readonly DesktopPalette palette = new();
    private IBrush Ink => palette.Ink;
    private IBrush Muted => palette.Muted;
    private IBrush Accent => palette.Accent;
    private IBrush Line => palette.Line;
    private readonly TextBlock statusHeading = new() { Name = "statusHeading", Text = "连接校园网", FontSize = 23, FontWeight = FontWeight.Medium };
    private readonly TextBlock statusCaption = new() { Name = "statusCaption", Text = "尚未连接", FontSize = 12 };
    private readonly Ellipse statusDot = new() { Width = 7, Height = 7, Fill = Brush("#8297AC") };
    private readonly ToggleButton wiredModeButton = new() { Name = "wiredModeButton", Content = "有线网络", IsChecked = true };
    private readonly ToggleButton wirelessModeButton = new() { Name = "wirelessModeButton", Content = "校园 Wi-Fi" };
    private readonly Button moreButton = new() { Name = "moreButton", Content = "更多" };
    private readonly Button diagnosticsLink = new() { Name = "diagnosticsLink", Content = "连接遇到问题？", FontSize = 12 };
    private readonly StackPanel connectionPanel = new() { Name = "connectionPanel" };
    private readonly Border extraPanel = new() { Name = "extraPanel", IsVisible = false, Padding = new Thickness(0, 4) };
    private readonly StackPanel settingsView = new() { Name = "settingsView", Spacing = 18, IsVisible = false };
    private readonly StackPanel diagnosticsView = new() { Name = "diagnosticsView", Spacing = 14, IsVisible = false };
    private readonly StackPanel updatesView = new() { Name = "updatesView", Spacing = 14, IsVisible = false };
    private readonly StackPanel advancedPortalSettings = new() { Spacing = 12, IsVisible = false };
    private readonly TextBlock extraHeading = new() { Name = "extraHeading", FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock versionDetails = new() { Name = "versionDetails", FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock extraStatus = new() { Name = "extraStatus", FontSize = 12, Margin = new Thickness(0, 0, 0, 18) };
    private readonly Border campusArt = new() { Name = "campusArt", Height = 132, CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 21) };
    private readonly ScrollViewer mainScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private Grid? pageHeader;
    private Grid? statusRow;

    private static IBrush Brush(string color) => new ImmutableSolidColorBrush(Color.Parse(color));

    private void BuildContent()
    {
        Background = palette.Surface;
        Foreground = Ink;
        statusHeading.Foreground = extraHeading.Foreground = Ink;
        statusCaption.Foreground = versionDetails.Foreground = extraStatus.Foreground = Muted;
        FontFamily = new FontFamily(OperatingSystem.IsWindows() ? "Microsoft YaHei UI"
            : OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC");
        FontSize = 14;
        Resources["ContentControlThemeFontFamily"] = FontFamily;
        ApplyDesktopStyles();
        ActualThemeVariantChanged += (_, _) => palette.Apply(ActualThemeVariant == ThemeVariant.Dark);
        palette.Apply(ActualThemeVariant == ThemeVariant.Dark);
        Activated += (_, _) => ApplyMotionPreferences();
        Opened += (_, _) => UpdateCompactLayout();
        SizeChanged += (_, _) => UpdateCompactLayout();

        Grid shell = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(30, 15, 26, 12), ColumnSpacing = 12 };
        pageHeader = header;
        using var brandStream = AssetLoader.Open(new Uri("avares://drcom4scutGUI/Assets/scut-brand.png"));
        Bitmap brandBitmap = new(brandStream);
        Closed += (_, _) => brandBitmap.Dispose();
        Image brand = new()
        {
            Source = new CroppedBitmap(brandBitmap, new PixelRect(20, 194, 686, 167)),
            Height = 44,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(brand, "华南理工大学校徽与校名");
        header.Children.Add(new Border { Child = brand, Background = Brushes.White, Padding = new Thickness(5, 3),
            CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left });
        moreButton.Classes.Add("quiet");
        moreButton.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(moreButton, "更多选项：连接设置、诊断与日志、软件更新");
        Grid.SetColumn(moreButton, 1);
        header.Children.Add(moreButton);
        ConfigureMoreMenu();
        shell.Children.Add(header);

        StackPanel body = new() { Margin = new Thickness(30, 4, 30, 12), Spacing = 0 };
        body.Children.Add(connectionPanel);
        using var artStream = AssetLoader.Open(new Uri("avares://drcom4scutGUI/Assets/campus-courtyard.jpg"));
        Bitmap artBitmap = new(artStream);
        Closed += (_, _) => artBitmap.Dispose();
        Image artwork = new() { Source = artBitmap, Stretch = Stretch.UniformToFill };
        AutomationProperties.SetName(artwork, "树荫下的校园小路与行人，岭南校园主题插画");
        campusArt.Child = artwork;
        connectionPanel.Children.Add(campusArt);
        Grid hero = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 36, Margin = new Thickness(0, 0, 0, 19) };
        statusRow = hero;
        StackPanel statusLine = new() { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        statusLine.Children.Add(statusDot);
        statusLine.Children.Add(statusCaption);
        statusHeading.VerticalAlignment = VerticalAlignment.Center;
        hero.Children.Add(statusHeading);
        Grid.SetColumn(statusLine, 1);
        hero.Children.Add(statusLine);
        connectionPanel.Children.Add(hero);

        Grid modes = new() { Name = "networkModes", ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6 };
        foreach (ToggleButton button in new[] { wiredModeButton, wirelessModeButton })
        {
            button.Classes.Add("mode");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.MinHeight = 38;
            button.Resources["ToggleButtonBackgroundChecked"] = palette.Paper;
            button.Resources["ToggleButtonForegroundChecked"] = Accent;
            button.Resources["ToggleButtonBackgroundCheckedPointerOver"] = palette.Hover;
            button.Resources["ToggleButtonForegroundCheckedPointerOver"] = Ink;
            button.Resources["ToggleButtonBackgroundCheckedPressed"] = palette.Pressed;
            button.Resources["ToggleButtonForegroundCheckedPressed"] = Ink;
        }
        modes.Children.Add(wiredModeButton);
        Grid.SetColumn(wirelessModeButton, 1);
        modes.Children.Add(wirelessModeButton);
        connectionPanel.Children.Add(new Border { Background = palette.Soft, CornerRadius = new CornerRadius(6), Padding = new Thickness(3), Child = modes, Margin = new Thickness(0, 0, 0, 19) });
        wiredModeButton.Click += (_, _) =>
        {
            if (modeComboBox.SelectedIndex == 2) modeComboBox.SelectedIndex = 0;
            UpdateModeUI();
        };
        wirelessModeButton.Click += (_, _) => { modeComboBox.SelectedIndex = 2; UpdateModeUI(); };

        nameComboBox.PlaceholderText = "输入或选择校园账号";
        nameComboBox.MinHeight = 45;
        AutomationProperties.SetName(nameComboBox, "校园账号");
        passwordTextBox.PlaceholderText = "校园网密码";
        AutomationProperties.SetName(passwordTextBox, "校园网密码");
        connectionPanel.Children.Add(Field("校园账号", nameComboBox));
        connectionPanel.Children.Add(Field("密码", passwordTextBox, new Thickness(0, 16, 0, 0)));
        rememberCheckBox.FontSize = 12;
        rememberCheckBox.Foreground = Muted;
        rememberCheckBox.Margin = new Thickness(0, 9, 0, 12);
        connectionPanel.Children.Add(rememberCheckBox);
        portalPanel.Children.Add(Field("校园登录页", portalBaseTextBox));
        portalPanel.Children.Add(Note("先连接校园 Wi-Fi，再填写学校提供的登录页地址。"));
        portalPanel.Margin = new Thickness(0, 0, 0, 16);
        connectionPanel.Children.Add(portalPanel);

        loginButton.Classes.Add("primary");
        loginButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        loginButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        loginButton.Height = 48;
        loginButton.FontSize = 15;
        loginButton.FontWeight = FontWeight.SemiBold;
        connectionPanel.Children.Add(loginButton);
        label.Foreground = Muted;
        label.FontSize = 12;
        label.LineHeight = 20;
        label.Margin = new Thickness(1, 13, 1, 0);
        connectionPanel.Children.Add(label);

        updatePanel.Margin = new Thickness(30, 0, 30, 12);
        updatePanel.Children.Add(guiUpdateNotice);
        updatePanel.Children.Add(coreUpdateNotice);
        updatePanel.Children.Add(updateProgress);
        guiUpdateNotice.Classes.Add("quiet");
        coreUpdateNotice.Classes.Add("quiet");
        BuildExtraViews();
        body.Children.Add(extraPanel);
        mainScroll.Content = body;
        mainScroll.Margin = new Thickness(0, 0, 0, 5);
        Grid.SetRow(mainScroll, 1);
        shell.Children.Add(mainScroll);
        Grid.SetRow(updatePanel, 2);
        shell.Children.Add(updatePanel);

        Grid footer = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(30, 0, 30, 12) };
        if (skipBackend)
            footer.Children.Add(new TextBlock { Text = "界面预览 · 不会发起连接", FontSize = 11, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center });
        diagnosticsLink.Classes.Add("quiet");
        diagnosticsLink.Click += (_, _) => { if (extraPanel.IsVisible) HideExtraView(); else ShowExtraView("diagnostics"); };
        Grid.SetColumn(diagnosticsLink, 1);
        footer.Children.Add(diagnosticsLink);
        Grid.SetRow(footer, 3);
        shell.Children.Add(footer);
        Content = shell;
        RefreshStatusPresentation();
    }

    private void ConfigureMoreMenu()
    {
        StackPanel menu = new() { Spacing = 4, Width = 218 };
        Flyout flyout = new() { Content = menu, Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach ((string title, string subtitle, string view) in new[]
        {
            ("连接设置", "自动连接、网卡与高级选项", "settings"),
            ("诊断与日志", "环境自检、实时日志与导出", "diagnostics"),
            ("软件更新", "检查界面与认证程序更新", "updates")
        })
        {
            StackPanel caption = new() { Spacing = 4 };
            caption.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
            caption.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Muted });
            Button item = new() { Name = view + "MenuButton", Content = caption, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(11, 10) };
            item.Classes.Add("quiet");
            item.Click += (_, _) => { flyout.Hide(); ShowExtraView(view); };
            menu.Children.Add(item);
        }
        Button exit = new() { Name = "exitMenuButton", Content = "退出程序并断开连接", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(11, 10) };
        exit.Classes.Add("quiet");
        exit.Click += (_, _) => { flyout.Hide(); RequestExit(); };
        menu.Children.Add(exit);
        moreButton.Flyout = flyout;
    }

    private void BuildExtraViews()
    {
        Grid heading = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 17) };
        heading.Children.Add(extraHeading);
        Button close = new() { Name = "closeExtraButton", Content = "返回连接", FontSize = 11 };
        close.Classes.Add("quiet");
        close.Click += (_, _) => HideExtraView();
        Grid.SetColumn(close, 1);
        heading.Children.Add(close);
        StackPanel extra = new();
        extra.Children.Add(heading);
        extra.Children.Add(extraStatus);
        extra.Children.Add(settingsView);
        extra.Children.Add(diagnosticsView);
        extra.Children.Add(updatesView);
        extraPanel.Child = extra;

        StackPanel preferences = new() { Spacing = 8 };
        preferences.Children.Add(autoCheckBox);
        preferences.Children.Add(startupCheckBox);
        settingsView.Children.Add(preferences);
        settingsView.Children.Add(new Separator { Background = Line });
        settingsView.Children.Add(Field("认证方式", modeComboBox));
        settingsView.Children.Add(Field("指定网卡", macComboBox));
        settingsView.Children.Add(Field("固定本机 IP", ipTextBox));
        settingsView.Children.Add(Note("一般保持自动选择即可。只有网络环境要求时才需要修改。"));
        StackPanel portalFields = new() { Spacing = 12 };
        portalFields.Children.Add(Field("认证接口地址", portalApiTextBox));
        portalFields.Children.Add(Field("接入设备 IPv4", portalAcIpTextBox));
        portalFields.Children.Add(Field("设备 IPv4", portalClientIpTextBox));
        portalFields.Children.Add(allowHttpCheckBox);
        portalFields.Children.Add(Note("仅在学校明确要求时使用 HTTP；账号和密码将以明文传输。"));
        advancedPortalSettings.Children.Add(new Expander
        {
            Name = "advancedPortalExpander", Header = "更多网页设置（通常留空）", Content = portalFields,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        settingsView.Children.Add(advancedPortalSettings);
        deleteButton.Content = "删除当前账号";
        deleteButton.Classes.Add("quiet");
        deleteButton.Foreground = palette.Error;
        settingsView.Children.Add(deleteButton);

        diagnosticsView.Children.Add(Note("检查连接环境，或查看认证程序的详细输出。关闭面板后，后台仍会保留最近的日志。"));
        WrapPanel diagnosticActions = new() { Orientation = Orientation.Horizontal };
        doctorButton.Margin = new Thickness(0, 0, 8, 6);
        diagnosticActions.Children.Add(doctorButton);
        Button export = new() { Name = "exportButton", Content = "导出诊断", Margin = new Thickness(0, 0, 0, 6) };
        export.Click += async (_, _) => await ExportAsync();
        diagnosticActions.Children.Add(export);
        diagnosticsView.Children.Add(diagnosticActions);
        logTextBox.Height = 210;
        logTextBox.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
        logTextBox.FontSize = 11;
        logTextBox.Background = palette.Paper;
        logTextBox.PlaceholderText = "连接后，详细日志会显示在这里。";
        diagnosticsView.Children.Add(logTextBox);

        updatesView.Children.Add(Note("更新前会先确认，检查更新不会中断当前连接。"));
        updatesView.Children.Add(checkUpdateButton);
        updatesView.Children.Add(checkCoreUpdateButton);
        versionDetails.Text = $"界面 {GuiVersion}  ·  认证程序待检查";
        updatesView.Children.Add(versionDetails);
    }

    internal void ShowExtraView(string view)
    {
        settingsView.IsVisible = view == "settings";
        diagnosticsView.IsVisible = view == "diagnostics";
        updatesView.IsVisible = view == "updates";
        extraHeading.Text = view switch { "settings" => "连接设置", "diagnostics" => "诊断与日志", _ => "软件更新" };
        connectionPanel.IsVisible = false;
        extraPanel.IsVisible = true;
        diagnosticsLink.IsVisible = false;
        diagnosticsLink.Content = "返回连接";
        mainScroll.Offset = new Vector(0, 0);
        RefreshLogVisibility();
    }

    private void HideExtraView()
    {
        extraPanel.IsVisible = false;
        diagnosticsLink.IsVisible = true;
        connectionPanel.IsVisible = true;
        diagnosticsLink.Content = "连接遇到问题？";
        mainScroll.Offset = new Vector(0, 0);
        RefreshLogVisibility();
        moreButton.Focus();
    }

    private void RefreshStatusPresentation()
    {
        string message = label.Text ?? "";
        bool online = message.StartsWith("已连接", StringComparison.Ordinal) || message == "网线认证已通过。";
        bool recovering = message.Contains("自动重连", StringComparison.Ordinal);
        bool waiting = message.Contains("等待网络", StringComparison.Ordinal);
        bool stopping = message.Contains("正在注销", StringComparison.Ordinal);
        bool preparing = message.Contains("正在检查", StringComparison.Ordinal) || message.Contains("后自动重试", StringComparison.Ordinal);
        bool busy = recovering || waiting || message.StartsWith("正在连接", StringComparison.Ordinal)
            || message.StartsWith("正在登录", StringComparison.Ordinal) || message.Contains("正在确认连接", StringComparison.Ordinal) || preparing;
        bool failed = message.Contains("失败", StringComparison.Ordinal) || message.Contains("已退出", StringComparison.Ordinal);
        statusHeading.Text = online ? "已连接校园网" : recovering ? "正在恢复连接" : waiting ? "等待网络就绪"
            : stopping ? "正在断开连接" : preparing ? "正在准备连接" : busy ? "正在连接校园网" : failed ? "连接需要检查" : "连接校园网";
        statusCaption.Text = online ? "连接已建立" : stopping ? "正在停止" : recovering ? "自动重试中" : busy ? "连接中" : failed ? "需要处理" : "尚未连接";
        statusDot.Fill = online || busy || stopping ? Accent : failed ? palette.Error : Muted;
        extraStatus.Text = "连接状态：" + statusCaption.Text;
    }

    private StackPanel Field(string caption, Control input, Thickness? margin = null)
    {
        StackPanel field = new() { Spacing = 8, Margin = margin ?? default };
        field.Children.Add(new TextBlock { Text = caption, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = Ink });
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        field.Children.Add(input);
        return field;
    }

    private TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };

    private void UpdateCompactLayout()
    {
        // Give the form priority on short displays; the art never forces the action off-screen.
        bool compact = ClientSize.Height < 620;
        campusArt.IsVisible = ClientSize.Height >= (portalPanel.IsVisible ? 820 : 620);
        if (pageHeader is not null) pageHeader.Margin = new Thickness(30, compact ? 8 : 15, 26, compact ? 6 : 12);
        if (statusRow is not null) statusRow.Margin = new Thickness(0, 0, 0, compact ? 10 : 19);
        rememberCheckBox.Margin = new Thickness(0, compact ? 6 : 9, 0, compact ? 8 : 12);
    }

    private void ApplyMotionPreferences()
    {
        bool enabled = DesktopMotion.AllowsTransitions;
        foreach (Button button in new[] { loginButton, moreButton, doctorButton, checkUpdateButton, checkCoreUpdateButton })
            button.Transitions = enabled ? new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(100) }
            } : null;
    }

    private void ApplyDesktopStyles()
    {
        Styles.Add(new Style(s => s.OfType<TextBox>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(Layoutable.MinHeightProperty, 44d), new Setter(TemplatedControl.BorderBrushProperty, Line), new Setter(TemplatedControl.BackgroundProperty, palette.Paper), new Setter(TemplatedControl.PaddingProperty, new Thickness(13, 11)) }
        });
        Styles.Add(new Style(s => s.OfType<ComboBox>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(Layoutable.MinHeightProperty, 43d), new Setter(TemplatedControl.BorderBrushProperty, Line), new Setter(TemplatedControl.BackgroundProperty, palette.Paper) }
        });
        Styles.Add(new Style(s => s.OfType<AutoCompleteBox>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(Layoutable.MinHeightProperty, 44d), new Setter(TemplatedControl.BorderBrushProperty, Line), new Setter(TemplatedControl.BackgroundProperty, palette.Paper), new Setter(TemplatedControl.PaddingProperty, new Thickness(13, 11)) }
        });
        Styles.Add(new Style(s => s.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 9)), new Setter(TemplatedControl.FontWeightProperty, FontWeight.Medium), new Setter(TemplatedControl.BackgroundProperty, palette.Paper), new Setter(TemplatedControl.BorderBrushProperty, Line), new Setter(TemplatedControl.ForegroundProperty, Ink) }
        });
        Resources["ButtonBackgroundPointerOver"] = palette.Hover;
        Resources["ButtonBackgroundPressed"] = palette.Pressed;
        Resources["ButtonForegroundPointerOver"] = Ink;
        Resources["ButtonForegroundPressed"] = Ink;
        Styles.Add(new Style(s => s.OfType<Button>().Class("quiet"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)), new Setter(TemplatedControl.ForegroundProperty, Muted) }
        });
        Styles.Add(new Style(s => s.OfType<Button>().Class("primary"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Accent), new Setter(TemplatedControl.ForegroundProperty, palette.OnAccent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)) }
        });
        Styles.Add(new Style(s => s.OfType<ToggleButton>().Class("mode"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(TemplatedControl.ForegroundProperty, Muted) }
        });
        Styles.Add(new Style(s => s.OfType<ToggleButton>().Class("mode").Class(":checked"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, palette.Paper), new Setter(TemplatedControl.ForegroundProperty, Ink), new Setter(TemplatedControl.FontWeightProperty, FontWeight.SemiBold) }
        });
        Styles.Add(new Style(s => s.OfType<Button>().Class(":pressed"))
        {
            Setters = { new Setter(OpacityProperty, 0.86d) }
        });
        // Theme the actual native text controls, including watermarks and disabled accounts.
        Styles.Add(new Style(s => s.OfType<TextBox>())
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Ink), new Setter(TextBox.PlaceholderForegroundProperty, Muted) }
        });
        foreach (string key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused",
            "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundDisabled", "TextControlForegroundDisabled" })
            Resources[key] = Muted;
        Resources["TextControlBackgroundDisabled"] = palette.Soft;
        Resources["TextControlBackgroundPointerOver"] = palette.Paper;
        Resources["TextControlBackgroundFocused"] = palette.Paper;
        Resources["TextControlForeground"] = Ink;
        Resources["TextControlPlaceholderOpacity"] = 1d;
        Resources["TextControlBorderBrushFocused"] = Accent;
        loginButton.Resources["ButtonBackgroundPointerOver"] = palette.AccentHover;
        loginButton.Resources["ButtonBackgroundPressed"] = palette.AccentPressed;
        loginButton.Resources["ButtonForegroundPointerOver"] = palette.OnAccent;
        loginButton.Resources["ButtonForegroundPressed"] = palette.OnAccent;
        ApplyMotionPreferences();
    }
}
