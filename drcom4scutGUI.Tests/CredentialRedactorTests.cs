using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class CredentialRedactorTests
{
    [Theory]
    [InlineData("student123", "123", "student123", "[凭据已隐藏]")]
    [InlineData("123", "student123", "student123", "[凭据已隐藏]")]
    [InlineData("student123", "student123", "student123", "[凭据已隐藏]")]
    [InlineData("abc", "bcd", "abcd", "[凭据已隐藏]")]
    [InlineData("abc", "cde", "abcde", "[凭据已隐藏]")]
    [InlineData("aba", "", "ababa", "[账号已隐藏]")]
    [InlineData("", "aba", "ababa", "[密码已隐藏]")]
    public void OverlappingOriginalSpansAreHiddenWithoutLeavingCredentialFragments(
        string username, string password, string value, string hidden)
    {
        CredentialRedactor redactor = new(username, password);
        Assert.Equal("before:" + hidden + ";after", redactor.Redact("before:" + value + ";after"));
        var diagnostics = new DiagnosticsBuffer();
        diagnostics.Add("before:" + value + ";after", username, password);
        Assert.Equal("before:" + hidden + ";after", diagnostics.Export());
    }

    [Fact]
    public void ReplacementMarkersAreNotReinterpretedAsInputCredentials()
    {
        CredentialRedactor redactor = new("隐藏", "private-secret");
        Assert.Equal("[密码已隐藏] [账号已隐藏]", redactor.Redact("private-secret 隐藏"));
    }

    [Fact]
    public void JsonAndUrlEncodedCredentialsAreHiddenUsingTheOriginalRepresentation()
    {
        const string username = "student \"quoted\" 名字";
        const string password = "prefix\"🔐%";
        CredentialRedactor redactor = new(username, password);
        string json = JsonSerializer.Serialize(new[] { username, password });
        Assert.Equal(["[账号已隐藏]", "[密码已隐藏]"], JsonSerializer.Deserialize<string[]>(redactor.Redact(json))!);
        Assert.Equal("user=[账号已隐藏]&password=[密码已隐藏]", redactor.Redact(
            "user=" + Uri.EscapeDataString(username) + "&password=" + Uri.EscapeDataString(password)));
    }
}
