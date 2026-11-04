using Jibo.Cloud.Domain.Models;
using Npgsql;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class PostgreSqlUserDeviceLinkRepository(PostgreSqlCloudStateDataSource dataSource)
    : IUserDeviceLinkRepository
{
    public async Task<UserDeviceLink> LinkAsync(string userId, string deviceId, string claimSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var normalizedUserId = userId.Trim();
        var normalizedDeviceId = deviceId.Trim();
        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Lock the durable device row, including its first claim, across replicas.
        await using (var guard = new NpgsqlCommand(
                         "SELECT DeviceId FROM Devices WHERE LOWER(DeviceId) = LOWER(@deviceId) FOR UPDATE", connection, transaction))
        {
            guard.Parameters.AddWithValue("deviceId", normalizedDeviceId);
            var canonical = (string?)await guard.ExecuteScalarAsync(cancellationToken);
            if (canonical is null) throw new KeyNotFoundException("Robot record was not found.");
            normalizedDeviceId = canonical;
        }
        UserDeviceLink? previous;
        await using (var read = new NpgsqlCommand(
                         "SELECT UserId, DeviceId, ClaimSource, LinkedUtc, OwnershipEpoch, LegacyIntegrationEpoch FROM UserDevices WHERE DeviceId = @deviceId", connection, transaction))
        {
            read.Parameters.AddWithValue("deviceId", normalizedDeviceId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            previous = await reader.ReadAsync(cancellationToken) ? ReadLink(reader) : null;
        }
        if (previous is not null && previous.UserId.Equals(normalizedUserId, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.CommitAsync(cancellationToken);
            return previous;
        }
        var linkedUtc = DateTimeOffset.UtcNow;
        var epoch = previous is null ? "unowned" : Guid.NewGuid().ToString("N");
        var legacyEpoch = previous?.EffectiveLegacyIntegrationEpoch ?? "unowned";

        await using (var delete = new NpgsqlCommand(
                         "DELETE FROM UserDevices WHERE LOWER(DeviceId) = LOWER(@deviceId)", connection, transaction))
        {
            delete.Parameters.AddWithValue("deviceId", normalizedDeviceId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand("""
                                                    INSERT INTO UserDevices
                                                        (UserId, DeviceId, ClaimSource, LinkedUtc, OwnershipEpoch, LegacyIntegrationEpoch)
                                                    VALUES (@userId, @deviceId, @claimSource, @linkedUtc, @epoch, @legacyEpoch)
                                                    """, connection, transaction))
        {
            insert.Parameters.AddWithValue("userId", normalizedUserId);
            insert.Parameters.AddWithValue("deviceId", normalizedDeviceId);
            insert.Parameters.AddWithValue("claimSource",
                string.IsNullOrWhiteSpace(claimSource) ? "portal-pairing" : claimSource.Trim());
            insert.Parameters.AddWithValue("linkedUtc", linkedUtc);
            insert.Parameters.AddWithValue("epoch", epoch);
            insert.Parameters.AddWithValue("legacyEpoch", legacyEpoch);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await CloudStateRevision.BumpAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserDeviceLink(normalizedUserId, normalizedDeviceId,
            string.IsNullOrWhiteSpace(claimSource) ? "portal-pairing" : claimSource.Trim(), linkedUtc, epoch, legacyEpoch);
    }

    public async Task<IReadOnlyList<string>> ListDeviceIdsForUserAsync(string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return [];

        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT DeviceId
                              FROM UserDevices
                              WHERE UserId = @userId
                              ORDER BY LinkedUtc, DeviceId
                              """;
        command.Parameters.AddWithValue("userId", userId.Trim());
        var deviceIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) deviceIds.Add(reader.GetString(0));
        return deviceIds;
    }

    public async Task<UserDeviceLink?> FindByDeviceAsync(string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT UserId, DeviceId, ClaimSource, LinkedUtc, OwnershipEpoch, LegacyIntegrationEpoch
                              FROM UserDevices
                              WHERE LOWER(DeviceId) = LOWER(@deviceId)
                              LIMIT 1
                              """;
        command.Parameters.AddWithValue("deviceId", deviceId.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLink(reader) : null;
    }
    private static UserDeviceLink ReadLink(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
}
