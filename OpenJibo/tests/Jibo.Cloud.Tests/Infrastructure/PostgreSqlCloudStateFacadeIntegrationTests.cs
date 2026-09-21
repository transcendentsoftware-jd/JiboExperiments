using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Npgsql;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class PostgreSqlCloudStateFacadeIntegrationTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task CredentialObservedHubToken_RevalidatesAccessKeyRevocationAndCrossReplicaCache()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var sourceA = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        await using var sourceB = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var first = new PostgreSqlCloudStateStore(sourceA, new PlaintextTestProtector());
        var second = new PostgreSqlCloudStateStore(sourceB, new PlaintextTestProtector());
        var account = first.GetAccount();
        var binding = new HubTokenCredentialBinding(
            AwsSigV4RequestVerifier.CreateAccessKeyFingerprint(account.AccessKeyId),
            DateTimeOffset.UtcNow,
            operationAuthenticated: false);

        var token = first.IssueHubToken(
            "credential-observed-device",
            useDefaultRobot: false,
            credentialBinding: binding);
        var rehydrated = Assert.IsType<CloudSession>(second.FindIssuedToken(token));
        Assert.True(HubTokenCredentialBinding.TryRead(rehydrated.Metadata, out var observed));
        Assert.Equal(binding, observed);
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM CloudAuthTokens WHERE TokenHash='{tokenHash}'"));
        Assert.Equal(0, await database.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM CloudAuthTokens WHERE TokenHash='{token}'"));

        var accessKeyBound = first.IssueHubToken(
            "access-key-rotation-device",
            useDefaultRobot: false,
            credentialBinding: new HubTokenCredentialBinding(
                AwsSigV4RequestVerifier.CreateAccessKeyFingerprint(account.AccessKeyId),
                DateTimeOffset.UtcNow,
                operationAuthenticated: false));
        Assert.NotNull(second.FindIssuedToken(accessKeyBound));
        await database.ExecuteAsync(
            "UPDATE Accounts SET AccessKeyId='rotated-access-key' WHERE AccountId='usr_openjibo_owner'");
        Assert.Null(first.FindIssuedToken(accessKeyBound));
        Assert.Null(second.FindIssuedToken(accessKeyBound));

        var malformed = first.IssueHubToken("malformed-binding-device", useDefaultRobot: false);
        var malformedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(malformed)))
            .ToLowerInvariant();
        await database.ExecuteAsync(
            $"UPDATE CloudAuthTokens SET Metadata='{{\"legacyCredentialBindingVersion\":\"1\"}}'::jsonb " +
            $"WHERE TokenHash='{malformedHash}'");
        Assert.Null(first.FindIssuedToken(malformed));

        var unbound = first.IssueHubToken("legacy-unbound-device", useDefaultRobot: false);
        Assert.NotNull(second.FindIssuedToken(unbound));
        var unboundHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unbound))).ToLowerInvariant();
        await database.ExecuteAsync(
            $"UPDATE CloudAuthTokens SET RevokedUtc=NOW() WHERE TokenHash='{unboundHash}'");
        Assert.Null(second.FindIssuedToken(unbound));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task HubToken_PreservesUnlinkedObservedIdentityWithoutCreatingInventory()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var store = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector());

        var token = store.IssueHubToken("unlinked-observed-device");
        var session = store.OpenSession(
            "neo-hub-listen",
            null,
            token,
            "neohub.openjibo.com",
            "/v1/listen");

        Assert.Equal("unlinked-observed-device", session.DeviceId);
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM CloudAuthTokens WHERE DeviceId='unlinked-observed-device'"));
        Assert.Equal(0, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Devices WHERE DeviceId='unlinked-observed-device'"));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task VisibleIdentityCandidates_AreScopedPrioritizedCappedAndHydrateOnlyResults()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var store = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector());

        await database.ExecuteAsync("""
            INSERT INTO Accounts (AccountId,Email,AccessKeyId,IsDefault)
            VALUES ('other-identity-account','other-identity@example.invalid','other-identity-access',FALSE);
            INSERT INTO Devices
                (DeviceId,RobotId,FriendlyName,VerifiedSerialNumber,RegistrationSource,IsHidden,IsDefault,ArchivedUtc)
            VALUES
                ('identity-exact','identity-lower','identity-friendly','identity-serial','physical',FALSE,FALSE,NULL),
                ('identity-lower-match','identity-exact','identity-lower-name','identity-lower-serial','physical',FALSE,FALSE,NULL),
                ('default-isolation','isolation-robot','isolation-key',NULL,'physical',FALSE,FALSE,NULL),
                ('other-isolation','other-isolation-robot','isolation-key',NULL,'physical',FALSE,FALSE,NULL),
                ('bounded-a','bounded-robot-a','bounded-friendly',NULL,'physical',FALSE,FALSE,NULL),
                ('bounded-b','bounded-robot-b','bounded-friendly',NULL,'physical',FALSE,FALSE,NULL),
                ('bounded-c','bounded-robot-c','bounded-friendly',NULL,'physical',FALSE,FALSE,NULL),
                ('bounded-hidden','bounded-robot-hidden','bounded-friendly',NULL,'physical',TRUE,FALSE,NULL),
                ('bounded-archived','bounded-robot-archived','bounded-friendly',NULL,'physical',FALSE,FALSE,NOW());
            INSERT INTO AccountDevices (AccountId,DeviceId)
            VALUES
                ('usr_openjibo_owner','identity-exact'),
                ('usr_openjibo_owner','identity-lower-match'),
                ('usr_openjibo_owner','default-isolation'),
                ('usr_openjibo_owner','bounded-a'),
                ('usr_openjibo_owner','bounded-b'),
                ('usr_openjibo_owner','bounded-c'),
                ('usr_openjibo_owner','bounded-hidden'),
                ('usr_openjibo_owner','bounded-archived'),
                ('other-identity-account','other-isolation');
            INSERT INTO DeviceHostMappings (DeviceId,MappingKey,MappingValue)
            VALUES
                ('bounded-a','selected','yes-a'),
                ('bounded-b','selected','yes-b'),
                ('bounded-c','selected','should-not-be-loaded'),
                ('bounded-hidden','selected','hidden');
            """);

        var exact = store.FindVisibleIdentityCandidates("identity-exact");
        var isolated = store.FindVisibleIdentityCandidates("isolation-key");
        var bounded = store.FindVisibleIdentityCandidates("bounded-friendly");
        var nullSerial = store.FindVisibleIdentityCandidates("missing-serial");

        Assert.Equal("identity-exact", Assert.Single(exact).DeviceId);
        Assert.Equal("default-isolation", Assert.Single(isolated).DeviceId);
        Assert.Equal(["bounded-a", "bounded-b"], bounded.Select(device => device.DeviceId));
        Assert.DoesNotContain(bounded, device => device.DeviceId is "bounded-c" or "bounded-hidden" or "bounded-archived");
        Assert.Equal("yes-a", bounded[0].HostMappings["selected"]);
        Assert.Equal("yes-b", bounded[1].HostMappings["selected"]);
        Assert.Empty(nullSerial);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task IdentitySuggestions_BreakExactTiesByProposedRobotIdRegardlessOfInsertionOrder()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var repository = new PostgreSqlRobotIdentitySuggestionRepository(source);

        await database.ExecuteAsync("""
            INSERT INTO Devices (DeviceId,RobotId,FriendlyName,RegistrationSource,IsDefault)
            VALUES
                ('tie-device-a','tie-robot-a','Tie Robot A','physical',FALSE),
                ('tie-device-b','tie-robot-b','Tie Robot B','physical',FALSE);
            INSERT INTO RobotIdentitySuggestions
                (ObservedDeviceId,ProposedRobotId,ObservationCount,FirstObservedUtc,LastObservedUtc)
            VALUES
                ('tie-device-a','Zulu-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('tie-device-a','Alpha-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('tie-device-b','Alpha-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('tie-device-b','Zulu-Beta-Charlie-Delta',1,NOW(),NOW());
            """);

        Assert.Equal("Alpha-Beta-Charlie-Delta", repository.GetBest("tie-device-a")!.ProposedRobotId);
        Assert.Equal("Alpha-Beta-Charlie-Delta", repository.GetBest("tie-device-b")!.ProposedRobotId);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task IdentitySuggestions_WhenCandidatesTie_PrunesLexicallyLastCandidate()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var repository = new PostgreSqlRobotIdentitySuggestionRepository(source);

        await database.ExecuteAsync("""
            INSERT INTO Devices (DeviceId,RobotId,FriendlyName,RegistrationSource,IsDefault)
            VALUES
                ('prune-device','prune-robot','Prune Robot','physical',FALSE),
                ('dismissed-device','dismissed-robot','Dismissed Robot','physical',FALSE),
                ('expired-device','expired-robot','Expired Robot','physical',FALSE),
                ('trigger-device','trigger-robot','Trigger Robot','physical',FALSE);
            INSERT INTO RobotIdentitySuggestions
                (ObservedDeviceId,ProposedRobotId,ObservationCount,FirstObservedUtc,LastObservedUtc)
            VALUES
                ('prune-device','Zulu-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('prune-device','Yankee-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('prune-device','Xray-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('prune-device','Whiskey-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('prune-device','Alpha-Beta-Charlie-Delta',1,NOW(),NOW()),
                ('dismissed-device','Dismissed-One-Robot-Identity',10,NOW(),NOW()),
                ('dismissed-device','Dismissed-Two-Robot-Identity',10,NOW(),NOW()),
                ('dismissed-device','Dismissed-Three-Robot-Identity',10,NOW(),NOW()),
                ('dismissed-device','Dismissed-Four-Robot-Identity',10,NOW(),NOW()),
                ('dismissed-device','Active-Robot-Identity-Candidate',1,NOW(),NOW()),
                ('expired-device','Expired-One-Robot-Identity',10,NOW() - INTERVAL '31 days',NOW() - INTERVAL '31 days'),
                ('expired-device','Expired-Two-Robot-Identity',10,NOW() - INTERVAL '31 days',NOW() - INTERVAL '31 days'),
                ('expired-device','Expired-Three-Robot-Identity',10,NOW() - INTERVAL '31 days',NOW() - INTERVAL '31 days'),
                ('expired-device','Expired-Four-Robot-Identity',10,NOW() - INTERVAL '31 days',NOW() - INTERVAL '31 days'),
                ('expired-device','Fresh-Robot-Identity-Candidate',1,NOW(),NOW());
            UPDATE RobotIdentitySuggestions
            SET DismissedUtc=NOW()
            WHERE ObservedDeviceId='dismissed-device'
              AND ProposedRobotId LIKE 'Dismissed-%';
            """);

        repository.Observe(
            "trigger-device",
            "Trigger-Beta-Charlie-Delta",
            new RobotIdentitySuggestionEvidence(
                "test", "name", "Trigger-Beta-Charlie-Delta", DateTimeOffset.UtcNow));

        Assert.Equal(
            "Alpha-Beta-Charlie-Delta,Whiskey-Beta-Charlie-Delta,Xray-Beta-Charlie-Delta,Yankee-Beta-Charlie-Delta",
            await database.ExecuteScalarAsync<string>("""
                SELECT string_agg(ProposedRobotId, ',' ORDER BY LOWER(ProposedRobotId))
                FROM RobotIdentitySuggestions
                WHERE ObservedDeviceId='prune-device'
                """));
        Assert.Equal(
            "Active-Robot-Identity-Candidate",
            repository.GetBest("dismissed-device")!.ProposedRobotId);
        Assert.Equal(
            "Fresh-Robot-Identity-Candidate",
            repository.GetBest("expired-device")!.ProposedRobotId);
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task IndependentStores_ObserveCommittedScopedChangesWithoutSnapshotOrSessionWrites()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var sourceA = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        await using var sourceB = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var protector = new PlaintextTestProtector();
        var ttl = TimeSpan.FromMilliseconds(40);
        var first = new PostgreSqlCloudStateStore(sourceA, protector, deviceCacheMaxEntries: 8,
            deviceCacheTtl: ttl, maximumActiveSessions: 4);
        var second = new PostgreSqlCloudStateStore(sourceB, protector, deviceCacheMaxEntries: 8,
            deviceCacheTtl: ttl, maximumActiveSessions: 4);

        first.UpsertDevice(Device("shared-device", "Shared Before"));
        first.UpsertDevice(Device("unrelated-device", "Must Survive"));
        Assert.Equal("Shared Before", second.GetOrCreateDevice("shared-device", null, null).FriendlyName);
        await database.ExecuteAsync("""
            INSERT INTO PersistenceSnapshots (SnapshotName, SnapshotJson)
            VALUES ('cloud-state-integration-marker', '{"marker":"unchanged"}')
            """);
        var snapshotBefore = await database.ReadSnapshotMarkerAsync();

        first.RenameDevice("shared-device", "Shared After");
        await Task.Delay(ttl + TimeSpan.FromMilliseconds(80));

        Assert.Equal("Shared After", second.GetOrCreateDevice("shared-device", null, null).FriendlyName);
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Devices WHERE DeviceId='unrelated-device' AND FriendlyName='Must Survive'"));
        Assert.Equal(snapshotBefore, await database.ReadSnapshotMarkerAsync());

        var revisionBeforeSession = await database.ExecuteScalarAsync<long>(
            "SELECT Revision FROM CloudStateMetadata WHERE StateKey='cloud-state'");
        var tokensBefore = await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CloudAuthTokens");
        var active = second.OpenSession("robot", "shared-device", null, "socket.test", "/v1/listen");
        Assert.False(string.IsNullOrWhiteSpace(active.Token));
        Assert.Same(active, second.FindActiveSessionByToken(active.Token!));
        second.CloseSession(active.SessionId);

        Assert.Equal(revisionBeforeSession, await database.ExecuteScalarAsync<long>(
            "SELECT Revision FROM CloudStateMetadata WHERE StateKey='cloud-state'"));
        Assert.Equal(tokensBefore, await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM CloudAuthTokens"));
        Assert.Equal(snapshotBefore, await database.ReadSnapshotMarkerAsync());
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task ExplicitRobotUpdate_RemovesSupersededProfileForSameDevice()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var store = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector());
        var original = store.GetRobot();

        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = original.DeviceId,
            RobotId = "replacement-robot-id",
            FriendlyName = original.FriendlyName,
            FirmwareVersion = original.FirmwareVersion,
            ApplicationVersion = original.ApplicationVersion,
            IsActive = original.IsActive,
            RegistrationSource = original.RegistrationSource,
            IsHidden = original.IsHidden,
            ArchivedUtc = original.ArchivedUtc,
            HostMappings = original.HostMappings
        });

        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM RobotProfiles WHERE DeviceId='{original.DeviceId}'"));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM RobotProfiles WHERE RobotId='replacement-robot-id'"));
        Assert.Equal(0, await database.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM RobotProfiles WHERE RobotId='{original.RobotId}'"));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task FreshBootstrap_PreservesLargeUnscopedFleetAndAddsOnlyItsScopedDefaults()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        const int fleetSize = 1200;
        await database.PreseedFleetAsync(fleetSize);
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);

        var store = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector(), deviceCacheMaxEntries: 4,
            deviceCacheTtl: TimeSpan.FromMilliseconds(20), maximumActiveSessions: 2);

        Assert.Equal(fleetSize + 1, await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Devices"));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Devices WHERE DeviceId='openjibo-bootstrap-default'"));
        Assert.Single(store.GetDevices());
        Assert.Single(store.GetLoops());
        Assert.Equal(fleetSize, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Devices WHERE DeviceId LIKE 'fleet-device-%'"));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task AdministrationInventory_SeesEveryAccountWithoutBroadeningNormalReads()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var store = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector());
        await database.ExecuteAsync("""
            INSERT INTO Accounts (AccountId,Email,AccessKeyId,IsDefault)
            VALUES ('other-account','other@example.invalid','other-access',FALSE);
            INSERT INTO Devices (DeviceId,RobotId,FriendlyName,RegistrationSource,IsHidden,ArchivedUtc)
            VALUES ('other-visible','duplicate-robot-name','Duplicate Robot','physical',FALSE,NULL),
                   ('other-archived','duplicate-robot-name','Duplicate Robot','physical',TRUE,NOW());
            INSERT INTO AccountDevices (AccountId,DeviceId)
            VALUES ('other-account','other-visible'),('other-account','other-archived');
            """);

        Assert.DoesNotContain(store.GetDevices(), device => device.DeviceId.StartsWith("other-", StringComparison.Ordinal));
        var administration = store.GetDevicesForAdministration();
        Assert.Contains(administration, device => device.DeviceId == "other-visible");
        Assert.Contains(administration, device => device.DeviceId == "other-archived" && device.IsHidden);
        Assert.Equal(2, administration.Count(device => device.FriendlyName == "Duplicate Robot"));

        var visible = administration.Single(device => device.DeviceId == "other-visible");
        store.UpsertDeviceForAdministration(new DeviceRegistration
        {
            DeviceId = visible.DeviceId, RobotId = visible.RobotId, FriendlyName = visible.FriendlyName,
            RegistrationSource = visible.RegistrationSource, IsHidden = true, ArchivedUtc = DateTimeOffset.UtcNow
        });
        Assert.Equal(0, await database.ExecuteScalarAsync<long>("""
            SELECT COUNT(*) FROM AccountDevices ad JOIN Accounts a ON a.AccountId=ad.AccountId
            WHERE ad.DeviceId='other-visible' AND a.IsDefault
            """));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM AccountDevices WHERE DeviceId='other-visible'"));

        store.RenameDeviceForAdministration("other-visible", "Other-Renamed-Robot");
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM AccountDevices WHERE DeviceId='other-visible' AND AccountId='other-account'"));
        Assert.Throws<InvalidOperationException>(() =>
            store.MergeRobotRecordsForAdministration("other-visible", "openjibo-bootstrap-default"));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM AccountDevices WHERE DeviceId='other-visible' AND AccountId='other-account'"));

        await database.ExecuteAsync("""
            INSERT INTO Devices (DeviceId,RobotId,FriendlyName,RegistrationSource)
            VALUES ('single-source','single-source','Single Source','physical'),
                   ('single-target','single-target','Single Target','physical'),
                   ('multi-source','multi-source','Multi Source','physical'),
                   ('multi-target','multi-target','Multi Target','physical'),
                   ('zero-source','zero-source','Zero Source','physical'),
                   ('zero-target','zero-target','Zero Target','physical');
            INSERT INTO AccountDevices (AccountId,DeviceId)
            VALUES ('other-account','single-source'),('other-account','single-target'),
                   ('other-account','multi-source'),('other-account','multi-target'),
                   ('usr_openjibo_owner','multi-source'),('usr_openjibo_owner','multi-target');
            INSERT INTO RobotCredentialBindings (AccessKeyFingerprint,DeviceId,ClaimedUtc,ClaimSource)
            VALUES ('0123456789abcdef','multi-source',NOW(),'integration-test');
            """);
        store.MergeRobotRecordsForAdministration("single-source", "single-target",
            new RobotMergePrecondition([], []));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM AccountDevices WHERE DeviceId='single-source' AND AccountId='other-account'"));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM RobotIdentityLinks WHERE ObservedDeviceId='single-source' AND InventoryDeviceId='single-target'"));
        Assert.True(store.BindObservedIdentityToDevice("peer-replica-observed", "single-target"));
        var peerStore = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector());
        var peerSession = peerStore.OpenSession("neo-hub-proactive", "peer-replica-observed",
            "peer-replica-token", "neo-hub", "/v1/proactive");
        Assert.Equal("single-target", peerSession.Metadata["registeredDeviceId"]?.ToString());

        Assert.Throws<InvalidOperationException>(() => store.MergeRobotRecordsForAdministration(
            "multi-source", "multi-target", new RobotMergePrecondition([], [])));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Devices WHERE DeviceId='multi-source' AND NOT IsHidden AND ArchivedUtc IS NULL"));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM RobotCredentialBindings WHERE AccessKeyFingerprint='0123456789abcdef' AND DeviceId='multi-source'"));

        store.MergeRobotRecordsForAdministration("multi-source", "multi-target",
            new RobotMergePrecondition([], ["0123456789abcdef"]));
        Assert.Equal(2, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM AccountDevices WHERE DeviceId='multi-source'"));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM RobotCredentialBindings WHERE AccessKeyFingerprint='0123456789abcdef' AND DeviceId='multi-target'"));
        store.MergeRobotRecordsForAdministration("zero-source", "zero-target");
        Assert.Equal(0, await database.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM AccountDevices WHERE DeviceId IN ('zero-source','zero-target')"));
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task TwoBackups_CreateBoundedManifestsAndExternalPayloadsWithoutSnapshotRewrite()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        var payloadRoot = Path.Combine(Path.GetTempPath(), $"openjibo-cloud-backup-test-{Guid.NewGuid():N}");
        try
        {
            var store = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector(),
                backupPayloadStore: new DirectoryBackupPayloadStore(payloadRoot));
            await database.ExecuteAsync("""
                INSERT INTO PersistenceSnapshots (SnapshotName, SnapshotJson)
                VALUES ('cloud-state-integration-marker', '{"marker":"backup-stable"}')
                """);
            var snapshotBefore = await database.ReadSnapshotMarkerAsync();
            var loopId = Assert.Single(store.GetLoops()).LoopId;

            var first = store.CreateBackup(loopId, "first");
            var second = store.CreateBackup(loopId, "second");

            Assert.NotEqual(first.BackupId, second.BackupId);
            Assert.Null(first.SnapshotJson);
            Assert.Null(second.SnapshotJson);
            Assert.Equal(2, store.GetBackups().Count);
            Assert.Equal(2, await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM BackupManifests"));
            Assert.Equal(2, Directory.GetFiles(payloadRoot, "*.json", SearchOption.AllDirectories).Length);
            Assert.True(await database.ExecuteScalarAsync<long>(
                "SELECT MAX(OCTET_LENGTH(BlobUri)+OCTET_LENGTH(ContentSha256)+OCTET_LENGTH(Name)) FROM BackupManifests") < 2048);
            Assert.Equal(snapshotBefore, await database.ReadSnapshotMarkerAsync());
        }
        finally
        {
            if (Directory.Exists(payloadRoot)) Directory.Delete(payloadRoot, recursive: true);
        }
    }

    private static DeviceRegistration Device(string id, string name) => new()
    {
        DeviceId = id, RobotId = name, FriendlyName = name,
        RegistrationSource = RobotRegistrationSources.Physical
    };

    private sealed class PlaintextTestProtector : ICloudStateSecretProtector
    {
        public string KeyId => "integration-test-plaintext";
        public byte[] Protect(string plaintext) => Encoding.UTF8.GetBytes(plaintext);
        public string Unprotect(byte[] ciphertext) => Encoding.UTF8.GetString(ciphertext);
    }

    private sealed class CloudStateTestDatabase : IAsyncDisposable
    {
        private const string ConnectionVariable = "OPENJIBO_TEST_POSTGRES_CONNECTION_STRING";
        private readonly string _adminConnectionString;
        private readonly string _schemaName;

        private CloudStateTestDatabase(string adminConnectionString, string schemaName)
        {
            _adminConnectionString = adminConnectionString;
            _schemaName = schemaName;
            ConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                SearchPath = schemaName,
                ApplicationName = "OpenJibo.CloudState.IntegrationTests",
                MaxPoolSize = 4
            }.ConnectionString;
        }

        internal string ConnectionString { get; }

        internal static async Task<CloudStateTestDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable(ConnectionVariable);
            if (string.IsNullOrWhiteSpace(admin)) throw new InvalidOperationException($"Set {ConnectionVariable}.");
            var schema = $"openjibo_cloud_test_{Guid.NewGuid():N}";
            var database = new CloudStateTestDatabase(admin, schema);
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE SCHEMA {QuoteIdentifier(schema)}";
                await command.ExecuteNonQueryAsync();
            }
            try
            {
                await database.ApplyMigrationsAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<T> ExecuteScalarAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync();
            return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal async Task<(string Json, DateTimeOffset UpdatedUtc)> ReadSnapshotMarkerAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SnapshotJson, UpdatedUtc FROM PersistenceSnapshots WHERE SnapshotName='cloud-state-integration-marker'";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
        }

        internal async Task PreseedFleetAsync(int count)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Devices (DeviceId,RobotId,FriendlyName,RegistrationSource,IsDefault)
                SELECT 'fleet-device-'||value, 'fleet-robot-'||value, 'Fleet Robot '||value, 'physical', FALSE
                FROM generate_series(1, @count) value;
                """;
            command.Parameters.AddWithValue("count", count);
            await command.ExecuteNonQueryAsync();
            Assert.Equal(count, await ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Devices"));
        }

        private async Task ApplyMigrationsAsync()
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql");
            var scripts = Directory.GetFiles(directory, "*.sql")
                .Where(path => !path.EndsWith(".personal-memory.sql", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            foreach (var script in scripts)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = await File.ReadAllTextAsync(script);
                await command.ExecuteNonQueryAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA {QuoteIdentifier(_schemaName)} CASCADE";
            await command.ExecuteNonQueryAsync();
        }

        private static string QuoteIdentifier(string identifier)
        {
            Debug.Assert(identifier.StartsWith("openjibo_cloud_test_", StringComparison.Ordinal));
            return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }
    }
}
