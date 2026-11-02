using Jibo.Cloud.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Jibo.Cloud.Infrastructure.Persistence;

/// <summary>Unregistered lease foundation. A dedicated function-only identity must be provisioned separately.</summary>
public sealed class PostgreSqlHubChannelLeaseStore : IHubChannelLeaseStore, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    public PostgreSqlHubChannelLeaseStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Lease connection is required.");
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 2, Timeout = 2, CommandTimeout = 2,
            ApplicationName = "OpenJibo.HubChannelLease"
        };
        if (options.SslMode != SslMode.VerifyFull && options.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new ArgumentException("Lease connection requires verified TLS outside loopback.");
        _source = NpgsqlDataSource.Create(options.ConnectionString);
    }

    public async Task<HubChannelLease> AcquireAsync(byte[] tokenDigest, HubChannel channel, Guid owner,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(1, tokenDigest, channel, owner, 0, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("Lease acquisition result is missing.");

    public Task<HubChannelLease?> TryRenewAsync(byte[] tokenDigest, HubChannel channel, Guid owner, long generation,
        CancellationToken cancellationToken = default) => ExecuteAsync(2, tokenDigest, channel, owner, generation, cancellationToken);

    public async Task<bool> TryReleaseAsync(byte[] tokenDigest, HubChannel channel, Guid owner, long generation,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(3, tokenDigest, channel, owner, generation, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<bool> IsCurrentAsync(byte[] tokenDigest, HubChannel channel, Guid owner, long generation,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(4, tokenDigest, channel, owner, generation, cancellationToken).ConfigureAwait(false) is not null;

    private async Task<HubChannelLease?> ExecuteAsync(short operation, byte[] tokenDigest, HubChannel channel,
        Guid owner, long generation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenDigest);
        if (tokenDigest.Length != 32 || channel is not (HubChannel.Listen or HubChannel.Proactive) ||
            owner == Guid.Empty || (operation == 1 ? generation != 0 : generation < 1))
            throw new ArgumentException("Lease request is invalid.");
        // Snapshot caller-owned bytes before asynchronous connection acquisition.
        var digest = tokenDigest.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT LeaseGeneration,LeaseExpiresUtc FROM public.ManageHubChannelLease(@operation,@digest,@channel,@owner,@generation)", connection);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Smallint, operation);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Bytea, digest);
        command.Parameters.AddWithValue("channel", NpgsqlDbType.Smallint, (short)channel);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Uuid, owner);
        command.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new HubChannelLease(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    public void Dispose() => _source.Dispose();
    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
