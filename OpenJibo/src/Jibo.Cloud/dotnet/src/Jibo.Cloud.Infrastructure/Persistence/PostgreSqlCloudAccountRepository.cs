using Jibo.Cloud.Domain.Models;
using Npgsql;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class PostgreSqlCloudAccountRepository(
    PostgreSqlCloudStateDataSource dataSource,
    ICloudStateSecretProtector secretProtector) : ICloudAccountRepository
{
    public Task<AccountProfile?> GetByIdAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return Task.FromResult<AccountProfile?>(null);
        return ReadOneAsync("AccountId = @value", accountId.Trim(), cancellationToken);
    }

    public Task<AccountProfile?> GetDefaultAsync(CancellationToken cancellationToken = default) =>
        ReadOneAsync("IsDefault", null, cancellationToken);

    public async Task<AccountProfile> UpsertAsync(AccountProfile account, bool? isDefault = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.AccountId))
            throw new ArgumentException("AccountId is required.", nameof(account));
        if (string.IsNullOrWhiteSpace(account.Email))
            throw new ArgumentException("Email is required.", nameof(account));
        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (isDefault == true)
        {
            await using var clearDefault = new NpgsqlCommand(
                "UPDATE Accounts SET IsDefault = FALSE, UpdatedUtc = NOW() WHERE IsDefault AND AccountId <> @id",
                connection, transaction);
            clearDefault.Parameters.AddWithValue("id", account.AccountId.Trim());
            await clearDefault.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand("""
                                                     INSERT INTO Accounts
                                                         (AccountId, Email, FirstName, LastName, AccessKeyId,
                                                          SecretAccessKeyCiphertext, SecretWrappingKeyId, IsDefault)
                                                     VALUES
                                                         (@id, @email, @firstName, @lastName, @accessKeyId,
                                                          @secret, @keyId, @insertIsDefault)
                                                     ON CONFLICT (AccountId) DO UPDATE SET
                                                         Email = EXCLUDED.Email,
                                                         FirstName = EXCLUDED.FirstName,
                                                         LastName = EXCLUDED.LastName,
                                                         AccessKeyId = EXCLUDED.AccessKeyId,
                                                         SecretAccessKeyCiphertext = EXCLUDED.SecretAccessKeyCiphertext,
                                                         SecretWrappingKeyId = EXCLUDED.SecretWrappingKeyId,
                                                         IsDefault = COALESCE(@isDefault, Accounts.IsDefault),
                                                         UpdatedUtc = NOW()
                                                     """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", account.AccountId.Trim());
            command.Parameters.AddWithValue("email", account.Email.Trim());
            command.Parameters.AddWithValue("firstName", account.FirstName.Trim());
            command.Parameters.AddWithValue("lastName", account.LastName.Trim());
            command.Parameters.AddWithValue("accessKeyId", account.AccessKeyId.Trim());
            command.Parameters.AddWithValue("secret", secretProtector.Protect(account.SecretAccessKey));
            command.Parameters.AddWithValue("keyId", secretProtector.KeyId);
            command.Parameters.AddWithValue("insertIsDefault", isDefault ?? false);
            command.Parameters.Add("isDefault", NpgsqlTypes.NpgsqlDbType.Boolean).Value =
                (object?)isDefault ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await CloudStateRevision.BumpAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return account;
    }

    private async Task<AccountProfile?> ReadOneAsync(string predicate, string? value,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.Value.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT AccountId, Email, FirstName, LastName, AccessKeyId,
                                      SecretAccessKeyCiphertext, SecretWrappingKeyId
                               FROM Accounts
                               WHERE {predicate}
                               ORDER BY CreatedUtc
                               LIMIT 1
                               """;
        if (value is not null) command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var ciphertext = reader.IsDBNull(5) ? null : reader.GetFieldValue<byte[]>(5);
        if (ciphertext is not null && !reader.IsDBNull(6) &&
            !string.Equals(reader.GetString(6), secretProtector.KeyId, StringComparison.Ordinal))
            throw new InvalidOperationException("The account secret uses an unavailable wrapping key.");

        return new AccountProfile
        {
            AccountId = reader.GetString(0),
            Email = reader.GetString(1),
            FirstName = reader.GetString(2),
            LastName = reader.GetString(3),
            AccessKeyId = reader.GetString(4),
            SecretAccessKey = ciphertext is null ? string.Empty : secretProtector.Unprotect(ciphertext)
        };
    }
}
