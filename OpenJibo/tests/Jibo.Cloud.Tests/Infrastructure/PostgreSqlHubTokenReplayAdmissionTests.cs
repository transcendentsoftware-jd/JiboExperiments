using System.Security.Cryptography;
using Jibo.Cloud.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Jibo.Cloud.Tests.Infrastructure;

[Collection(PostgreSqlPrivilegedIntegrationCollection.Name)]
public sealed class PostgreSqlHubTokenReplayAdmissionTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ConcurrentReplicasAndOverlappingRotationSlotsHaveExactlyOneWinner()
    {
        await using var database = await Database.CreateAsync();
        var old = database.NewDigest(); var current = database.NewDigest();
        await using var first = new PostgreSqlHubTokenReplayAdmissionStore(database.Connection);
        await using var second = new PostgreSqlHubTokenReplayAdmissionStore(database.Connection);
        var signedAt = DateTimeOffset.UtcNow;
        var decisions = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => (i % 3) switch
        {
            0 => first.TryConsumeAsync([old], [1], signedAt),
            1 => second.TryConsumeAsync([current, old], [2, 1], signedAt),
            _ => first.TryConsumeAsync([old, current], [1, 2], signedAt)
        }));
        Assert.Single(decisions, accepted => accepted);
        Assert.False(await second.TryConsumeAsync([current, old], [2,1], signedAt));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task RejectedBatchDoesNotPartiallyConsumeNewSlot_AndVersionChangesDoNotReplayExistingDigest()
    {
        await using var database = await Database.CreateAsync();
        var old = database.NewDigest(); var next = database.NewDigest();
        await using var store = new PostgreSqlHubTokenReplayAdmissionStore(database.Connection);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.TryConsumeAsync([old],[1],now));
        Assert.False(await store.TryConsumeAsync([next,old],[2,1],now));
        Assert.True(await store.TryConsumeAsync([next],[2],now)); // no partial reservation from rejected batch
        Assert.False(await store.TryConsumeAsync([old],[9],now)); // version label cannot change digest identity
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task DatabaseRejectsStaleFutureAndQueuedPastDeadlineProofs()
    {
        await using var database = await Database.CreateAsync();
        await using var store = new PostgreSqlHubTokenReplayAdmissionStore(database.Connection);
        Assert.False(await store.TryConsumeAsync([database.NewDigest()],[1],DateTimeOffset.UtcNow.AddMinutes(-6)));
        Assert.False(await store.TryConsumeAsync([database.NewDigest()],[1],DateTimeOffset.UtcNow.AddMinutes(6)));
        var digest = database.NewDigest();
        await using var blocker = new NpgsqlConnection(database.Connection);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(pg_catalog.encode(@digest,'hex'),682351714))", blocker, transaction))
        {
            command.Parameters.AddWithValue("digest",digest); await command.ExecuteNonQueryAsync();
        }
        var signedAt=DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(1);
        var pending = store.TryConsumeAsync([digest],[1],signedAt);
        var waiting=false;
        for(var attempt=0;attempt<30 && !waiting;attempt++)
        {
            waiting=await database.IsAdmissionWaitingAsync();
            if(!waiting)await Task.Delay(10);
        }
        Assert.True(waiting,"The request must reach the advisory-lock wait before testing its deadline.");
        var delay=signedAt.AddMinutes(5)-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(50);
        if(delay>TimeSpan.Zero)await Task.Delay(delay);
        await transaction.CommitAsync();
        Assert.False(await pending); // server must use time AFTER the lock wait
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SqlRejectsMalformedBatchesAndRetainsOnlyOpaqueMaterial()
    {
        await using var database = await Database.CreateAsync();
        foreach (var values in new[] { "NULL,NULL", "ARRAY[]::bytea[],ARRAY[]::smallint[]",
            "ARRAY[decode('01','hex')],ARRAY[1]::smallint[]",
            "ARRAY[decode(repeat('01',32),'hex')],ARRAY[0]::smallint[]",
            "ARRAY[decode(repeat('01',32),'hex'),decode(repeat('01',32),'hex')],ARRAY[1,2]::smallint[]" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => database.Execute(
                "SELECT public.ConsumeHubTokenReplay("+values+",clock_timestamp())"));
            Assert.Equal("22023",error.SqlState);
        }
        await using var store = new PostgreSqlHubTokenReplayAdmissionStore(database.Connection);
        var digest = database.NewDigest();
        Assert.True(await store.TryConsumeAsync([digest],[1],DateTimeOffset.UtcNow));
        await using var connection = new NpgsqlConnection(database.Connection); await connection.OpenAsync();
        await using var q = new NpgsqlCommand("SELECT octet_length(ReplayDigest), KeyVersion, ExpiresUtc-SignedAtUtc FROM public.HubTokenReplayAdmissions WHERE ReplayDigest=@digest",connection);
        q.Parameters.AddWithValue("digest",digest);
        await using var reader=await q.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
        Assert.Equal(32,reader.GetInt32(0));Assert.Equal(1,reader.GetInt16(1));Assert.Equal(TimeSpan.FromMinutes(5),reader.GetTimeSpan(2));
    }

    [PostgreSqlPrivilegedIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task FunctionOnlyCallerCannotReadOrEraseConsumption_AndTempObjectsCannotShadowTheFunction()
    {
        await using var database = await Database.CreateAsync("OPENJIBO_TEST_POSTGRES_PRIVILEGED_CONNECTION_STRING");
        var role="hub_admission_test_"+Guid.NewGuid().ToString("N");
        await database.Execute($"CREATE ROLE {role} NOLOGIN; GRANT USAGE ON SCHEMA public TO {role}; GRANT EXECUTE ON FUNCTION public.ConsumeHubTokenReplay(BYTEA[],SMALLINT[],TIMESTAMPTZ) TO {role}");
        try
        {
            await using var connection=new NpgsqlConnection(new NpgsqlConnectionStringBuilder(database.Connection){Pooling=false}.ConnectionString);
            await connection.OpenAsync();
            // Keep the shadow object owned by the disposable administrator. The
            // function-only role must own no objects when its grants are removed;
            // backend temp-table cleanup can lag client connection disposal.
            await using (var set=new NpgsqlCommand($"CREATE TEMP TABLE HubTokenReplayAdmissions (WrongColumn INTEGER); SET ROLE {role}",connection))await set.ExecuteNonQueryAsync();
            var digest=database.NewDigest();
            await using(var consume=new NpgsqlCommand("SELECT public.ConsumeHubTokenReplay(ARRAY[@digest],ARRAY[1]::smallint[],clock_timestamp())",connection))
            {consume.Parameters.AddWithValue("digest",digest);Assert.True((bool)(await consume.ExecuteScalarAsync())!);}
            foreach(var sql in new[]{"SELECT COUNT(*) FROM public.HubTokenReplayAdmissions","DELETE FROM public.HubTokenReplayAdmissions","CREATE TABLE public.HubAdmissionEscape(Id INTEGER)"})
            {
                await using var denied=new NpgsqlCommand(sql,connection);
                Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>denied.ExecuteNonQueryAsync())).SqlState);
            }
        }
        finally
        {
            await database.Execute($"REVOKE EXECUTE ON FUNCTION public.ConsumeHubTokenReplay(BYTEA[],SMALLINT[],TIMESTAMPTZ) FROM {role}; REVOKE USAGE ON SCHEMA public FROM {role}; DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task AdapterRejectsInvalidInputsWithoutOpeningDatabase()
    {
        await using var store=new PostgreSqlHubTokenReplayAdmissionStore("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        await Assert.ThrowsAsync<ArgumentException>(()=>store.TryConsumeAsync([],[],DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.TryConsumeAsync([new byte[31]],[1],DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.TryConsumeAsync([new byte[32]],[0],DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.TryConsumeAsync([new byte[32],new byte[32]],[1,2],DateTimeOffset.UtcNow));
        foreach(var mode in new[]{"Disable","Prefer","Require","VerifyCA"})
            Assert.Throws<ArgumentException>(()=>new PostgreSqlHubTokenReplayAdmissionStore($"Host=remote.example;SslMode={mode};Database=unused;Username=unused"));
        await using var verified=new PostgreSqlHubTokenReplayAdmissionStore("Host=remote.example;SslMode=VerifyFull;Database=unused;Username=unused");
    }

    private sealed class Database(string connection) : IAsyncDisposable
    {
        public string Connection { get; }=connection;
        private readonly List<byte[]> _owned=[];
        public byte[] NewDigest(){var value=RandomNumberGenerator.GetBytes(32);_owned.Add(value);return value;}
        public static async Task<Database> CreateAsync(string variable="OPENJIBO_TEST_POSTGRES_CONNECTION_STRING")
        {
            var database=new Database(Environment.GetEnvironmentVariable(variable)??throw new InvalidOperationException("Disposable PostgreSQL test connection required."));
            await database.Execute(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Migrations/PostgreSql/017_hub_token_replay_admission.state.sql")));
            return database;
        }
        public async Task Execute(string sql)
        {
            await using var c=new NpgsqlConnection(Connection);await c.OpenAsync();
            await using var q=new NpgsqlCommand(sql,c);await q.ExecuteNonQueryAsync();
        }
        public async Task<bool> IsAdmissionWaitingAsync()
        {
            await using var c=new NpgsqlConnection(Connection);await c.OpenAsync();
            await using var q=new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name='OpenJibo.HubTokenReplayAdmission' AND wait_event_type='Lock' AND wait_event='advisory')",c);
            return (bool)(await q.ExecuteScalarAsync())!;
        }
        public async ValueTask DisposeAsync()
        {
            if(_owned.Count==0)return;
            await using var c=new NpgsqlConnection(Connection);await c.OpenAsync();
            await using var q=new NpgsqlCommand("DELETE FROM public.HubTokenReplayAdmissions WHERE ReplayDigest=ANY(@digests)",c);
            q.Parameters.AddWithValue("digests",NpgsqlDbType.Array|NpgsqlDbType.Bytea,_owned.ToArray());await q.ExecuteNonQueryAsync();
        }
    }
}
