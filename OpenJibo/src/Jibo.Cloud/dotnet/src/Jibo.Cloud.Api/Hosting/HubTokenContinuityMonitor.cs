using System.Net.WebSockets;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Api.Hosting;

/// <summary>
/// Ends a credential-bound socket when its original binding can no longer be
/// authoritatively revalidated. Does not establish ownership or co-presence.
/// </summary>
internal sealed class HubTokenContinuityMonitor(
    ICloudStateStore store,
    TimeSpan? interval = null,
    TimeSpan? validationTimeout = null)
{
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultValidationTimeout = TimeSpan.FromSeconds(5);

    internal async Task<string?> RunAsync(WebSocket socket, string token, HubTokenCredentialBinding expected,
        CancellationTokenSource connectionLifetime)
    {
        var cancellation = connectionLifetime.Token;
        using var timer = new PeriodicTimer(interval ?? DefaultInterval);
        while (!cancellation.IsCancellationRequested)
        {
            string? failure = null;
            using var validation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            validation.CancelAfter(validationTimeout ?? DefaultValidationTimeout);
            try
            {
                var current = await store.IsHubTokenCredentialBindingCurrentAsync(token, expected, validation.Token)
                    .WaitAsync(validationTimeout ?? DefaultValidationTimeout, cancellation);
                if (!current) failure = "credential-invalid";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception)
            {
                // Never record exception text: provider messages can contain connection details.
                failure = "credential-validation-unavailable";
            }

            if (failure is not null)
            {
                validation.Cancel();
                connectionLifetime.Cancel();
                // Abort is safe alongside an in-flight send/receive. A close handshake
                // would race the reply writer and could wait indefinitely on the peer.
                socket.Abort();
                return failure;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellation)) return null;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return null;
            }
        }
        return null;
    }
}
