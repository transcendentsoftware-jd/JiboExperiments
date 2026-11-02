using Jibo.Cloud.Application.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Jibo.Cloud.Infrastructure.Persistence;

/// <summary>Dedicated bounded pool. Configuration must use a function-only identity, not cloud-state credentials.</summary>
public sealed class PostgreSqlHubTokenReplayAdmissionStore : IHubTokenReplayAdmissionStore, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;
    public PostgreSqlHubTokenReplayAdmissionStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Admission connection is required.");
        var options = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = 2, Timeout = 2, CommandTimeout = 2,
            ApplicationName = "OpenJibo.HubTokenReplayAdmission"
        };
        if (options.SslMode < SslMode.Require && options.Host is not ("localhost" or "127.0.0.1" or "::1"))
            throw new ArgumentException("Admission connection requires TLS outside loopback.");
        _source = NpgsqlDataSource.Create(options.ConnectionString);
    }

    public async Task<bool> TryConsumeAsync(IReadOnlyList<byte[]> digests, IReadOnlyList<short> keyVersions,
        DateTimeOffset signedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        ArgumentNullException.ThrowIfNull(keyVersions);
        if (digests.Count is < 1 or > 2 || keyVersions.Count != digests.Count ||
            digests.Any(d => d is null || d.Length != 32) || keyVersions.Any(v => v < 1) ||
            keyVersions.Distinct().Count() != keyVersions.Count ||
            digests.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count() != digests.Count)
            throw new ArgumentException("Admission digest batch is invalid.");
        await using var connection = await _source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT public.ConsumeHubTokenReplay(@digests,@versions,@signedAt)", connection);
        command.Parameters.AddWithValue("digests", NpgsqlDbType.Array | NpgsqlDbType.Bytea, digests.ToArray());
        command.Parameters.AddWithValue("versions", NpgsqlDbType.Array | NpgsqlDbType.Smallint, keyVersions.ToArray());
        command.Parameters.AddWithValue("signedAt", NpgsqlDbType.TimestampTz, signedAt.ToUniversalTime());
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Admission result is missing."));
    }
    public void Dispose() => _source.Dispose();
    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
