using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

/// <summary>The parent owns this stop-only connection until its child exits.</summary>
internal sealed class PrivateControlServer : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly NamedPipeServerStream? pipe;
    private readonly Socket? listener;
    private readonly string? socketPath;
    private Socket? peer;
    private bool disposed;
    public Task Connection { get; }

    public PrivateControlServer(ProcessStartInfo start, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            string name = "drcom4scut-" + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            start.ArgumentList.Add("--control-pipe");
            start.ArgumentList.Add(@"\\.\pipe\" + name);
            Connection = pipe.WaitForConnectionAsync(lifetime.Token);
        }
        else
        {
            socketPath = Path.Combine(directory, "c.sock");
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                listener.Listen(1);
                start.ArgumentList.Add("--control-socket");
                start.ArgumentList.Add(socketPath);
                Connection = AcceptAsync();
            }
            catch
            {
                listener.Dispose();
                File.Delete(socketPath);
                lifetime.Dispose();
                throw;
            }
        }
    }

    private async Task AcceptAsync() => peer = await listener!.AcceptAsync(lifetime.Token).ConfigureAwait(false);

    public async Task SendStopAsync(CancellationToken token)
    {
        await Connection.WaitAsync(token).ConfigureAwait(false);
        if (pipe is not null)
        {
            await pipe.WriteAsync("stop\n"u8.ToArray(), token).ConfigureAwait(false);
            await pipe.FlushAsync(token).ConfigureAwait(false);
        }
        else
        {
            ReadOnlyMemory<byte> message = "stop\n"u8.ToArray();
            while (message.Length > 0)
            {
                int written = await peer!.SendAsync(message, SocketFlags.None, token).ConfigureAwait(false);
                if (written == 0) throw new IOException("私有停止通道已关闭。");
                message = message[written..];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try { await Connection.ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        peer?.Dispose();
        listener?.Dispose();
        if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
        if (socketPath is not null) File.Delete(socketPath);
        lifetime.Dispose();
    }
}
