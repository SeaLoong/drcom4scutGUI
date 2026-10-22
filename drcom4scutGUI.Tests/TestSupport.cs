using System.Security.Cryptography;
using System.Text;

namespace drcom4scutGUI.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drcom-gui-tests-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

// Production uses DPAPI; the deterministic portable substitute lets the same
// persistence contract run on Linux without claiming to test Windows cryptography.
internal sealed class TestProtector : ISecretProtector
{
    public string Protect(string plaintext) => "test:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
    public string Unprotect(string ciphertext)
    {
        if (!ciphertext.StartsWith("test:", StringComparison.Ordinal))
            throw new CryptographicException("Synthetic corrupted ciphertext");
        return Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[5..]));
    }
}

internal sealed class FailingProtector : ISecretProtector
{
    public string Protect(string plaintext) => throw new CryptographicException("Synthetic encryption failure");
    public string Unprotect(string ciphertext) => throw new CryptographicException("Synthetic decryption failure");
}
