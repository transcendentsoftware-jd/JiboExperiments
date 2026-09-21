using System.Security.Cryptography;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Persistence;
using Npgsql;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class PostgreSqlAwsSigV4ReplayObservationStoreTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ObserveAsync_AtomicallyClassifiesConcurrentReplicasAndResetsExpiredDigest()
    {
        await using var database = await ReplayTestDatabase.CreateAsync();
        await using var firstSource = new PostgreSqlAwsSigV4ReplayDataSource(database.ConnectionString);
        await using var secondSource = new PostgreSqlAwsSigV4ReplayDataSource(database.ConnectionString);
        var firstStore = new PostgreSqlAwsSigV4ReplayObservationStore(firstSource);
        var secondStore = new PostgreSqlAwsSigV4ReplayObservationStore(secondSource);
        var digest = database.Digest;

        var observations = await Task.WhenAll(Enumerable.Range(0, 32).Select(index =>
            (index & 1) == 0
                ? firstStore.ObserveAsync(digest, 1, "Account.CreateHubToken")
                : secondStore.ObserveAsync(digest, 1, "Account.CreateHubToken")));

        Assert.Single(observations, observation =>
            observation.Status == AwsSigV4ReplayObservationStatus.FirstSeen);
        Assert.Equal(31, observations.Count(observation =>
            observation.Status == AwsSigV4ReplayObservationStatus.Repeat));
        Assert.Equal(32, await database.ExecuteScalarAsync<long>(
            "SELECT ObservationCount FROM public.AwsSigV4ReplayObservations WHERE ReplayDigest = @digest"));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM public.AwsSigV4ReplayObservations WHERE ReplayDigest = @digest"));

        var preExpiryRotation = await firstStore.ObserveAsync(
            digest, 2, "Notification.NewRobotToken");
        Assert.Equal(AwsSigV4ReplayObservationStatus.Repeat, preExpiryRotation.Status);
        Assert.Equal(33, preExpiryRotation.ObservationCount);
        Assert.Equal(1, await database.ExecuteScalarAsync<short>(
            "SELECT KeyVersion FROM public.AwsSigV4ReplayObservations WHERE ReplayDigest = @digest"));
        Assert.Equal("Account.CreateHubToken", await database.ExecuteScalarAsync<string>(
            "SELECT Operation FROM public.AwsSigV4ReplayObservations WHERE ReplayDigest = @digest"));

        await database.ExecuteAsync(
            """
            UPDATE public.AwsSigV4ReplayObservations
            SET FirstSeenUtc = clock_timestamp() - INTERVAL '3 seconds',
                LastSeenUtc = clock_timestamp() - INTERVAL '2 seconds',
                ExpiresUtc = clock_timestamp() - INTERVAL '1 second'
            WHERE ReplayDigest = @digest
            """, includeDigest: true);
        var reset = await firstStore.ObserveAsync(digest, 2, "Notification.NewRobotToken");

        Assert.Equal(AwsSigV4ReplayObservationStatus.FirstSeen, reset.Status);
        Assert.Equal(1, reset.ObservationCount);
        Assert.Equal(2, await database.ExecuteScalarAsync<short>(
            "SELECT KeyVersion FROM public.AwsSigV4ReplayObservations WHERE ReplayDigest = @digest"));
        Assert.Equal("Notification.NewRobotToken", await database.ExecuteScalarAsync<string>(
            "SELECT Operation FROM public.AwsSigV4ReplayObservations WHERE ReplayDigest = @digest"));

    }

    [PostgreSqlPrivilegedIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ProvisionedObserver_CanOnlyExecuteReplayObservationFunction()
    {
        await using var database = await ReplayTestDatabase.CreateAsync(
            "OPENJIBO_TEST_POSTGRES_PRIVILEGED_CONNECTION_STRING");
        var observerConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = PostgreSqlAwsSigV4ReplayRoleProvisioner.LoginRole,
            Password = $"test-{Guid.NewGuid():N}",
            MaxPoolSize = 1
        }.ConnectionString;
        await PostgreSqlAwsSigV4ReplayRoleProvisioner.ProvisionAsync(
            database.ConnectionString, observerConnection);
        await PostgreSqlAwsSigV4ReplayRoleProvisioner.ProvisionAsync(
            database.ConnectionString, observerConnection);

        await using var observerSource = new PostgreSqlAwsSigV4ReplayDataSource(observerConnection);
        var observerStore = new PostgreSqlAwsSigV4ReplayObservationStore(observerSource);
        var observerResult = await observerStore.ObserveAsync(
            database.Digest, 1, "Account.CreateHubToken");
        Assert.Equal(AwsSigV4ReplayObservationStatus.FirstSeen, observerResult.Status);

        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsObserverAsync(
            observerConnection, "SELECT COUNT(*) FROM public.AwsSigV4ReplayObservations"));
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsObserverAsync(
            observerConnection, "CREATE TABLE public.SigV4ReplayPrivilegeEscape(Id INTEGER)"));
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsObserverAsync(
            observerConnection, $"SET ROLE {PostgreSqlAwsSigV4ReplayRoleProvisioner.OwnerRole}"));
        Assert.False(await database.ExecuteScalarAsync<bool>(
            $"SELECT has_schema_privilege('{PostgreSqlAwsSigV4ReplayRoleProvisioner.OwnerRole}', 'public', 'CREATE')"));
    }

    [Fact]
    public async Task ObserveAsync_RejectsInvalidMaterialBeforeOpeningDatabase()
    {
        await using var source = new PostgreSqlAwsSigV4ReplayDataSource(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var store = new PostgreSqlAwsSigV4ReplayObservationStore(source);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ObserveAsync(new byte[31], 1, "Account.CreateHubToken"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ObserveAsync(new byte[32], 0, "Account.CreateHubToken"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ObserveAsync(new byte[32], 1, " "));
    }

    private sealed class ReplayTestDatabase : IAsyncDisposable
    {
        private const string DefaultConnectionVariable = "OPENJIBO_TEST_POSTGRES_CONNECTION_STRING";
        private readonly byte[] _digest = RandomNumberGenerator.GetBytes(32);

        private ReplayTestDatabase(string adminConnectionString)
        {
            ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                ApplicationName = "OpenJibo.SigV4Replay.IntegrationTests",
                MaxPoolSize = 4
            }.ConnectionString;
        }

        internal string ConnectionString { get; }
        internal byte[] Digest => _digest;

        internal static async Task<ReplayTestDatabase> CreateAsync(
            string connectionVariable = DefaultConnectionVariable)
        {
            var admin = Environment.GetEnvironmentVariable(connectionVariable)
                        ?? throw new InvalidOperationException($"Set {connectionVariable}.");
            var database = new ReplayTestDatabase(admin);

            try
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql");
                var path = Path.Combine(directory,
                    "013_create_sigv4_replay_observations.state.sql");
                await database.ExecuteAsync(await File.ReadAllTextAsync(path));
                path = Path.Combine(directory, "014_harden_sigv4_replay_observer.state.sql");
                await database.ExecuteAsync(await File.ReadAllTextAsync(path));
                await database.DeleteTestDigestAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        internal async Task ExecuteAsync(string sql, bool includeDigest = false)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (includeDigest)
                command.Parameters.AddWithValue("digest", _digest);
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<T> ExecuteScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("digest", _digest);
            var value = await command.ExecuteScalarAsync();
            return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal async Task ExecuteAsObserverAsync(string connectionString, string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DeleteTestDigestAsync();
        }

        private async Task DeleteTestDigestAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM public.AwsSigV4ReplayObservations WHERE ReplayDigest = @digest";
            command.Parameters.AddWithValue("digest", _digest);
            await command.ExecuteNonQueryAsync();
        }
    }
}
