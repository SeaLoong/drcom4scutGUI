using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class ConfigurationStoreTests
{
    private static Configuration Example() => new()
    {
        Accounts = [new Account { Name = "student", Password = "synthetic-private-password", IP = "192.0.2.17" }],
        Account = "student", Mac = "02:00:00:00:00:01", Auto = true
    };

    [Fact]
    public void NewConfigurationNeedsNoWritableCredentialFile()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var configuration = new ConfigurationStore(path, new TestProtector()).Load();
        Assert.Empty(configuration.Accounts);
        Assert.False(configuration.Auto);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SaveContainsCiphertextAndNeverSerializesRuntimePassword()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var store = new ConfigurationStore(path, new TestProtector());
        store.Save(Example());
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("synthetic-private-password", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var account = Assert.Single(document.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.False(account.TryGetProperty("password", out _));
        Assert.StartsWith("test:", account.GetProperty("encryptedPassword").GetString());
        var loaded = store.Load();
        Assert.Equal("synthetic-private-password", Assert.Single(loaded.Accounts).Password);
        Assert.Equal("192.0.2.17", loaded.Accounts[0].IP);
        Assert.Equal("student", loaded.Account);
        Assert.Equal("02:00:00:00:00:01", loaded.Mac);
        Assert.True(loaded.Auto);
    }

    [Fact]
    public void EapOnlySelectionSurvivesRestartAndCanBeDisabledAgain()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var configuration = Example();
        configuration.NoUdp = true;
        var store = new ConfigurationStore(path, new TestProtector());
        store.Save(configuration);
        var loaded = store.Load();
        Assert.True(loaded.NoUdp);
        Assert.True(loaded.Auto);
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.True(document.RootElement.GetProperty("noUdp").GetBoolean());
        loaded.NoUdp = false;
        store.Save(loaded);
        Assert.False(store.Load().NoUdp);
    }

    [Fact]
    public void MultipleAccountsAndUnicodeSecretsRoundTripIndependently()
    {
        using var directory = new TemporaryDirectory();
        var configuration = Example();
        configuration.Accounts.Add(new Account { Name = "第二账号", Password = "空 格\"\\密碼🔐", IP = "" });
        var store = new ConfigurationStore(directory.File("credentials.json"), new TestProtector());
        store.Save(configuration);
        var loaded = store.Load();
        Assert.Equal(2, loaded.Accounts.Count);
        Assert.Equal("空 格\"\\密碼🔐", loaded.Find("第二账号")!.Password);
        Assert.Null(loaded.Find("missing"));
    }

    [Fact]
    public void LegacyMultiAccountPlaintextMigratesBeforeLoadReturns()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        File.WriteAllText(path, """{"accounts":[{"name":"student","password":"legacy-secret","ip":"192.0.2.9"}],"mac":"02:00:00:00:00:01","account":"student","auto":true}""");
        var loaded = new ConfigurationStore(path, new TestProtector()).Load();
        Assert.Equal("legacy-secret", Assert.Single(loaded.Accounts).Password);
        Assert.True(loaded.Auto);
        Assert.False(loaded.NoUdp); // Legacy clients enabled the UDP stage by default.
        string rewritten = File.ReadAllText(path);
        Assert.DoesNotContain("legacy-secret", rewritten);
        Assert.DoesNotContain("\"password\"", rewritten);
        Assert.Contains("encryptedPassword", rewritten);
        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void OriginalSingleAccountGuiFileMigratesAndIsRemovedAfterSuccessfulSave()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        string legacy = directory.File("gui.json");
        File.WriteAllText(legacy, """{"username":"student","password":"old-gui-secret","ip":"192.0.2.8","mac":"02:00:00:00:00:01","autoLogin":true}""");
        var loaded = new ConfigurationStore(path, new TestProtector(), legacy).Load();
        Assert.Equal("old-gui-secret", Assert.Single(loaded.Accounts).Password);
        Assert.Equal("student", loaded.Account);
        Assert.True(loaded.Auto);
        Assert.False(File.Exists(legacy));
        Assert.DoesNotContain("old-gui-secret", File.ReadAllText(path));
    }

    [Fact]
    public void ExistingEncryptedPrimaryDoesNotImportAnUnrelatedLegacyFile()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        string legacy = directory.File("gui.json");
        var store = new ConfigurationStore(path, new TestProtector(), legacy);
        store.Save(Example());
        File.WriteAllText(legacy, """{"username":"unrelated","password":"do-not-import"}""");
        var loaded = store.Load();
        Assert.Equal("student", Assert.Single(loaded.Accounts).Name);
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void EncryptionFailureCannotOverwriteExistingCredentialFile()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        new ConfigurationStore(path, new TestProtector()).Save(Example());
        byte[] original = File.ReadAllBytes(path);
        var store = new ConfigurationStore(path, new FailingProtector());
        Assert.Throws<CryptographicException>(() => store.Save(Example()));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void FailedLegacyEncryptionPreservesRecoverableOriginal()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        string legacy = directory.File("gui.json");
        string original = """{"username":"student","password":"synthetic-legacy"}""";
        File.WriteAllText(legacy, original);
        Assert.Throws<CryptographicException>(() => new ConfigurationStore(path, new FailingProtector(), legacy).Load());
        Assert.Equal(original, File.ReadAllText(legacy));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CorruptEncryptedCredentialFailsWithoutDestroyingOtherData()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        string original = """{"schemaVersion":2,"accounts":[{"name":"student","encryptedPassword":"broken","ip":""}],"account":"student"}""";
        File.WriteAllText(path, original);
        Assert.Throws<CryptographicException>(() => new ConfigurationStore(path, new TestProtector()).Load());
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void InvalidJsonFailsWithoutReplacingOrCreatingBackupFiles()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        File.WriteAllText(path, "{not-json");
        Assert.Throws<InvalidDataException>(() => new ConfigurationStore(path, new TestProtector()).Load());
        Assert.Equal("{not-json", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void RemovingAccountRemovesItsCiphertextOnSave()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var store = new ConfigurationStore(path, new TestProtector());
        var configuration = Example();
        store.Save(configuration);
        configuration.Accounts.Clear();
        configuration.Account = "";
        store.Save(configuration);
        Assert.Empty(store.Load().Accounts);
        Assert.DoesNotContain("student", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":3,\"accounts\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"accounts\":[{\"name\":\"student\"}]}")]
    [InlineData("{\"schemaVersion\":2,\"accounts\":false}")]
    [InlineData("{\"accounts\":[],\"auto\":\"true\"}")]
    [InlineData("[]")]
    public void InvalidSchemaCannotSilentlyDiscardCredentials(string json)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => new ConfigurationStore(path, new TestProtector()).Load());
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void SerializingRuntimeModelsDirectlyCannotExposePassword()
    {
        string json = JsonSerializer.Serialize(Example());
        Assert.DoesNotContain("synthetic-private-password", json);
        Assert.DoesNotContain("\"Password\"", json);
    }

    public static bool IsNotWindows => !OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsNotWindows), Skip = "POSIX file modes require a Unix runner")]
    public void CredentialFileIsPrivateOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        new ConfigurationStore(path, new TestProtector()).Save(Example());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows DPAPI requires a Windows runner")]
    [SupportedOSPlatform("windows")]
    public void DpapiRoundTripIsBoundToWindowsCurrentUser()
    {
        // This branch executes on the Windows CI runner, never under a fake API.
        if (!OperatingSystem.IsWindows()) return;
        var protector = new DpapiSecretProtector();
        string encrypted = protector.Protect("synthetic-windows-secret");
        Assert.DoesNotContain("synthetic-windows-secret", encrypted);
        Assert.Equal("synthetic-windows-secret", protector.Unprotect(encrypted));
        Assert.Throws<InvalidDataException>(() => protector.Unprotect("invalid-base64"));
    }
}
