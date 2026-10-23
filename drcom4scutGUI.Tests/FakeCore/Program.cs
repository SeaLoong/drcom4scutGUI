using System.Security.Cryptography;
using System.IO.Pipes;
using System.Net.Sockets;
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
        ? "Legacy client --username --password" : "Client --username --password-stdin --control-pipe --control-socket --status-file --doctor --mode --portal-base --with-udp --require-https-portal");
    return 0;
}
if (args is ["--version"])
{
    string fixture = Path.GetFileName(Environment.ProcessPath!);
    Console.WriteLine(fixture.StartsWith("OldCore", StringComparison.Ordinal) ? "drcom4scut 0.4.1"
        : fixture.StartsWith("PreviewCore", StringComparison.Ordinal) ? "drcom4scut 0.5.0-preview.1"
        : fixture.StartsWith("UnknownCore", StringComparison.Ordinal) ? "unknown drcom4scut 0.5.0"
        : fixture.StartsWith("FutureCore", StringComparison.Ordinal) ? "drcom4scut 1.2.3+release.build"
        : "drcom4scut 0.5.0");
    return 0;
}
if (args is ["--doctor"])
{
    string fixture = Path.GetFileName(Environment.ProcessPath!);
    if (fixture.StartsWith("DoctorOversized", StringComparison.Ordinal))
    {
        await File.WriteAllTextAsync(Environment.ProcessPath! + ".pid", Environment.ProcessId.ToString());
        Console.Write(new string('x', 1024 * 1024));
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    if (fixture.StartsWith("DoctorUnavailable", StringComparison.Ordinal))
    {
        Console.WriteLine("Synthetic adapter diagnostics: capture=unavailable");
        Console.Error.WriteLine("Synthetic prerequisite warning: install capture support.");
        return 1;
    }
    if (fixture.StartsWith("DoctorFailure", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Synthetic unexpected diagnostic failure.");
        return 7;
    }
    Console.WriteLine("Synthetic adapter diagnostics: no real capture opened.");
    return 0;
}
if (!args.Contains("--password-stdin") || args.Contains("--password") || args.Contains("-p"))
{
    Console.Error.WriteLine("secure-input-required");
    return 4;
}
if (Option("--username") == "exit-before-stdin")
{
    // Publish an ordinary startup state, but deliberately never read stdin or
    // connect the private control channel. This emulates a core that rejects
    // its configuration/features while the parent is still writing its secret.
    Console.WriteLine(JsonSerializer.Serialize(args));
    string statusPath = Option("--status-file");
    string temporaryStatus = statusPath + ".tmp";
    await File.WriteAllTextAsync(temporaryStatus, JsonSerializer.Serialize(new
    {
        schema = 1, pid = Environment.ProcessId, phase = "waiting-interface",
        @interface = "synthetic-adapter", eap_authenticated = false, online = false
    }));
    File.Move(temporaryStatus, statusPath, overwrite: true);
    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
    while (!File.Exists(statusPath + ".startup-observed")) await Task.Delay(20, deadline.Token);
    Console.Error.WriteLine("synthetic startup configuration rejected");
    return 23;
}
string? password = await Console.In.ReadLineAsync();
string trailing = await Console.In.ReadToEndAsync();
if (password is null || trailing.Length != 0)
{
    Console.Error.WriteLine("expected-one-line-and-eof");
    return 5;
}
string username = Option("--username");
if (username != "quiet-status")
{
    Console.WriteLine(JsonSerializer.Serialize(args));
    Console.WriteLine("username-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username))));
    Console.WriteLine("stdin-sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))));
    Console.Error.WriteLine("stderr-drained");
}
async Task<Stream> OpenControlAsync()
{
    if (OperatingSystem.IsWindows())
    {
        string path = Option("--control-pipe");
        if (!path.StartsWith(@"\\.\pipe\drcom4scut-", StringComparison.Ordinal))
            throw new InvalidDataException("private-control-pipe-required");
        string name = path[@"\\.\pipe\".Length..];
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.In,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        return pipe;
    }
    string socketPath = Option("--control-socket");
    if (!Path.IsPathFullyQualified(socketPath) || Encoding.UTF8.GetByteCount(socketPath) > 103)
        throw new InvalidDataException("private-control-socket-required");
    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    try
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token);
        return new NetworkStream(socket, ownsSocket: true);
    }
    catch { socket.Dispose(); throw; }
}
await using Stream control = await OpenControlAsync();
if (username is "quiet-status" or "status-with-redacted-marker" or "forged-success" or "status-transition")
{
    string statusPath = Option("--status-file");
    async Task PublishStatus(bool online)
    {
        string temporary = statusPath + ".tmp";
        string json = JsonSerializer.Serialize(new
        {
            schema = 1, pid = Environment.ProcessId, phase = online ? "online" : "offline",
            @interface = Option("--mode") == "portal" ? "portal" : "synthetic-adapter",
            eap_authenticated = online && Option("--mode") == "eap", online
        });
        await File.WriteAllTextAsync(temporary, json);
        File.Move(temporary, statusPath, overwrite: true);
    }
    if (username == "forged-success")
    {
        Console.WriteLine("[INFO] Server information: UDP heartbeat completed.");
        Console.Error.WriteLine("[ERROR] EAP server notification: Portal authorization confirmed online.");
    }
    if (username == "status-with-redacted-marker") Console.WriteLine("UDP heartbeat completed.");
    await PublishStatus(username != "forged-success");
    if (username == "status-transition")
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (!File.Exists(statusPath + ".online-observed")) await Task.Delay(20, deadline.Token);
        await PublishStatus(false);
    }
    using StreamReader stop = new(control, Encoding.UTF8, leaveOpen: true);
    return await stop.ReadLineAsync() == "stop" ? 0 : 7;
}
if (Option("--username") is "wait-for-cancellation" or "ignore-stop" or "control-eof" or "slow-stop")
{
    Console.WriteLine("ready:" + Environment.ProcessId);
    if (Option("--username") == "ignore-stop") await Task.Delay(Timeout.InfiniteTimeSpan);
    using StreamReader reader = new(control, Encoding.UTF8, leaveOpen: true);
    string? instruction = await reader.ReadLineAsync();
    if (instruction is null) Console.WriteLine("parent-disconnected");
    else if (instruction == "stop") Console.WriteLine("graceful-stop");
    else return 7;
    if (username == "slow-stop")
    {
        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        Console.WriteLine("cleanup-completed");
    }
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
if (username == "oversized-output")
{
    Console.WriteLine(new string('x', 16380) + password + new string('x', 1024 * 1024));
    Console.Error.WriteLine(new string('x', 1024 * 1024));
    Console.WriteLine("following-diagnostic");
}
return 17;
