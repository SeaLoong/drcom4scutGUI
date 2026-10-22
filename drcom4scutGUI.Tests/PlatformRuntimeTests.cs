using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace drcom4scutGUI.Tests;

public sealed class PlatformRuntimeTests
{
    private sealed class FixedKeyStore(byte[] key) : ICredentialKeyStore
    {
        public byte[] GetOrCreateKey() => key.ToArray();
    }

    private sealed class SecretToolFixture : ICredentialCommandRunner
    {
        public readonly List<(string Executable, string[] Arguments, string? Input)> Calls = [];
        public string? Stored;
        public bool Fail;
        public CredentialCommandResult Run(string executable, string[] arguments, string? input)
        {
            Calls.Add((executable, arguments, input));
            if (Fail) return new(1, "synthetic-do-not-display-secret", true);
            if (arguments[0] == "lookup") return Stored is null ? new(1, "", false) : new(0, Stored + "\n", false);
            if (arguments[0] == "store") { Stored = input; return new(0, "", false); }
            throw new InvalidOperationException("Unexpected secret-tool fixture command");
        }
    }

    [Fact]
    public void VaultCiphertextUsesRandomNoncesAndAuthenticatedEncryption()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var protector = new VaultSecretProtector(new FixedKeyStore(key));
        const string password = "synthetic portable secret 🔐";
        string first = protector.Protect(password);
        string second = protector.Protect(password);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(password, first);
        Assert.Equal(password, protector.Unprotect(first));
        Assert.Equal(password, protector.Unprotect(second));
        byte[] damaged = Convert.FromBase64String(first["vault-v1:".Length..]);
        damaged[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => protector.Unprotect("vault-v1:" + Convert.ToBase64String(damaged)));
        Assert.Throws<InvalidDataException>(() => new VaultSecretProtector(new FixedKeyStore(RandomNumberGenerator.GetBytes(32))).Unprotect(first));
        Assert.Throws<InvalidDataException>(() => protector.Unprotect("not-ciphertext"));
    }

    [Fact]
    public void LinuxVaultSuppliesKeyOnlyThroughStdinAndReusesTheStoredValue()
    {
        var command = new SecretToolFixture();
        using var directory = new TemporaryDirectory();
        var store = new SecretToolKeyStore("tests." + Guid.NewGuid().ToString("N"), command, directory.Path);
        byte[] first = store.GetOrCreateKey();
        byte[] second = store.GetOrCreateKey();
        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        var write = Assert.Single(command.Calls, call => call.Arguments[0] == "store");
        Assert.Equal("/usr/bin/secret-tool", write.Executable);
        Assert.Equal(Convert.ToBase64String(first), write.Input);
        Assert.DoesNotContain(write.Input!, write.Arguments);
        Assert.All(command.Calls.Where(call => call.Arguments[0] == "lookup"), call => Assert.Null(call.Input));
    }

    [Fact]
    public void LinuxVaultErrorsNeverExposeCommandOutputOrFallBackToAKeyFile()
    {
        var command = new SecretToolFixture { Fail = true };
        using var directory = new TemporaryDirectory();
        var store = new SecretToolKeyStore("tests." + Guid.NewGuid().ToString("N"), command, directory.Path);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => store.GetOrCreateKey());
        Assert.DoesNotContain("synthetic-do-not-display-secret", error.ToString());
        Assert.Contains("取消记住密码", error.Message);
        Assert.Single(command.Calls);
        command.Fail = false;
        command.Stored = "synthetic-do-not-display-secret";
        error = Assert.Throws<InvalidDataException>(() => store.GetOrCreateKey());
        Assert.DoesNotContain(command.Stored, error.ToString());
    }

    [Fact]
    public void NotRememberingPasswordPersistsOnlyTheAccountAndNeverCallsTheVault()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("accounts.json");
        var configuration = new Configuration
        {
            Account = "student", Accounts = [new Account { Name = "student", Password = "synthetic-ephemeral-secret", RememberPassword = false }]
        };
        var store = new ConfigurationStore(path, new FailingProtector());
        store.Save(configuration);
        Assert.Equal("synthetic-ephemeral-secret", configuration.Accounts[0].Password);
        Assert.DoesNotContain("synthetic-ephemeral-secret", File.ReadAllText(path));
        Account loaded = Assert.Single(store.Load().Accounts);
        Assert.Equal("student", loaded.Name);
        Assert.Equal("", loaded.Password);
        Assert.False(loaded.RememberPassword);
    }

    [Fact]
    public void CoreResolutionUsesOnlyTheInstallationDirectoryAndMacBundleSibling()
    {
        using var directory = new TemporaryDirectory();
        string mac = Path.Combine(directory.Path, "drcom4scutGUI.app", "Contents", "MacOS");
        Directory.CreateDirectory(mac);
        Assert.Equal(Path.Combine(directory.Path, "drcom4scut.exe"), PlatformPaths.ResolveCoreExecutablePath(directory.Path, true, false));
        Assert.Equal(Path.Combine(directory.Path, "drcom4scut"), PlatformPaths.ResolveCoreExecutablePath(directory.Path, false, false));
        Assert.Equal(Path.Combine(mac, "drcom4scut"), PlatformPaths.ResolveCoreExecutablePath(mac, false, true));
        string adjacent = directory.File("drcom4scut");
        File.WriteAllText(adjacent, "fixture core");
        Assert.Equal(adjacent, PlatformPaths.ResolveCoreExecutablePath(mac, false, true));
    }

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    [Fact(SkipUnless = nameof(IsMacOS), Skip = "Native Keychain requires a macOS runner")]
    [SupportedOSPlatform("macos")]
    public void NativeMacKeychainProtectsAndRetrievesTheConfigurationKey()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var store = new MacKeychainKeyStore("SeaLoong.drcom4scutGUI.tests." + Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new VaultSecretProtector(store);
            string ciphertext = protector.Protect("synthetic-native-keychain-secret");
            Assert.DoesNotContain("synthetic-native-keychain-secret", ciphertext);
            Assert.Equal("synthetic-native-keychain-secret", new VaultSecretProtector(store).Unprotect(ciphertext));
        }
        finally { store.DeleteTestItem(); }
    }

    public static bool IsNativeLinuxVault => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DRCOM_TEST_SECRET_SERVICE") == "1";

    [Fact(SkipUnless = nameof(IsNativeLinuxVault), Skip = "Native Secret Service requires the CI desktop keyring session")]
    public void NativeLinuxSecretServiceProtectsAndRetrievesTheConfigurationKey()
    {
        if (!IsNativeLinuxVault) return;
        var store = new SecretToolKeyStore("SeaLoong.drcom4scutGUI.tests." + Guid.NewGuid().ToString("N"));
        try
        {
            string ciphertext = new VaultSecretProtector(store).Protect("synthetic-native-secret-service-secret");
            Assert.DoesNotContain("synthetic-native-secret-service-secret", ciphertext);
            Assert.Equal("synthetic-native-secret-service-secret", new VaultSecretProtector(store).Unprotect(ciphertext));
        }
        finally { store.DeleteTestItem(); }
    }
}
