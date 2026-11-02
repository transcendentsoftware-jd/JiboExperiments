using System.Net.WebSockets;

namespace Jibo.Cloud.Api.Hosting;

internal sealed class HubChannelLeaseMonitor(TimeSpan? interval = null)
{
    internal async Task RunAsync(WebSocket socket, HubChannelLeaseHandle lease, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                if (!await lease.RenewAsync(lifetime.Token))
                {
                    lifetime.Cancel(); socket.Abort(); return;
                }
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
}
