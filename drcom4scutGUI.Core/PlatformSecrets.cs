using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("drcom4scutGUI.Tests")]

namespace drcom4scutGUI;

public static class SecretProtectorFactory
{
    public static ISecretProtector Create()
    {
        if (OperatingSystem.IsWindows()) return new DpapiSecretProtector();
        if (OperatingSystem.IsMacOS()) return new VaultSecretProtector(new MacKeychainKeyStore());
        if (OperatingSystem.IsLinux()) return new VaultSecretProtector(new SecretToolKeyStore());
        throw new PlatformNotSupportedException("此系统没有受支持的密码保险库，请取消记住密码。");
    }

    public static string Description => OperatingSystem.IsWindows() ? "密码由当前 Windows 用户的 DPAPI 保护。"
        : OperatingSystem.IsMacOS() ? "密码由当前用户的 macOS 登录钥匙串保护。"
        : "密码由当前桌面的 Secret Service 钥匙环保护；不可用时可取消记住密码。";
}

internal interface ICredentialKeyStore
{
    byte[] GetOrCreateKey();
}

/// <summary>Only authenticated ciphertext is written to configuration; its key stays in the OS vault.</summary>
internal sealed class VaultSecretProtector(ICredentialKeyStore keyStore) : ISecretProtector
{
    private const string Prefix = "vault-v1:";
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("SeaLoong.drcom4scutGUI.credentials.v3");

    public string Protect(string plaintext)
    {
        byte[] key = keyStore.GetOrCreateKey();
        byte[] clear = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            byte[] payload = new byte[12 + 16 + clear.Length];
            RandomNumberGenerator.Fill(payload.AsSpan(0, 12));
            using AesGcm cipher = new(key, 16);
            cipher.Encrypt(payload.AsSpan(0, 12), clear, payload.AsSpan(28), payload.AsSpan(12, 16), Context);
            return Prefix + Convert.ToBase64String(payload);
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(key); }
    }

    public string Unprotect(string ciphertext)
    {
        byte[]? clear = null;
        byte[]? key = null;
        try
        {
            if (!ciphertext.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException();
            byte[] payload = Convert.FromBase64String(ciphertext[Prefix.Length..]);
            if (payload.Length < 28) throw new FormatException();
            key = keyStore.GetOrCreateKey();
            clear = new byte[payload.Length - 28];
            using AesGcm cipher = new(key, 16);
            cipher.Decrypt(payload.AsSpan(0, 12), payload.AsSpan(28), payload.AsSpan(12, 16), clear, Context);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception failure) when (failure is FormatException or CryptographicException)
        {
            throw new InvalidDataException("当前用户的系统钥匙环无法解密保存的密码，请重新输入密码。");
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }
}

internal sealed record CredentialCommandResult(int ExitCode, string Output, bool HasError);
internal interface ICredentialCommandRunner
{
    CredentialCommandResult Run(string executable, string[] arguments, string? input);
}

internal sealed class CredentialCommandRunner : ICredentialCommandRunner
{
    public CredentialCommandResult Run(string executable, string[] arguments, string? input)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false)
        };
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new IOException();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (input is not null) process.StandardInput.WriteLine(input);
            process.StandardInput.Close();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new IOException();
            }
            return new CredentialCommandResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult().Length > 0);
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Command diagnostics may contain returned secret material. Never
            // expose the command's stdout/stderr or supplied stdin in errors.
            throw new InvalidDataException("系统钥匙环不可用。Linux 请安装 libsecret 的 secret-tool 并解锁桌面钥匙环，或取消记住密码。");
        }
    }
}

