using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace drcom4scutGUI;

public sealed class Account : IComparable<Account>
{
    public string Name { get; set; } = "";
    [JsonIgnore]
    public string Password { get; set; } = "";
    public bool RememberPassword { get; set; } = true;
    public string IP { get; set; } = "";
    public int CompareTo(Account? other) => string.Compare(Name, other?.Name, StringComparison.Ordinal);
}

public sealed class Configuration
{
    public List<Account> Accounts { get; set; } = [];
    public string Mac { get; set; } = "";
    public string Account { get; set; } = "";
    public bool Auto { get; set; }
    public bool NoUdp { get; set; }
    public string Mode { get; set; } = "eap";
    public string PortalBase { get; set; } = "";
    public string PortalApi { get; set; } = "";
    public string PortalAcIp { get; set; } = "";
    public string PortalClientIp { get; set; } = "";
    public bool AllowHttp { get; set; }
    public Account? Find(string name) => Accounts.Find(account => account.Name == name);
}

/// <summary>
/// Stores protected passwords only when requested. Legacy files are replaced only
/// after every account has been protected and the new document is complete.
/// </summary>
public sealed class ConfigurationStore
{
    private readonly ISecretProtector protector;
    private readonly string? legacyPath;
    private readonly Action<string> deleteLegacy;
    private readonly Dictionary<Account, ProtectedPassword> protectedPasswords = new(ReferenceEqualityComparer.Instance);
    private sealed record ProtectedPassword(string Password, string Ciphertext);

    public string FilePath { get; }
    public string? LoadWarning { get; private set; }

    public ConfigurationStore(string path, ISecretProtector protector, string? legacyPath = null)
        : this(path, protector, legacyPath, File.Delete) { }

    internal ConfigurationStore(string path, ISecretProtector protector, string? legacyPath, Action<string> deleteLegacy)
    {
        FilePath = Path.GetFullPath(path);
        this.protector = protector;
        this.legacyPath = legacyPath;
        this.deleteLegacy = deleteLegacy;
    }

