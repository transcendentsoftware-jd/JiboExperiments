using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Persistence;
using Npgsql;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class RuntimeUsageEventTests
{
    private static RuntimeUsageEvent Create(
        byte[]? subject = null, Guid? id = null, DateTimeOffset? occurred = null,
        string environment = "staging", long successful = 1, long failed = 0,
        long requests = 0, long requestBytes = 0, long inboundMessages = 0,
        string? reason = null) =>
        new(subject ?? new byte[32], id ?? Guid.NewGuid(), occurred ?? DateTimeOffset.UtcNow,
            environment, successful, failed, requests, requestBytes, 0,
            inboundMessages, 0, 0, 0, 0, reason);

    [Fact]
    public void Constructor_RejectsInvalidIdentityTimestampAndEnvironment()
    {
        Assert.Throws<ArgumentException>(() => Create(subject: new byte[31]));
        Assert.Throws<ArgumentException>(() => Create(id: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(occurred: DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => Create(environment: "Staging"));
        Assert.Throws<ArgumentException>(() => Create(environment: "staging\n"));
        Assert.Throws<ArgumentException>(() => Create(environment: "staging'; DROP TABLE x;--"));
    }

    [Fact]
    public void Constructor_EnforcesTypedBoundsAndIncompleteMarker()
    {
        Assert.Throws<ArgumentException>(() => Create(successful: 1, failed: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(requests: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(requestBytes: 1_073_741_825));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(inboundMessages: 10_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(successful: -1));
        Assert.Throws<ArgumentException>(() => Create(successful: 0));
        Assert.Throws<ArgumentException>(() => Create(successful: 0, reason: "Bad Reason"));
        Assert.Throws<ArgumentException>(() => Create(successful: 0, reason: "valid\n"));
        Assert.Equal("source-write-failed", Create(successful: 0, reason: "source-write-failed").IncompleteReasonCode);
    }

    [Fact]
    public void SourceHmac_IsDefensivelyCopied()
    {
        var source = new byte[32];
        source[0] = 7;
        var usageEvent = Create(subject: source);
        source[0] = 8;
        var exposed = usageEvent.SourceSubjectHmac.ToArray();
        exposed[0] = 9;
        Assert.Equal(7, usageEvent.SourceSubjectHmac.Span[0]);
    }

    [Fact]
    public void EventTime_IsNormalizedForDatabaseReplay()
    {
        var timestamp = DateTimeOffset.UtcNow.AddTicks(7);
        var usageEvent = Create(occurred: timestamp);
        Assert.Equal(0, usageEvent.OccurredUtc.Ticks % 10);
        Assert.Equal(timestamp.Ticks - timestamp.Ticks % 10, usageEvent.OccurredUtc.Ticks);
    }

    [Fact]
    public void Writer_RejectsUnsafeOrTruncatedSchemaName()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        Assert.Throws<ArgumentException>(() => new PostgreSqlRuntimeUsageEventWriter(dataSource, "state\n"));
        Assert.Throws<ArgumentException>(() => new PostgreSqlRuntimeUsageEventWriter(dataSource, new string('a', 64)));
        Assert.Throws<ArgumentException>(() => new PostgreSqlRuntimeUsageEventWriter(dataSource, "other;drop"));
    }
}
