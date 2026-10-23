using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class UpdateOptionsTests
{
    [Fact]
    public void DefaultsDoNotWriteAnyFileOrModifyAccountSettings()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("updates.json");
        var options = new UpdateOptionsStore(path).Load();
        Assert.Equal("https://gh-proxy.com/", options.ProxyUrl);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Theory]
    [InlineData("http://proxy.example/")]
    [InlineData("https://user:password@proxy.example/")]
    [InlineData("https://proxy.example/?secret=value")]
    [InlineData("https://proxy.example/#fragment")]
    [InlineData("https://proxy.example/prefix")]
    [InlineData("not a URL")]
    public void InvalidProxyNeverFallsBackToDirectConnection(string address)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("updates.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { proxyUrl = address }));
        Assert.Throws<InvalidDataException>(() => new UpdateOptionsStore(path).Load());
    }

    [Fact]
    public void CustomHttpsPrefixIsReadOnlyAndIndependentOfCredentialStore()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("updates.json");
        const string json = "{\"proxyUrl\":\"https://proxy.example:8443/github/\"}";
        File.WriteAllText(path, json);
        Assert.Equal("https://proxy.example:8443/github/", new UpdateOptionsStore(path).Load().ProxyUrl);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"password\":\"never-an-update-option\"}")]
    [InlineData("{\"proxyUrl\":null}")]
    public void CorruptOrUnknownSettingsFailWithClearError(string json)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("updates.json");
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => new UpdateOptionsStore(path).Load());
    }
}
