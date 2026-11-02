using System.Security.Cryptography;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Jibo.Cloud.Tests.Infrastructure;

[Collection(PostgreSqlPrivilegedIntegrationCollection.Name)]
public sealed class PostgreSqlHubChannelLeaseTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task IndependentReplicasSerializeConcurrentReplacements_AndOnlyNewestGenerationIsCurrent()
    {
        await using var database = await Database.CreateAsync();
        await using var first = new PostgreSqlHubChannelLeaseStore(database.Connection);
        await using var second = new PostgreSqlHubChannelLeaseStore(database.Connection);
        var digest = database.NewDigest();
        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
        {
            var owner = Guid.NewGuid();
            var lease = await (i % 2 == 0 ? first : second).AcquireAsync(digest, HubChannel.Listen, owner);
            return (Owner: owner, Lease: lease);
        }));
        Assert.Equal(Enumerable.Range(1, 24).Select(i => (long)i), results.Select(r => r.Lease.Generation).Order());
        var checks = await Task.WhenAll(results.Select(r => second.IsCurrentAsync(digest, HubChannel.Listen, r.Owner, r.Lease.Generation)));
        Assert.Single(checks, current => current);
        var winner = results.Single(r => r.Lease.Generation == 24);
        Assert.True(await first.IsCurrentAsync(digest, HubChannel.Listen, winner.Owner, winner.Lease.Generation));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ListenAndProactiveCoexist_AndStaleOwnersCannotRenewOrReleaseSuccessors()
    {
        await using var database = await Database.CreateAsync();
        await using var store = new PostgreSqlHubChannelLeaseStore(database.Connection);
        var digest = database.NewDigest();
        var oldOwner = Guid.NewGuid(); var nextOwner = Guid.NewGuid();
        var listen = await store.AcquireAsync(digest, HubChannel.Listen, oldOwner);
        var proactive = await store.AcquireAsync(digest, HubChannel.Proactive, oldOwner);
        var replacement = await store.AcquireAsync(digest, HubChannel.Listen, nextOwner);
        Assert.True(await store.IsCurrentAsync(digest, HubChannel.Proactive, oldOwner, proactive.Generation));
        Assert.False(await store.IsCurrentAsync(digest, HubChannel.Listen, oldOwner, listen.Generation));
        Assert.Null(await store.TryRenewAsync(digest, HubChannel.Listen, oldOwner, listen.Generation));
        Assert.False(await store.TryReleaseAsync(digest, HubChannel.Listen, oldOwner, listen.Generation));
        Assert.False(await store.TryReleaseAsync(digest, HubChannel.Listen, oldOwner, replacement.Generation));
        var renewed = await store.TryRenewAsync(digest, HubChannel.Listen, nextOwner, replacement.Generation);
        Assert.NotNull(renewed);
        Assert.Equal(replacement.Generation, renewed.Generation);
        Assert.True(renewed.ExpiresUtc >= replacement.ExpiresUtc);
        Assert.True(await store.TryReleaseAsync(digest, HubChannel.Listen, nextOwner, replacement.Generation));
        Assert.False(await store.IsCurrentAsync(digest, HubChannel.Listen, nextOwner, replacement.Generation));
        Assert.Null(await store.TryRenewAsync(digest, HubChannel.Listen, nextOwner, replacement.Generation));
        Assert.False(await store.TryReleaseAsync(digest, HubChannel.Listen, nextOwner, replacement.Generation));
        // Even reusing an owner ID cannot revive a generation after release (ABA).
        var reacquired = await store.AcquireAsync(digest, HubChannel.Listen, nextOwner);
        Assert.True(reacquired.Generation > replacement.Generation);
        Assert.False(await store.TryReleaseAsync(digest, HubChannel.Listen, nextOwner, replacement.Generation));
        Assert.True(await store.IsCurrentAsync(digest, HubChannel.Listen, nextOwner, reacquired.Generation));
        Assert.True(await store.IsCurrentAsync(digest, HubChannel.Proactive, oldOwner, proactive.Generation));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ExpiredLeaseCannotBeResurrected_AndReplacementRetainsGenerationHistory()
    {
        await using var database = await Database.CreateAsync();
        await using var store = new PostgreSqlHubChannelLeaseStore(database.Connection);
        var digest = database.NewDigest(); var owner = Guid.NewGuid();
        var lease = await store.AcquireAsync(digest, HubChannel.Listen, owner);
        await database.ExecuteForDigest("UPDATE public.HubChannelLeases SET ExpiresUtc=clock_timestamp()-interval '1 second' WHERE TokenDigest=@digest", digest);
        Assert.False(await store.IsCurrentAsync(digest, HubChannel.Listen, owner, lease.Generation));
        Assert.Null(await store.TryRenewAsync(digest, HubChannel.Listen, owner, lease.Generation));
        Assert.False(await store.TryReleaseAsync(digest, HubChannel.Listen, owner, lease.Generation));
        var next = await store.AcquireAsync(digest, HubChannel.Listen, owner);
        Assert.Equal(lease.Generation + 1, next.Generation);
        Assert.False(await store.IsCurrentAsync(digest, HubChannel.Listen, owner, lease.Generation));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task RenewalChecksDatabaseTimeAfterWaitingForTheSlotLock()
    {
        await using var database = await Database.CreateAsync();
        await using var store = new PostgreSqlHubChannelLeaseStore(database.Connection);
        var digest = database.NewDigest(); var owner = Guid.NewGuid();
        var lease = await store.AcquireAsync(digest, HubChannel.Listen, owner);
        await using var blocker = new NpgsqlConnection(database.Connection);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(pg_catalog.encode(@digest,'hex') || ':1',682351718))", blocker, transaction))
        {
            command.Parameters.AddWithValue("digest", digest);
            await command.ExecuteNonQueryAsync();
        }
        var pending = store.TryRenewAsync(digest, HubChannel.Listen, owner, lease.Generation);
        var waiting = false;
        for (var attempt = 0; attempt < 100 && !waiting; attempt++)
        {
            waiting = await database.IsLeaseWaitingAsync();
            if (!waiting) await Task.Delay(10);
        }
        Assert.True(waiting, "Renewal must reach the slot lock before expiry is changed.");
        // Expiry is after any pre-lock timestamp and before the lock is released.
        await using (var expire = new NpgsqlCommand(
            "UPDATE public.HubChannelLeases SET ExpiresUtc=clock_timestamp() WHERE TokenDigest=@digest", blocker, transaction))
        {
            expire.Parameters.AddWithValue("digest", digest);
            await expire.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        Assert.Null(await pending);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task InvalidSqlRequestsAreRejected_AndGenerationOverflowFailsWithoutReplacingOwner()
    {
        await using var database = await Database.CreateAsync();
        var validDigest = "decode(repeat('01',32),'hex')";
        var owner = "'11111111-1111-1111-1111-111111111111'::uuid";
        foreach (var arguments in new[]
        {
            $"0::smallint,{validDigest},1::smallint,{owner},0::bigint",
            $"1::smallint,NULL,1::smallint,{owner},0::bigint",
            $"1::smallint,decode('01','hex'),1::smallint,{owner},0::bigint",
            $"1::smallint,{validDigest},3::smallint,{owner},0::bigint",
            $"1::smallint,{validDigest},1::smallint,'00000000-0000-0000-0000-000000000000'::uuid,0::bigint",
            $"1::smallint,{validDigest},1::smallint,{owner},1::bigint",
            $"2::smallint,{validDigest},1::smallint,{owner},0::bigint",
            $"4::smallint,{validDigest},1::smallint,{owner},NULL"
        })
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
                database.Execute($"SELECT * FROM public.ManageHubChannelLease({arguments})"))).SqlState);
        await using var store = new PostgreSqlHubChannelLeaseStore(database.Connection);
        var digest = database.NewDigest(); var originalOwner = Guid.NewGuid();
        await store.AcquireAsync(digest, HubChannel.Listen, originalOwner);
        await database.ExecuteForDigest("UPDATE public.HubChannelLeases SET Generation=9223372036854775807 WHERE TokenDigest=@digest", digest);
        Assert.Equal("22003", (await Assert.ThrowsAsync<PostgresException>(() =>
            store.AcquireAsync(digest, HubChannel.Listen, Guid.NewGuid()))).SqlState);
        Assert.True(await store.IsCurrentAsync(digest, HubChannel.Listen, originalOwner, long.MaxValue));
    }

    [PostgreSqlPrivilegedIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task FunctionOnlyRoleCannotReadDeleteOrResetGeneration_AndTempTableCannotShadowStorage()
    {
        await using var database = await Database.CreateAsync("OPENJIBO_TEST_POSTGRES_PRIVILEGED_CONNECTION_STRING");
        var role = "hub_lease_test_" + Guid.NewGuid().ToString("N");
        const string signature = "public.ManageHubChannelLease(SMALLINT,BYTEA,SMALLINT,UUID,BIGINT)";
        await database.Execute($"CREATE ROLE {role} NOLOGIN; GRANT USAGE ON SCHEMA public TO {role}; GRANT EXECUTE ON FUNCTION {signature} TO {role}");
        try
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.Connection) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            // The administrator owns the shadow object, so delayed backend cleanup cannot block DROP ROLE.
            await using (var set = new NpgsqlCommand($"CREATE TEMP TABLE HubChannelLeases(WrongColumn INTEGER); SET ROLE {role}", connection))
                await set.ExecuteNonQueryAsync();
            var digest = database.NewDigest();
            await using (var acquire = new NpgsqlCommand(
                "SELECT LeaseGeneration FROM public.ManageHubChannelLease(1::smallint,@digest,1::smallint,@owner,0::bigint)", connection))
            {
                acquire.Parameters.AddWithValue("digest", digest);
                acquire.Parameters.AddWithValue("owner", Guid.NewGuid());
                Assert.Equal(1L, await acquire.ExecuteScalarAsync());
            }
            foreach (var sql in new[] { "SELECT * FROM public.HubChannelLeases", "DELETE FROM public.HubChannelLeases",
                         "UPDATE public.HubChannelLeases SET Generation=1", "CREATE TABLE public.HubLeaseEscape(Id INTEGER)" })
            {
                await using var denied = new NpgsqlCommand(sql, connection);
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync())).SqlState);
            }
        }
        finally
        {
            await database.Execute($"REVOKE EXECUTE ON FUNCTION {signature} FROM {role}; REVOKE USAGE ON SCHEMA public FROM {role}; DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task AdapterRejectsInvalidInputsAndCancellationBeforeConnecting()
    {
        await using var store = new PostgreSqlHubChannelLeaseStore("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var digest = new byte[32]; var owner = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireAsync(new byte[31], HubChannel.Listen, owner));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireAsync(digest, (HubChannel)3, owner));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcquireAsync(digest, HubChannel.Listen, Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryRenewAsync(digest, HubChannel.Listen, owner, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => store.IsCurrentAsync(digest, HubChannel.Listen, owner, -1));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReleaseAsync(digest, HubChannel.Listen, owner, 0));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AcquireAsync(digest, HubChannel.Listen, owner, cancellation.Token));
    }

    [Fact]
    public async Task RemoteConnectionRequiresCertificateAndHostnameVerification()
    {
        foreach (var mode in new[] { "Disable", "Prefer", "Require", "VerifyCA" })
            Assert.Throws<ArgumentException>(() => new PostgreSqlHubChannelLeaseStore($"Host=remote.example;SslMode={mode};Database=unused;Username=unused"));
        await using var verified = new PostgreSqlHubChannelLeaseStore("Host=remote.example;SslMode=VerifyFull;Database=unused;Username=unused");
    }

    private sealed class Database(string connection) : IAsyncDisposable
    {
        public string Connection { get; } = connection;
        private readonly List<byte[]> _owned = [];
        public byte[] NewDigest() { var digest = RandomNumberGenerator.GetBytes(32); _owned.Add(digest); return digest; }
        public static async Task<Database> CreateAsync(string variable = "OPENJIBO_TEST_POSTGRES_CONNECTION_STRING")
        {
            var database = new Database(Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException("Disposable PostgreSQL test connection required."));
            await database.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations/PostgreSql/018_hub_channel_leases.state.sql")));
            return database;
        }
        public async Task Execute(string sql)
        {
            await using var connection = new NpgsqlConnection(Connection); await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
        }
        public async Task ExecuteForDigest(string sql, byte[] digest)
        {
            await using var connection = new NpgsqlConnection(Connection); await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("digest", digest); await command.ExecuteNonQueryAsync();
        }
        public async Task<bool> IsLeaseWaitingAsync()
        {
            await using var connection = new NpgsqlConnection(Connection); await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name='OpenJibo.HubChannelLease' AND wait_event_type='Lock' AND wait_event='advisory')", connection);
            return (bool)(await command.ExecuteScalarAsync())!;
        }
        public async ValueTask DisposeAsync()
        {
            if (_owned.Count == 0) return;
            await using var connection = new NpgsqlConnection(Connection); await connection.OpenAsync();
            // Only disposable test digests; production must retain generation tombstones.
            await using var command = new NpgsqlCommand("DELETE FROM public.HubChannelLeases WHERE TokenDigest=ANY(@digests)", connection);
            command.Parameters.AddWithValue("digests", NpgsqlDbType.Array | NpgsqlDbType.Bytea, _owned.ToArray());
            await command.ExecuteNonQueryAsync();
        }
    }
}