    public Configuration Load()
    {
        LoadWarning = null;
        string? source = File.Exists(FilePath) ? FilePath :
            legacyPath is not null && File.Exists(legacyPath) ? Path.GetFullPath(legacyPath) : null;
        if (source is null) { protectedPasswords.Clear(); return new Configuration(); }
        Dictionary<Account, ProtectedPassword> loadedPasswords = new(ReferenceEqualityComparer.Instance);
        Configuration configuration;
        bool requiresMigration;
        bool plaintextSource;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(source));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("配置必须是 JSON 对象。");
            int schema = root.TryGetProperty("schemaVersion", out JsonElement version) ? version.GetInt32() : 1;
            if (schema is not (1 or 2)) throw new InvalidDataException("配置版本不受支持，请更新客户端。");
            plaintextSource = schema == 1;
            requiresMigration = schema != 2 || source != FilePath;
            configuration = new Configuration
            {
                Mac = ReadString(root, "mac"),
                Account = ReadString(root, "account"),
                Auto = ReadBoolean(root, schema == 1 && !root.TryGetProperty("accounts", out _) ? "autoLogin" : "auto"),
                NoUdp = ReadBoolean(root, "noUdp"),
                Mode = ReadString(root, "mode") is { Length: > 0 } mode ? mode : "eap",
                PortalBase = ReadString(root, "portalBase"),
                PortalApi = ReadString(root, "portalApi"),
                PortalAcIp = ReadString(root, "portalAcIp"),
                PortalClientIp = ReadString(root, "portalClientIp"),
                AllowHttp = ReadBoolean(root, "allowHttp")
            };
            if (root.TryGetProperty("accounts", out JsonElement accounts))
            {
                if (accounts.ValueKind != JsonValueKind.Array) throw new InvalidDataException("账号列表格式不正确。");
                foreach (JsonElement value in accounts.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Null) continue;
                    string name = ReadString(value, "name");
                    if (string.IsNullOrEmpty(name) || configuration.Find(name) is not null) continue;
                    bool remember = !value.TryGetProperty("rememberPassword", out _) || ReadBoolean(value, "rememberPassword");
                    string encrypted = schema == 2 ? ReadRequiredString(value, "encryptedPassword") : "";
                    string password = schema == 2
                        ? remember && encrypted.Length > 0 ? protector.Unprotect(encrypted) : ""
                        : ReadString(value, "password");
                    Account account = new() { Name = name, Password = password, RememberPassword = remember, IP = ReadString(value, "ip") };
                    configuration.Accounts.Add(account);
                    if (schema == 2 && remember && encrypted.Length > 0)
                        loadedPasswords[account] = new ProtectedPassword(password, encrypted);
                }
            }
            else if (schema == 1)
            {
                string name = ReadString(root, "username").Trim();
                if (name.Length > 0)
                {
                    configuration.Accounts.Add(new Account { Name = name, Password = ReadString(root, "password"), IP = ReadString(root, "ip").Trim() });
                    configuration.Account = name;
                }
            }
            else throw new InvalidDataException("加密配置中缺少账号列表。");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            // JSON diagnostics can contain plaintext source fragments from old files.
            throw new InvalidDataException("配置文件格式不正确，原文件未更改。");
        }
        PortalOptions.ValidateStored(configuration);
        protectedPasswords.Clear();
        foreach (var entry in loadedPasswords) protectedPasswords.Add(entry.Key, entry.Value);
        if (requiresMigration)
        {
            Save(configuration);
            if (source != FilePath)
            {
                // Do not retain a plaintext .bak alongside the encrypted migration.
                try { deleteLegacy(source); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    LoadWarning = "账号已迁移到加密配置，但旧" + (plaintextSource ? "明文" : "账号")
                        + "文件未能删除。请手动删除旧文件：\n" + source;
                }
            }
        }
        return configuration;
    }

    public void Save(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        PortalOptions.ValidateStored(configuration);
        bool refreshRememberedPasswords = configuration.Accounts.Any(account => account.RememberPassword && account.Password.Length > 0
            && (!protectedPasswords.TryGetValue(account, out ProtectedPassword? saved)
                || !string.Equals(saved.Password, account.Password, StringComparison.Ordinal)));
        Dictionary<Account, ProtectedPassword> pendingPasswords = new(ReferenceEqualityComparer.Instance);
        string Protect(Account account)
        {
            string password = account.Password;
            if (!account.RememberPassword || password.Length == 0) return "";
            // A loaded, unchanged secret already has authenticated ciphertext.
            // Preference changes must not require unlocking every other account.
            // A fresh secret can create a replacement OS-vault key. Refresh the
            // entire remembered set together so one file never mixes key epochs.
            string ciphertext = !refreshRememberedPasswords && protectedPasswords.TryGetValue(account, out ProtectedPassword? saved)
                && string.Equals(saved.Password, password, StringComparison.Ordinal)
                    ? saved.Ciphertext : protector.Protect(password);
            pendingPasswords[account] = new ProtectedPassword(password, ciphertext);
            return ciphertext;
        }
        var encrypted = new StoredConfiguration
        {
            Mac = configuration.Mac,
            Account = configuration.Account,
            Auto = configuration.Auto,
            NoUdp = configuration.NoUdp,
            Mode = configuration.Mode,
            PortalBase = configuration.PortalBase,
            PortalApi = configuration.PortalApi,
            PortalAcIp = configuration.PortalAcIp,
            PortalClientIp = configuration.PortalClientIp,
            AllowHttp = configuration.AllowHttp,
            Accounts = configuration.Accounts.Select(account => new StoredAccount
            {
                Name = account.Name,
                EncryptedPassword = Protect(account),
                RememberPassword = account.RememberPassword,
                IP = account.IP
            }).ToList()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(encrypted, new JsonSerializerOptions { WriteIndented = true });
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
            protectedPasswords.Clear();
            foreach (var entry in pendingPasswords) protectedPasswords.Add(entry.Key, entry.Value);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ReadRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException("加密账号记录不完整，原文件未更改。");

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("配置字段类型不正确。");
    }

    private static bool ReadBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("配置字段类型不正确。")
        };
    }

    private sealed class StoredConfiguration
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 2;
        [JsonPropertyName("accounts")] public List<StoredAccount> Accounts { get; set; } = [];
        [JsonPropertyName("mac")] public string Mac { get; set; } = "";
        [JsonPropertyName("account")] public string Account { get; set; } = "";
        [JsonPropertyName("auto")] public bool Auto { get; set; }
        [JsonPropertyName("noUdp")] public bool NoUdp { get; set; }
        [JsonPropertyName("mode")] public string Mode { get; set; } = "eap";
        [JsonPropertyName("portalBase")] public string PortalBase { get; set; } = "";
        [JsonPropertyName("portalApi")] public string PortalApi { get; set; } = "";
        [JsonPropertyName("portalAcIp")] public string PortalAcIp { get; set; } = "";
        [JsonPropertyName("portalClientIp")] public string PortalClientIp { get; set; } = "";
        [JsonPropertyName("allowHttp")] public bool AllowHttp { get; set; }
    }
    private sealed class StoredAccount
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("encryptedPassword")] public string EncryptedPassword { get; set; } = "";
        [JsonPropertyName("rememberPassword")] public bool RememberPassword { get; set; } = true;
        [JsonPropertyName("ip")] public string IP { get; set; } = "";
    }
}
