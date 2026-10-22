using System.Text.Json;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class PortalProfileTests
{
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "helpers", OperatingSystem.IsWindows() ? "FakeCore.exe" : "FakeCore");

    private static CoreLaunchOptions Portal(string portalBase = "https://portal.example.test", string portalApi = "", bool allowHttp = false) =>
        new("student", "synthetic-portal-secret", Mode: "portal", PortalBase: portalBase, PortalApi: portalApi, AllowHttp: allowHttp);

    [Fact]
    public void PortalProfilePersistsWithoutDowngradingSecretStorage()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var store = new ConfigurationStore(path, new TestProtector());
        var configuration = new Configuration
        {
            Accounts = [new Account { Name = "student", Password = "private-portal-profile-secret", IP = "192.0.2.17" }],
            Account = "student", Auto = true, NoUdp = true, Mode = "portal",
            PortalBase = "https://portal.example.test", PortalApi = "https://portal.example.test/eportal/portal/login",
            PortalAcIp = "192.0.2.20", PortalClientIp = "198.51.100.17", AllowHttp = false
        };
        store.Save(configuration);
        var loaded = store.Load();
        Assert.Equal("portal", loaded.Mode);
        Assert.Equal(configuration.PortalBase, loaded.PortalBase);
        Assert.Equal(configuration.PortalApi, loaded.PortalApi);
        Assert.Equal(configuration.PortalAcIp, loaded.PortalAcIp);
        Assert.Equal(configuration.PortalClientIp, loaded.PortalClientIp);
        Assert.False(loaded.AllowHttp);
        Assert.True(loaded.Auto);
        Assert.True(loaded.NoUdp); // Retained for selecting the older EAP profile later.
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("private-portal-profile-secret", json);
        Assert.DoesNotContain("\"password\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("portal", document.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public void LegacyAccountMigrationDefaultsToEapAndNeverInventsPortalHost()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        File.WriteAllText(path, """{"accounts":[{"name":"student","password":"legacy-secret"}],"account":"student","host":"unrelated.example.test"}""");
        var loaded = new ConfigurationStore(path, new TestProtector()).Load();
        Assert.Equal("eap", loaded.Mode);
        Assert.Equal("", loaded.PortalBase);
        Assert.Equal("", loaded.PortalApi);
        Assert.Equal("", loaded.PortalAcIp);
        Assert.Equal("", loaded.PortalClientIp);
        Assert.False(loaded.AllowHttp);
        Assert.False(loaded.NoUdp);
    }

    [Fact]
    public void ExistingEncryptedConfigurationDefaultsToEapWhenPortalFieldsAreAbsent()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        File.WriteAllText(path, """{"schemaVersion":2,"accounts":[],"account":"","auto":false,"noUdp":true}""");
        var loaded = new ConfigurationStore(path, new TestProtector()).Load();
        Assert.Equal("eap", loaded.Mode);
        Assert.Equal("", loaded.PortalBase);
        Assert.False(loaded.AllowHttp);
        Assert.True(loaded.NoUdp);
    }

    [Fact]
    public void IncompleteProfileCanBeSavedButCannotStartWithoutPortalAddress()
    {
        using var directory = new TemporaryDirectory();
        var store = new ConfigurationStore(directory.File("credentials.json"), new TestProtector());
        store.Save(new Configuration { Mode = "portal" });
        Assert.Equal("portal", store.Load().Mode);
        Assert.Throws<ArgumentException>(() => CoreProcessSession.CreateStartInfo(Helper, Portal(portalBase: "")));
    }

    [Fact]
    public void HttpAddressMayBeRetainedForEditingWithoutImplicitConsentToSendCredentials()
    {
        using var directory = new TemporaryDirectory();
        var store = new ConfigurationStore(directory.File("credentials.json"), new TestProtector());
        store.Save(new Configuration { Mode = "portal", PortalBase = "http://portal.example.test", AllowHttp = false });
        var loaded = store.Load();
        Assert.Equal("http://portal.example.test", loaded.PortalBase);
        Assert.False(loaded.AllowHttp);
        Assert.Throws<ArgumentException>(() => CoreProcessSession.CreateStartInfo(Helper, Portal(loaded.PortalBase)));
    }

    [Fact]
    public void PortalArgumentsDistinguishLocalSourceIpFromProtocolClientIpAndAcIp()
    {
        var options = new CoreLaunchOptions("student", "synthetic-portal-secret", IP: "192.0.2.17", NoUdp: true,
            Mode: "portal", PortalBase: "https://portal.example.test",
            PortalApi: "https://portal.example.test/eportal/portal/login", PortalAcIp: "192.0.2.20", PortalClientIp: "198.51.100.17");
        string[] args = [.. CoreProcessSession.CreateStartInfo(Helper, options).ArgumentList];
        Assert.Equal("portal", Value(args, "--mode"));
        Assert.Equal(options.PortalBase, Value(args, "--portal-base"));
        Assert.Equal(options.PortalApi, Value(args, "--portal-api"));
        Assert.Equal(options.PortalAcIp, Value(args, "--portal-ac-ip"));
        Assert.Equal(options.PortalClientIp, Value(args, "--portal-client-ip"));
        Assert.Equal(options.IP, Value(args, "--ip"));
        Assert.DoesNotContain("--no-udp", args);
        Assert.DoesNotContain("--allow-insecure-portal", args);
        Assert.Contains("--require-https-portal", args);
        Assert.Contains("--password-stdin", args);
        Assert.DoesNotContain(options.Password, args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EapModeNeverActivatesRetainedPortalFieldsOrHttpConsent(bool noUdp)
    {
        var options = new CoreLaunchOptions("student", "secret", NoUdp: noUdp, Mode: "eap",
            PortalBase: "http://portal.example.test", PortalApi: "https://portal.example.test/login",
            PortalAcIp: "192.0.2.20", PortalClientIp: "198.51.100.17", AllowHttp: true);
        string[] args = [.. CoreProcessSession.CreateStartInfo(Helper, options).ArgumentList];
        Assert.DoesNotContain("--portal-base", args);
        Assert.DoesNotContain("--portal-api", args);
        Assert.DoesNotContain("--portal-ac-ip", args);
        Assert.DoesNotContain("--portal-client-ip", args);
        Assert.DoesNotContain("--allow-insecure-portal", args);
        Assert.DoesNotContain("--require-https-portal", args);
        Assert.Equal(noUdp, args.Contains("--no-udp"));
        Assert.Equal(!noUdp, args.Contains("--with-udp"));
        Assert.Equal("eap", Value(args, "--mode"));
    }

    [Fact]
    public void PlainHttpNeedsExplicitOptInAndAddsExactlyOneConsentFlag()
    {
        Assert.Throws<ArgumentException>(() => CoreProcessSession.CreateStartInfo(Helper, Portal("http://portal.example.test")));
        string[] args = [.. CoreProcessSession.CreateStartInfo(Helper, Portal("http://portal.example.test", allowHttp: true)).ArgumentList];
        Assert.Equal(1, args.Count(argument => argument == "--allow-insecure-portal"));
        Assert.DoesNotContain("--require-https-portal", args);
        Assert.Equal("http://portal.example.test", Value(args, "--portal-base"));
    }

    [Theory]
    [InlineData("https://user:synthetic-portal-secret@portal.example.test", "")]
    [InlineData("https://portal.example.test/#synthetic-portal-secret", "")]
    [InlineData("https://portal.example.test", "https://user:synthetic-portal-secret@portal.example.test/login")]
    [InlineData("https://portal.example.test", "https://portal.example.test/login#synthetic-portal-secret")]
    [InlineData("https://portal.example.test", "https://other.example.test/login")]
    [InlineData("https://portal.example.test", "http://portal.example.test/login")]
    public void UnsafePortalTargetsAreRejectedBeforeLaunchWithoutEchoingUrlOrSecret(string portalBase, string portalApi)
    {
        var error = Assert.Throws<ArgumentException>(() => CoreProcessSession.CreateStartInfo(Helper, Portal(portalBase, portalApi, allowHttp: true)));
        Assert.DoesNotContain("synthetic-portal-secret", error.Message);
        Assert.DoesNotContain(portalBase, error.Message);
        if (portalApi.Length > 0) Assert.DoesNotContain(portalApi, error.Message);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("portal --password injected")]
    public void UnknownModesCannotBeSavedOrLaunched(string mode)
    {
        using var directory = new TemporaryDirectory();
        var store = new ConfigurationStore(directory.File("credentials.json"), new TestProtector());
        Assert.ThrowsAny<ArgumentException>(() => CoreProcessSession.CreateStartInfo(Helper, new CoreLaunchOptions("student", "secret", Mode: mode)));
        Assert.ThrowsAny<Exception>(() => store.Save(new Configuration { Mode = mode }));
        Assert.False(File.Exists(store.FilePath));
    }

    [Theory]
    [InlineData("https://user:stored-url-secret@portal.example.test", "")]
    [InlineData("https://portal.example.test", "https://user:stored-url-secret@portal.example.test/login")]
    public void CredentialBearingUrlsCannotBeWrittenIntoConfiguration(string portalBase, string portalApi)
    {
        using var directory = new TemporaryDirectory();
        var store = new ConfigurationStore(directory.File("credentials.json"), new TestProtector());
        var error = Assert.ThrowsAny<Exception>(() => store.Save(new Configuration { Mode = "portal", PortalBase = portalBase, PortalApi = portalApi }));
        Assert.DoesNotContain("stored-url-secret", error.Message);
        Assert.False(File.Exists(store.FilePath));
    }

    private static string Value(string[] args, string option)
    {
        Assert.Equal(1, args.Count(argument => argument == option));
        int index = Array.IndexOf(args, option);
        Assert.InRange(index + 1, 1, args.Length - 1);
        return args[index + 1];
    }
}

public sealed class PortalUrlCredentialTests
{
    private static string Helper => Path.Combine(AppContext.BaseDirectory, "helpers", OperatingSystem.IsWindows() ? "FakeCore.exe" : "FakeCore");

    [Theory]
    [InlineData("user_password")]
    [InlineData("%75ser_password")]
    [InlineData("PASSWORD")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("user_account")]
    [InlineData("username")]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("authorization")]
    [InlineData("x_unknown_secret")]
    public void CredentialQueryKeysCannotEnterPersistedPortalProfileOrProcessArguments(string key)
    {
        string url = "https://portal.example.test/?" + key + "=synthetic-url-secret";
        using var directory = new TemporaryDirectory();
        var store = new ConfigurationStore(directory.File("credentials.json"), new TestProtector());
        var save = Assert.Throws<ArgumentException>(() => store.Save(new Configuration { Mode = "portal", PortalBase = url }));
        var launch = Assert.Throws<ArgumentException>(() => CoreProcessSession.CreateStartInfo(Helper,
            new("student", "synthetic-process-secret", Mode: "portal", PortalBase: url)));
        Assert.DoesNotContain("synthetic-url-secret", save.Message + launch.Message);
        Assert.DoesNotContain(url, save.Message + launch.Message);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void EncodedCredentialQueryInExistingConfigurationIsPreservedButNeverMigrated()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        string original = """{"mode":"portal","portalBase":"https://portal.example.test/?%75ser_password=synthetic-url-secret","accounts":[]}""";
        File.WriteAllText(path, original);
        var error = Assert.Throws<ArgumentException>(() => new ConfigurationStore(path, new TestProtector()).Load());
        Assert.DoesNotContain("synthetic-url-secret", error.Message);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void PortalDiscoveryAddressMetadataRemainsUsable()
    {
        using var directory = new TemporaryDirectory();
        var configuration = new Configuration { Mode = "portal", PortalBase = "https://portal.example.test/?wlanacip=192.0.2.20&wlanuserip=192.0.2.17" };
        var store = new ConfigurationStore(directory.File("credentials.json"), new TestProtector());
        store.Save(configuration);
        Assert.Equal(configuration.PortalBase, store.Load().PortalBase);
        var args = CoreProcessSession.CreateStartInfo(Helper, new("student", "secret", Mode: "portal", PortalBase: configuration.PortalBase)).ArgumentList;
        Assert.Contains(configuration.PortalBase, args);
    }
}
