using System.Security.Cryptography;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// This is a deliberately independent process fixture. It accepts the same secure
// input channel as the real core, reports only a digest of its synthetic secret,
// and can remain running so cancellation is exercised against an actual child.
// Windows consoles may use CP437; the real Rust core decodes stdin as UTF-8.
// Pin both fixture streams to that protocol rather than inheriting the console.
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
string Option(string key)
{
    int position = Array.IndexOf(args, key);
    return position >= 0 && position + 1 < args.Length ? args[position + 1] : "";
}
if (args is ["--help"])
{
    Console.WriteLine(Path.GetFileName(Environment.ProcessPath!).StartsWith("Legacy", StringComparison.Ordinal)
        ? "Legacy client --username --password" : "Client --username --password-stdin --control-pipe --doctor --mode --portal-base --with-udp --require-https-portal");
    return 0;
}
if (args is ["--version"])
{
    Console.WriteLine("drcom4scut 0.4.0");
    return 0;
}
if (args is ["--doctor"])
{
    Console.WriteLine("Synthetic adapter diagnostics: no real capture opened.");
    return 0;
}
if (!args.Contains("--password-stdin") || args.Contains("--password") || args.Contains("-p"))
{
    Console.Error.WriteLine("secure-input-required");
    return 4;
}
string? password = await Console.In.ReadLineAsync();
string trailing = await Console.In.ReadToEndAsync();
if (password is null || trailing.Length != 0)
{
    Console.Error.WriteLine("expected-one-line-and-eof");
    return 5;
}
Console.WriteLine(JsonSerializer.Serialize(args));
Console.WriteLine("stdin-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))));
Console.Error.WriteLine("stderr-drained");
string controlPath = Option("--control-pipe");
if (!controlPath.StartsWith(@"\\.\pipe\drcom4scut-", StringComparison.Ordinal))
{
    Console.Error.WriteLine("private-control-pipe-required");
    return 6;
}
string pipeName = controlPath[@"\\.\pipe\".Length..];
await using var control = new NamedPipeClientStream(".", pipeName, PipeDirection.In,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await control.ConnectAsync(5000);
if (Option("--username") is "wait-for-cancellation" or "ignore-stop" or "control-eof")
{
    Console.WriteLine("ready:" + Environment.ProcessId);
    if (Option("--username") == "ignore-stop") await Task.Delay(Timeout.InfiniteTimeSpan);
    using StreamReader reader = new(control, Encoding.UTF8, leaveOpen: true);
    string? instruction = await reader.ReadLineAsync();
    if (instruction is null) Console.WriteLine("parent-disconnected");
    else if (instruction == "stop") Console.WriteLine("graceful-stop");
    else return 7;
    return 0;
}
if (Option("--username") == "echo-password")
{
    Console.WriteLine("external-core-echo:" + password);
    Console.Error.WriteLine("external-error-echo:" + password);
}
if (Option("--username") == "flood-output")
{
    for (int i = 0; i < 5000; i++)
    {
        Console.WriteLine("stdout-line-" + i);
        Console.Error.WriteLine("stderr-line-" + i);
    }
}
return 17;
