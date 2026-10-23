using System.Text;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class CoreOutputReaderTests
{
    [Fact]
    public async Task OversizedLinesAreDroppedWholeAndTheFollowingMessageSurvives()
    {
        const string secret = "synthetic-private-password";
        string input = new string('x', CoreOutputReader.MaximumLineCharacters - 8) + secret
            + new string('x', 512 * 1024) + "\r\nnext message\n";
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        List<string> lines = [];
        await CoreOutputReader.ReadLinesAsync(reader, lines.Add, new("", secret), TestContext.Current.CancellationToken);
        Assert.Equal(["[核心日志过长，已省略]", "next message"], lines);
        Assert.DoesNotContain("synthetic", string.Join('\n', lines));
    }

    [Fact]
    public async Task NewlineBoundariesAndUnterminatedLastLinesPreserveRedaction()
    {
        string input = new string('x', 4095) + "\r\nfirst-secret\rsecond-secret\nlast-secret";
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        List<string> lines = [];
        await CoreOutputReader.ReadLinesAsync(reader, lines.Add, new("", "secret"), TestContext.Current.CancellationToken);
        Assert.Equal([new string('x', 4095), "first-[密码已隐藏]", "second-[密码已隐藏]", "last-[密码已隐藏]"], lines);
    }

    [Fact]
    public async Task ProbeOutputIsBoundedWithoutIncludingSourceTextInTheError()
    {
        string input = "private-diagnostic-text" + new string('x', CoreOutputReader.MaximumProbeCharacters);
        using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CoreOutputReader.ReadProbeAsync(reader, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private-diagnostic-text", error.Message);
    }
}
