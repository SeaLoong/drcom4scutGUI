using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using System.Xml.Linq;
using Xunit;

namespace drcom4scutGUI.UiTests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).WithInterFont();
}

public sealed class MainWindowTests
{
    private static T Find<T>(MainWindow window, string name) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static async Task OnUi(Action action)
    {
        // The official xUnit adapter currently targets xUnit 3.x internals.
        // Keep the latest xUnit runner and use Avalonia's public UI dispatcher.
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(action, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void PerUserInstanceLockExcludesAnotherInstanceAndReleasesForRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "drcom-gui-instance-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (FileStream? first = DesktopInstance.TryAcquire(directory))
            {
                Assert.NotNull(first);
                using FileStream? second = DesktopInstance.TryAcquire(directory);
                Assert.Null(second);
                Assert.Equal(0, first.Length);
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(first.Name));
            }
            using FileStream? restarted = DesktopInstance.TryAcquire(directory);
            Assert.NotNull(restarted);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LoginStartupDefinitionsKeepExecutablePathsAsOneLiteralArgument()
    {
        const string executable = "/Applications/My & Your $app/quote\"and\\slash%client/drcom4scutGUI";
        XDocument mac = XDocument.Parse(DesktopStartup.MacDefinition(executable));
        Assert.Equal(executable, mac.Descendants("array").Single().Elements("string").Single().Value);
        string linux = DesktopStartup.LinuxDefinition(executable);
        Assert.Contains("\\\\$app", linux);
        Assert.Contains("quote\\\\\"and\\\\\\\\slash%%client", linux);
        Assert.DoesNotContain("sh -c", linux);
        Assert.Throws<InvalidOperationException>(() => DesktopStartup.LinuxDefinition("/tmp/line\nbreak/gui"));
    }

    [Fact]
    public void MacLoginStartupUsesTheBundleLauncherWithSpacesAsOneArgument()
    {
        string directory = Path.Combine(Path.GetTempPath(), "drcom-gui-startup-" + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(directory, "Campus & Network.app");
        string payload = Path.Combine(bundle, "Contents", "Resources", "publish");
        string native = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(payload);
        Directory.CreateDirectory(native);
        try
        {
            string apphost = Path.Combine(payload, "drcom4scutGUI");
            string launcher = Path.Combine(native, "drcom4scutGUI");
            File.WriteAllText(apphost, "managed apphost fixture");
            File.WriteAllText(launcher, "native launcher fixture");
            string startup = PlatformPaths.ResolveGuiExecutablePath(payload, apphost, macOS: true);
            Assert.Equal(launcher, startup);
            XDocument document = XDocument.Parse(DesktopStartup.MacDefinition(startup));
            Assert.Equal(launcher, document.Descendants("array").Single().Elements("string").Single().Value);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public Task AllThreeModesKeepTheirProviderAndExposeOnlyRelevantFields() => OnUi(() =>
    {
        Configuration configuration = new();
        MainWindow window = new(skipBackend: true, initialConfiguration: configuration);
        window.Show();
        try
        {
            ComboBox modes = Find<ComboBox>(window, "modeComboBox");
            StackPanel portal = Find<StackPanel>(window, "portalPanel");
            Assert.Equal(3, modes.Items.Count);
            Assert.False(portal.IsVisible);
            modes.SelectedIndex = 1;
            Assert.Equal("eap", configuration.Mode);
            Assert.True(configuration.NoUdp);
            Assert.False(portal.IsVisible);
            modes.SelectedIndex = 2;
            Assert.Equal("portal", configuration.Mode);
            Assert.False(configuration.NoUdp);
            Assert.True(portal.IsVisible);
            Assert.NotNull(Find<CheckBox>(window, "allowHttpCheckBox"));
            modes.SelectedIndex = 0;
            Assert.Equal("eap", configuration.Mode);
            Assert.False(configuration.NoUdp);
            Assert.False(portal.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SwitchingAccountsShowsEachPasswordsAndRememberChoiceWithoutMixingThem() => OnUi(() =>
    {
        Account first = new() { Name = "first-user", Password = "first-secret", IP = "192.0.2.7" };
        Account second = new() { Name = "second-user", Password = "session-secret", IP = "192.0.2.8", RememberPassword = false };
        Configuration configuration = new() { Accounts = [first, second], Account = first.Name };
        MainWindow window = new(skipBackend: true, initialConfiguration: configuration);
        window.Show();
        try
        {
            AutoCompleteBox account = Find<AutoCompleteBox>(window, "nameComboBox");
            TextBox password = Find<TextBox>(window, "passwordTextBox");
            CheckBox remember = Find<CheckBox>(window, "rememberCheckBox");
            Assert.Equal('●', password.PasswordChar);
            Assert.Equal("first-secret", password.Text);
            password.Text = "updated-first-secret";
            account.Text = second.Name;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("updated-first-secret", first.Password);
            Assert.Equal("session-secret", password.Text);
            Assert.False(remember.IsChecked);
            Assert.Equal(second.IP, Find<TextBox>(window, "ipTextBox").Text);
            remember.IsChecked = true;
            Assert.True(second.RememberPassword);
            account.Text = first.Name;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("updated-first-secret", password.Text);
            Assert.True(remember.IsChecked);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task InvalidSourceIpIsMarkedAndCorrectedInputClearsTheError() => OnUi(() =>
    {
        MainWindow window = new(skipBackend: true);
        window.Show();
        try
        {
            TextBox address = Find<TextBox>(window, "ipTextBox");
            address.Text = "2001:db8::1";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("IPv4", ToolTip.GetTip(address)?.ToString());
            address.Text = "192.0.2.22";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(ToolTip.GetTip(address));
            address.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Null(ToolTip.GetTip(address));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OnlineAndRecoveryStatusRemainReachableWithoutATrayHost() => OnUi(() =>
    {
        MainWindow window = new(skipBackend: true);
        window.Show();
        try
        {
            TextBlock status = Find<TextBlock>(window, "statusLabel");
            window.ApplyConnectionStatus(new(CoreConnectionPhase.UdpAuthentication), "eap", false);
            Assert.Contains("正在建立 UDP", status.Text);
            window.ApplyConnectionStatus(new(CoreConnectionPhase.Online), "eap", false);
            Assert.Contains("UDP 心跳均成功", status.Text);
            Assert.True(window.IsVisible);
            window.ApplyConnectionStatus(new(CoreConnectionPhase.Offline), "eap", false);
            Assert.Contains("重试", status.Text);
            Assert.True(window.IsVisible);
            window.ApplyConnectionStatus(new(CoreConnectionPhase.Online), "portal", false);
            Assert.Contains("门户已确认在线", status.Text);
            Assert.True(window.IsVisible);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ProbeModeRendersTheRealWindowWithoutStartingAnAuthenticationProcess() => OnUi(() =>
    {
        MainWindow window = new(skipBackend: true);
        window.Show();
        try
        {
            Assert.True(window.IsVisible);
            Assert.True(window.ClientSize.Width > 0);
            Assert.Contains("不会读取账号或启动认证", Find<TextBlock>(window, "statusLabel").Text);
            Assert.NotNull(Find<Button>(window, "doctorButton"));
            Assert.NotNull(Find<Button>(window, "exportButton"));
            Assert.NotNull(Find<CheckBox>(window, "startupCheckBox"));
        }
        finally { window.Close(); }
    });
}
