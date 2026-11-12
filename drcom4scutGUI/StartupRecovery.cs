using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace drcom4scutGUI;

/// <summary>Retry only environment failures, before sending any credentials.</summary>
internal static class StartupRecovery
{
    internal static async Task RunAsync(Func<CancellationToken, Task> prepare,
        Action<Exception, int> waiting, CancellationToken cancellationToken,
        int attempts = 13, TimeSpan? retryDelay = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await prepare(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            catch (Exception error) when (attempt < attempts
                && error is CoreProbeException or TimeoutException or IOException)
            {
                waiting(error, attempt);
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }
}
