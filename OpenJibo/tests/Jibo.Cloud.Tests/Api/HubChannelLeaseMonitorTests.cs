using System.Net.WebSockets;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.Api;

public sealed class HubChannelLeaseMonitorTests
{
    [Fact]
    public async Task StalledRenewalCancelsWorkAndAbortsWithoutWaitingForProviderCompletion()
    {
        var store = HubChannelLeasePolicyTests.WorkingStore();
        var pending = new TaskCompletionSource<HubChannelLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        store.Setup(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .Returns((byte[] _, HubChannel _, Guid _, long _, CancellationToken cancellation) => { observed = cancellation; return pending.Task; });
        using var policy = new HubChannelLeasePolicy(store.Object, new byte[32], timeout: TimeSpan.FromMilliseconds(20));
        await using var lease = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        using var lifetime = new CancellationTokenSource(); var socket = new Mock<WebSocket>();
        await new HubChannelLeaseMonitor().RunAsync(socket.Object, lease, lifetime).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(lifetime.IsCancellationRequested); Assert.True(observed.IsCancellationRequested);
        Assert.Equal("lease-unavailable", lease.FailureReason); socket.Verify(s => s.Abort(), Times.Once);
        pending.SetResult(new HubChannelLease(1, DateTimeOffset.UtcNow.AddSeconds(30)));
        Assert.False(await lease.CheckAsync(default));
    }

    [Fact]
    public async Task OrdinaryCancellationStopsAndJoinsMonitorWithoutAborting()
    {
        var store = HubChannelLeasePolicyTests.WorkingStore();
        var renewed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Setup(s => s.TryRenewAsync(It.IsAny<byte[]>(), It.IsAny<HubChannel>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .Returns(() => { renewed.TrySetResult(true); return Task.FromResult<HubChannelLease?>(new(1, DateTimeOffset.UtcNow.AddSeconds(30))); });
        using var policy = new HubChannelLeasePolicy(store.Object, new byte[32]);
        await using var lease = (await policy.AcquireAsync("opaque", HubChannel.Listen, default))!;
        using var lifetime = new CancellationTokenSource(); var socket = new Mock<WebSocket>();
        var monitoring = new HubChannelLeaseMonitor().RunAsync(socket.Object, lease, lifetime);
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5)); lifetime.Cancel();
        await monitoring.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(lease.FailureReason); socket.Verify(s => s.Abort(), Times.Never);
    }
}
