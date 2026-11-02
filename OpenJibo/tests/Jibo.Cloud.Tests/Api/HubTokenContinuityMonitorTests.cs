using System.Net.WebSockets;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Moq;

namespace Jibo.Cloud.Tests.Api;

public sealed class HubTokenContinuityMonitorTests
{
    private static readonly HubTokenCredentialBinding Binding =
        new("0123456789abcdef", DateTimeOffset.UtcNow, false);

    [Fact]
    public async Task RechecksWhileReceiveIsIdle_AndAbortsWhenOriginalBindingIsInvalid()
    {
        var store = new Mock<ICloudStateStore>(MockBehavior.Strict);
        var calls = 0;
        store.Setup(s => s.IsHubTokenCredentialBindingCurrentAsync("opaque", Binding, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Interlocked.Increment(ref calls) == 1));
        var socket = new Mock<WebSocket>();
        using var lifetime = new CancellationTokenSource();
        var monitor = new HubTokenContinuityMonitor(store.Object, TimeSpan.FromMilliseconds(10));

        var reason = await monitor.RunAsync(socket.Object, "opaque", Binding, lifetime)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("credential-invalid", reason);
        Assert.Equal(2, calls);
        Assert.True(lifetime.IsCancellationRequested);
        socket.Verify(s => s.Abort(), Times.Once);
    }

    [Fact]
    public async Task ProviderFailureIsClosed_WithoutReturningExceptionDetails()
    {
        var store = new Mock<ICloudStateStore>();
        store.Setup(s => s.IsHubTokenCredentialBindingCurrentAsync(It.IsAny<string>(), Binding,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("private-provider-connection-details"));
        var socket = new Mock<WebSocket>();
        using var lifetime = new CancellationTokenSource();

        var reason = await new HubTokenContinuityMonitor(store.Object)
            .RunAsync(socket.Object, "opaque", Binding, lifetime);

        Assert.Equal("credential-validation-unavailable", reason);
        Assert.True(lifetime.IsCancellationRequested);
        socket.Verify(s => s.Abort(), Times.Once);
    }

    [Fact]
    public async Task UnresponsiveValidatorHasBoundedWait_AndReceivesCancellation()
    {
        var store = new Mock<ICloudStateStore>();
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken validationToken = default;
        store.Setup(s => s.IsHubTokenCredentialBindingCurrentAsync(It.IsAny<string>(), Binding,
                It.IsAny<CancellationToken>()))
            .Returns((string _, HubTokenCredentialBinding _, CancellationToken cancellation) =>
            {
                validationToken = cancellation;
                return waiting.Task; // deliberately ignores cancellation, like a stalled provider
            });
        var socket = new Mock<WebSocket>();
        using var lifetime = new CancellationTokenSource();
        var monitor = new HubTokenContinuityMonitor(store.Object,
            validationTimeout: TimeSpan.FromMilliseconds(20));

        var reason = await monitor.RunAsync(socket.Object, "opaque", Binding, lifetime)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("credential-validation-unavailable", reason);
        Assert.True(validationToken.IsCancellationRequested);
        socket.Verify(s => s.Abort(), Times.Once);
        waiting.TrySetResult(true);
    }

    [Fact]
    public async Task NormalConnectionCancellationStopsMonitorWithoutAbortOrFailure()
    {
        var checkedOnce = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<ICloudStateStore>();
        store.Setup(s => s.IsHubTokenCredentialBindingCurrentAsync(It.IsAny<string>(), Binding,
                It.IsAny<CancellationToken>()))
            .Returns(() => { checkedOnce.TrySetResult(true); return Task.FromResult(true); });
        var socket = new Mock<WebSocket>();
        using var lifetime = new CancellationTokenSource();
        var task = new HubTokenContinuityMonitor(store.Object).RunAsync(socket.Object, "opaque", Binding, lifetime);
        await checkedOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.Cancel();

        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        socket.Verify(s => s.Abort(), Times.Never);
    }
}
