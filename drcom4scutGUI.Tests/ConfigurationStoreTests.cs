using System.Security.Cryptography;
using System.Runtime.InteropServices;
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
    public void FailedVaultLoadCannotOverwriteRecoverableAccountsWithAnEmptyFallback()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var protector = new LockableProtector();
        new ConfigurationStore(path, protector).Save(Example());
        byte[] original = File.ReadAllBytes(path);
        var store = new ConfigurationStore(path, protector);
        protector.Locked = true;

        Assert.Throws<CryptographicException>(() => store.Load());
        Assert.True(store.HasLoadFailure);
        Assert.Equal(path, store.LoadFailurePath);
        var fallback = new Configuration { NoUdp = true };
        fallback.Accounts.Add(new Account { Name = "temporary", Password = "synthetic-temporary", RememberPassword = false });
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => store.Save(fallback));
        Assert.Contains("原账号配置未能读取", failure.Message);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(directory.Path));

        protector.Locked = false;
        var recovered = store.Load();
        Assert.False(store.HasLoadFailure);
        Assert.Null(store.LoadFailurePath);
        Assert.Equal("synthetic-private-password", Assert.Single(recovered.Accounts).Password);
        recovered.NoUdp = true;
        store.Save(recovered);
        Assert.True(new ConfigurationStore(path, protector).Load().NoUdp);
    }

    [Theory]
    [InlineData("{not-json", typeof(InvalidDataException))]
    [InlineData("""{"schemaVersion":2,"accounts":[{"name":"student","encryptedPassword":"broken"}]}""", typeof(CryptographicException))]
    public void UnreadableConfigurationMustBeRecoveredOrMovedBeforeFallbackCanBeSaved(string original, Type failureType)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        File.WriteAllText(path, original);
        var store = new ConfigurationStore(path, new TestProtector());

        Assert.Throws(failureType, () => store.Load());
        Assert.True(store.HasLoadFailure);
        Assert.Throws<InvalidOperationException>(() => store.Save(new Configuration()));
        Assert.Equal(original, File.ReadAllText(path));

        string recovery = directory.File("recover-manually.json");
        File.Move(path, recovery);
        Assert.Throws<InvalidOperationException>(() => store.Save(new Configuration()));
        var empty = store.Load();
        Assert.False(store.HasLoadFailure);
        Assert.Empty(empty.Accounts);
        store.Save(empty);
        Assert.Equal(original, File.ReadAllText(recovery));
        Assert.Empty(new ConfigurationStore(path, new TestProtector()).Load().Accounts);
    }

    [Fact]
    public void FailedLegacyMigrationCannotBeHiddenBySavingAnEmptyPrimary()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        string legacy = directory.File("gui.json");
        const string original = """{"username":"student","password":"synthetic-legacy"}""";
        File.WriteAllText(legacy, original);
        var protector = new LockableProtector { Locked = true };
        var store = new ConfigurationStore(path, protector, legacy);

        Assert.Throws<CryptographicException>(() => store.Load());
        Assert.Equal(legacy, store.LoadFailurePath);
        Assert.Throws<InvalidOperationException>(() => store.Save(new Configuration()));
        Assert.False(File.Exists(path));
        Assert.Equal(original, File.ReadAllText(legacy));

        protector.Locked = false;
        var recovered = store.Load();
        Assert.False(store.HasLoadFailure);
        Assert.Equal("synthetic-legacy", Assert.Single(recovered.Accounts).Password);
        Assert.False(File.Exists(legacy));
        Assert.Equal("synthetic-legacy", Assert.Single(new ConfigurationStore(path, protector).Load().Accounts).Password);
    }

    public static bool IsUnprivilegedUnix => !OperatingSystem.IsWindows() && GetEffectiveUserId() != 0;

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [Theory(SkipUnless = nameof(IsUnprivilegedUnix), Skip = "Permission denial requires a non-root Unix runner")]
    [InlineData(false)]
    [InlineData(true)]
    public void InaccessibleConfigurationParentCannotBeTreatedAsMissing(bool legacySource)
    {
        if (OperatingSystem.IsWindows() || GetEffectiveUserId() == 0) return;
        using var directory = new TemporaryDirectory();
        string locked = directory.File("private-accounts");
        string source = Path.Combine(locked, "credentials.json");
        new ConfigurationStore(source, new TestProtector()).Save(Example());
        byte[] original = File.ReadAllBytes(source);
        string path = legacySource ? directory.File("new-credentials.json") : source;
        string legacy = legacySource ? source : directory.File("legacy.json");
        // An unreadable primary must not fall through to an unrelated readable
        // legacy document, whose migration would replace the primary as well.
        if (!legacySource) File.WriteAllText(legacy, """{"accounts":[]}""");
        var store = new ConfigurationStore(path, new TestProtector(), legacy);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.False(File.Exists(source));
            Assert.Throws<UnauthorizedAccessException>(() => store.Load());
            Assert.True(store.HasLoadFailure);
            Assert.Equal(source, store.LoadFailurePath);
            Assert.Throws<InvalidOperationException>(() => store.Save(new Configuration()));
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(locked));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.Equal(original, File.ReadAllBytes(source));
        if (legacySource) Assert.False(File.Exists(path));

        Configuration recovered = store.Load();
        Assert.False(store.HasLoadFailure);
        Assert.Equal("synthetic-private-password", Assert.Single(recovered.Accounts).Password);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationPathThatIsADirectoryCannotBeTreatedAsMissing(bool legacySource)
    {
        using var directory = new TemporaryDirectory();
        string source = directory.File("unexpected-directory");
        Directory.CreateDirectory(source);
        string marker = Path.Combine(source, "existing-data.txt");
        File.WriteAllText(marker, "synthetic-existing-data");
        string path = legacySource ? directory.File("new-credentials.json") : source;
        var store = new ConfigurationStore(path, new TestProtector(), legacySource ? source : null);

        Assert.Throws<UnauthorizedAccessException>(() => store.Load());
        Assert.True(store.HasLoadFailure);
        Assert.Equal(source, store.LoadFailurePath);
        Assert.Throws<InvalidOperationException>(() => store.Save(new Configuration()));
        Assert.Equal("synthetic-existing-data", File.ReadAllText(marker));
        if (legacySource) Assert.False(File.Exists(path));
    }

    [Fact]
    public void ForgettingCurrentPasswordDoesNotReencryptOtherUnchangedAccountsWhenVaultLocks()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var protector = new LockableProtector();
        var configuration = Example();
        configuration.Accounts.Add(new Account { Name = "other", Password = "synthetic-other" });
        new ConfigurationStore(path, protector).Save(configuration);
        var store = new ConfigurationStore(path, protector);
        configuration = store.Load();
        configuration.Find("student")!.RememberPassword = false;
        int calls = protector.ProtectCalls;
        protector.Locked = true;

        store.Save(configuration);

        Assert.Equal(calls, protector.ProtectCalls);
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Assert.Equal("", document.RootElement.GetProperty("accounts")[0].GetProperty("encryptedPassword").GetString());
        protector.Locked = false;
        var saved = new ConfigurationStore(path, protector).Load();
        Assert.Equal("", saved.Find("student")!.Password);
        Assert.False(saved.Find("student")!.RememberPassword);
        Assert.Equal("synthetic-other", saved.Find("other")!.Password);
    }

    [Fact]
    public void EnablingRememberAgainRequiresEncryptionAndKeepsTheSavedForgetChoiceOnFailure()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var protector = new LockableProtector();
        var store = new ConfigurationStore(path, protector);
        var configuration = Example();
        store.Save(configuration);
        configuration.Accounts[0].RememberPassword = false;
        store.Save(configuration);
        byte[] forgotten = File.ReadAllBytes(path);
        configuration.Accounts[0].RememberPassword = true;
        protector.Locked = true;

        Assert.Throws<CryptographicException>(() => store.Save(configuration));
        Assert.Equal(forgotten, File.ReadAllBytes(path));
    }

    [Fact]
    public void ChangedPasswordRequiresEncryptionAndCannotReuseThePreviouslyVerifiedCiphertext()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var protector = new LockableProtector();
        var store = new ConfigurationStore(path, protector);
        store.Save(Example());
        var configuration = store.Load();
        byte[] original = File.ReadAllBytes(path);
        configuration.Accounts[0].Password = "synthetic-changed";
        protector.Locked = true;
        Assert.Throws<CryptographicException>(() => store.Save(configuration));
        Assert.Equal(original, File.ReadAllBytes(path));
        protector.Locked = false;
        store.Save(configuration);
        Assert.Equal("synthetic-changed", new ConfigurationStore(path, protector).Load().Accounts[0].Password);
    }

    [Fact]
    public void ReplacingAnAccountInstanceCannotReuseAnothersCachedCiphertext()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var protector = new LockableProtector();
        var store = new ConfigurationStore(path, protector);
        var configuration = Example();
        store.Save(configuration);
        byte[] original = File.ReadAllBytes(path);
        configuration.Accounts[0] = Example().Accounts[0];
        protector.Locked = true;
        Assert.Throws<CryptographicException>(() => store.Save(configuration));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FreshEncryptionAfterVaultKeyReplacementRefreshesAllRememberedCiphertexts(bool addAccount)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json");
        var keys = new RotatingKeyStore();
        var protector = new VaultSecretProtector(keys);
        var store = new ConfigurationStore(path, protector);
        var configuration = Example();
        configuration.Accounts.Add(new Account { Name = "unchanged", Password = "synthetic-unchanged" });
        store.Save(configuration);
        configuration = store.Load();
        keys.ReplaceKey();
        if (addAccount) configuration.Accounts.Add(new Account { Name = "new", Password = "synthetic-new" });
        else configuration.Accounts[0].Password = "synthetic-changed";

        store.Save(configuration);

        Configuration loaded = new ConfigurationStore(path, protector).Load();
        Assert.Equal("synthetic-unchanged", loaded.Find("unchanged")!.Password);
        Assert.Equal(addAccount ? "synthetic-private-password" : "synthetic-changed", loaded.Find("student")!.Password);
        if (addAccount) Assert.Equal("synthetic-new", loaded.Find("new")!.Password);
    }

    [Fact]
    public void FailedAtomicPublishDoesNotAdvanceTheReusableCiphertext()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json"), originalPath = directory.File("original.json");
        var protector = new LockableProtector();
        var store = new ConfigurationStore(path, protector);
        store.Save(Example());
        var configuration = store.Load();
        string password = configuration.Accounts[0].Password;
        byte[] original = File.ReadAllBytes(path);
        configuration.Accounts[0].Password = "synthetic-not-published";
        File.Move(path, originalPath);
        Directory.CreateDirectory(path);
        Exception? failure = Record.Exception(() => store.Save(configuration));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(originalPath));
        Directory.Delete(path);
        File.Move(originalPath, path);

        configuration.Accounts[0].Password = password;
        protector.Locked = true;
        store.Save(configuration);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyCleanupFailureReturnsMigratedAccountsAndAnActionableWarning(bool accessDenied)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("credentials.json"), legacy = directory.File("gui.json");
        const string original = """{"username":"student","password":"synthetic-legacy","autoLogin":true}""";
        File.WriteAllText(legacy, original);
        var store = new ConfigurationStore(path, new TestProtector(), legacy, _ =>
        {
            if (accessDenied) throw new UnauthorizedAccessException();
            throw new IOException();
        });

        Configuration configuration = store.Load();

        Assert.Equal("student", Assert.Single(configuration.Accounts).Name);
        Assert.Equal("synthetic-legacy", configuration.Accounts[0].Password);
        Assert.True(configuration.Auto);
        Assert.Contains("旧明文文件未能删除", store.LoadWarning);
        Assert.Contains(legacy, store.LoadWarning);
        Assert.Equal(original, File.ReadAllText(legacy));
        Assert.DoesNotContain("synthetic-legacy", File.ReadAllText(path));
        configuration.Auto = false;
        store.Save(configuration);
        var reloaded = new ConfigurationStore(path, new TestProtector()).Load();
        Assert.Equal("student", Assert.Single(reloaded.Accounts).Name);
        Assert.False(reloaded.Auto);
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

    private sealed class LockableProtector : ISecretProtector
    {
        private readonly TestProtector inner = new();
        public bool Locked;
        public int ProtectCalls;
        public string Protect(string plaintext)
        {
            ProtectCalls++;
            if (Locked) throw new CryptographicException("Synthetic locked vault");
            return inner.Protect(plaintext);
        }
        public string Unprotect(string ciphertext) => Locked
            ? throw new CryptographicException("Synthetic locked vault") : inner.Unprotect(ciphertext);
    }

    private sealed class RotatingKeyStore : ICredentialKeyStore
    {
        private byte[] key = RandomNumberGenerator.GetBytes(32);
        public byte[] GetOrCreateKey() => key.ToArray();
        public void ReplaceKey()
        {
            CryptographicOperations.ZeroMemory(key);
            key = RandomNumberGenerator.GetBytes(32);
        }
    }
}