internal sealed class SecretToolKeyStore(string service = "SeaLoong.drcom4scutGUI", ICredentialCommandRunner? command = null,
    string? lockDirectory = null) : ICredentialKeyStore
{
    private readonly ICredentialCommandRunner runner = command ?? new CredentialCommandRunner();
    private static readonly string[] Purpose = ["purpose", "configuration-key-v3"];

    public byte[] GetOrCreateKey()
    {
        string directory = lockDirectory ?? PlatformPaths.ConfigurationDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string name = ".vault-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service)))[..24] + ".lock";
            FileStreamOptions options = new() { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                FileStream gate;
                try { gate = new FileStream(Path.Combine(directory, name), options); }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(15)) { Thread.Sleep(25); continue; }
                using (gate) return ReadOrCreateLocked();
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw Unavailable();
        }
    }

    private byte[] ReadOrCreateLocked()
    {
        CredentialCommandResult found = runner.Run("/usr/bin/secret-tool", ["lookup", "application", service, .. Purpose], null);
        if (found.ExitCode == 0) return Decode(found.Output.Trim());
        if (found.ExitCode != 1 || found.HasError) throw Unavailable();
        byte[] candidate = RandomNumberGenerator.GetBytes(32);
        try
        {
            CredentialCommandResult stored = runner.Run("/usr/bin/secret-tool",
                ["store", "--label=drcom4scutGUI configuration encryption", "application", service, .. Purpose], Convert.ToBase64String(candidate));
            if (stored.ExitCode != 0 || stored.HasError) throw Unavailable();
            // The user-private file lock serializes first creation;
            // also verify the service value instead of trusting an exit code.
            CredentialCommandResult verified = runner.Run("/usr/bin/secret-tool", ["lookup", "application", service, .. Purpose], null);
            if (verified.ExitCode != 0 || verified.HasError) throw Unavailable();
            return Decode(verified.Output.Trim());
        }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    internal void DeleteTestItem()
    {
        CredentialCommandResult result = runner.Run("/usr/bin/secret-tool", ["clear", "application", service, .. Purpose], null);
        if (result.ExitCode != 0 || result.HasError) throw Unavailable();
    }

    private static byte[] Decode(string text)
    {
        try
        {
            byte[] key = Convert.FromBase64String(text);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
        }
        catch (FormatException) { }
        throw Unavailable();
    }

    private static InvalidDataException Unavailable() => new("系统钥匙环不可用或密钥已损坏，请解锁 Secret Service 钥匙环，或取消记住密码。");
}

[SupportedOSPlatform("macos")]
internal sealed class MacKeychainKeyStore(string service = "SeaLoong.drcom4scutGUI", string account = "configuration-key-v3") : ICredentialKeyStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private readonly byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
    private readonly byte[] accountBytes = Encoding.UTF8.GetBytes(account);

    public byte[] GetOrCreateKey()
    {
        byte[]? existing = Read();
        if (existing is not null) return existing;
        byte[] key = RandomNumberGenerator.GetBytes(32);
        try
        {
            int status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes,
                (uint)accountBytes.Length, accountBytes, (uint)key.Length, key, IntPtr.Zero);
            if (status is not (0 or -25299)) throw Unavailable();
            return Read() ?? throw Unavailable();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private byte[]? Read()
    {
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes, out uint length, out IntPtr content, IntPtr.Zero);
        if (status == -25300) return null;
        if (status != 0) throw Unavailable();
        try
        {
            if (length != 32 || content == IntPtr.Zero) throw Unavailable();
            byte[] key = new byte[32];
            Marshal.Copy(content, key, 0, key.Length);
            return key;
        }
        finally { SecKeychainItemFreeContent(IntPtr.Zero, content); }
    }

    internal void DeleteTestItem()
    {
        int status = FindItem(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes,
            (uint)accountBytes.Length, accountBytes, IntPtr.Zero, IntPtr.Zero, out IntPtr item);
        if (status == -25300) return;
        if (status != 0) throw Unavailable();
        try { if (SecKeychainItemDelete(item) != 0) throw Unavailable(); }
        finally { CFRelease(item); }
    }

    private static InvalidDataException Unavailable() => new("macOS 登录钥匙串不可用或密钥已损坏，请解锁当前用户的钥匙串，或取消记住密码。");

    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, IntPtr item);
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")] private static extern int FindItem(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, IntPtr passwordLength, IntPtr password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(Foundation)] private static extern void CFRelease(IntPtr value);
}
