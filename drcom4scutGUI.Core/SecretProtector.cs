using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace drcom4scutGUI;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>Passwords belong to the current Windows user, not to a portable JSON file.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SeaLoong.drcom4scutGUI.credentials.v2");

    public string Protect(string plaintext)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Unprotect(string ciphertext)
    {
        byte[]? cleartext = null;
        try
        {
            cleartext = ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(cleartext);
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            throw new InvalidDataException("无法为当前 Windows 用户解密密码。请使用原用户配置，或重新输入账号密码。");
        }
        finally { if (cleartext is not null) CryptographicOperations.ZeroMemory(cleartext); }
    }
}
