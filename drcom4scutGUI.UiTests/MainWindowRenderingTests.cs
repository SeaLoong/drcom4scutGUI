using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace drcom4scutGUI.UiTests;

[Collection("Avalonia UI")]
public sealed class MainWindowRenderingTests
{
    [Theory]
    [InlineData("idle")]
    [InlineData("online")]
    [InlineData("reconnecting")]
    [InlineData("error")]
    [InlineData("portal")]
    [InlineData("settings")]
    [InlineData("diagnostics")]
    [InlineData("compact")]
    public async Task NativeControlsRenderWithoutHorizontalOverflowAndKeepThePrimaryActionReachable(string scene)
    {
        await using HeadlessUnitTestSession session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            Configuration configuration = new()
            {
                Accounts = [new Account { Name = "student-demo", RememberPassword = false }],
                Account = "student-demo",
                Mode = scene == "portal" ? "portal" : "eap",
                PortalBase = "https://s.scut.edu.cn"
            };
            MainWindow window = new(skipBackend: true, initialConfiguration: configuration);
            if (scene == "compact") { window.Width = 440; window.Height = 520; }
            window.Show();
            try
            {
                T Find<T>(string name) where T : Control => window.GetLogicalDescendants()
                    .OfType<T>().Single(control => control.Name == name);
                Find<TextBlock>("statusLabel").Text = "填写校园账号，连接校园网络。";
                if (scene is "online" or "reconnecting")
                {
                    window.SetUIEnabled(false);
                    window.ApplyConnectionStatus(new(scene == "online" ? CoreConnectionPhase.Online : CoreConnectionPhase.Offline), "eap", false);
                }
                if (scene == "error")
                    window.ReportConnectionFailure("连接失败，请检查网络或查看诊断。", "[界面预览] 认证程序暂时没有响应。");
                if (scene is "settings" or "diagnostics")
                {
                    window.ReceiveLine("[界面预览] 日志示例；未读取账号或发起网络认证。");
                    window.ShowExtraView(scene);
                }
                Dispatcher.UIThread.RunJobs();
                window.SetRenderScaling(1.5);
                using WriteableBitmap? frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                ScrollViewer scroll = window.GetLogicalDescendants().OfType<ScrollViewer>()
                    .First(control => control.Content is StackPanel);
                Assert.True(scroll.Viewport.Width > 0);
                Assert.InRange(scroll.Extent.Width, 0, scroll.Viewport.Width + 1);
                Button primary = Find<Button>("loginButton");
                if (scene is not ("settings" or "diagnostics"))
                    Assert.InRange(primary.Bounds.Width, 300, window.ClientSize.Width);
                if (scene is "idle" or "online" or "reconnecting")
                {
                    Point? bottom = primary.TranslatePoint(new Point(0, primary.Bounds.Height), scroll);
                    Assert.NotNull(bottom);
                    Assert.InRange(bottom.Value.Y, 0, scroll.Viewport.Height);
                }
                string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../artifacts/ui-redesign"));
                Directory.CreateDirectory(directory);
                frame.Save(Path.Combine(directory, scene + ".png"), PngBitmapEncoderOptions.Default);
                if (scene == "idle")
                    File.WriteAllText(Path.Combine(directory, "font.txt"),
                        "Requested: " + window.FontFamily.Name + Environment.NewLine
                        + "Resolved: " + new Typeface(window.FontFamily).GlyphTypeface.FamilyName);
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }
}
