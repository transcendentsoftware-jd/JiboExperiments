using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace Jibo.Cloud.Infrastructure.Persistence;

/// <summary>
/// Explicit, one-time importer for the legacy <c>PersistenceSnapshots/cloud-state</c>
/// document. It is deliberately not wired into service startup.
/// </summary>
public sealed class PostgreSqlCloudStateSnapshotImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly ICloudStateSecretProtector _secretProtector;
    private readonly IBackupPayloadStore? _backupPayloadStore;

    public PostgreSqlCloudStateSnapshotImporter(
        NpgsqlDataSource dataSource, ICloudStateSecretProtector secretProtector,
        IBackupPayloadStore? backupPayloadStore = null)
    {
        _dataSource = dataSource;
        _secretProtector = secretProtector;
        _backupPayloadStore = backupPayloadStore;
    }

    public async Task<CloudStateSnapshotImportResult> ImportAsync(
        string snapshotName = "cloud-state", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotName);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        await ExecuteAsync(connection, transaction,
            "SELECT pg_advisory_xact_lock(hashtext(@value))", cancellationToken, ("value", snapshotName));

        string sourceJson;
        DateTimeOffset? sourceUpdatedUtc;
        await using (var load = new NpgsqlCommand("""
                                                  SELECT SnapshotJson, UpdatedUtc
                                                  FROM PersistenceSnapshots
                                                  WHERE SnapshotName = @snapshotName
                                                  FOR SHARE
                                                  """, connection, transaction))
        {
            load.Parameters.AddWithValue("snapshotName", snapshotName);
            await using var reader = await load.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException($"Legacy snapshot '{snapshotName}' was not found.");
            sourceJson = reader.GetString(0);
            sourceUpdatedUtc = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        }

        var sourceSha256 = Sha256(sourceJson);
        string? existingName = null;
        Dictionary<string, int>? existingCounts = null;
        await using (var check = new NpgsqlCommand("""
                                                   SELECT ImportName, ImportedCounts
                                                   FROM CloudStateImports
                                                   WHERE SourceSnapshotName = @snapshotName
                                                     AND SourceSha256 = @sha256
                                                   """, connection, transaction))
        {
            check.Parameters.AddWithValue("snapshotName", snapshotName);
            check.Parameters.AddWithValue("sha256", sourceSha256);
            await using var reader = await check.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                existingName = reader.GetString(0);
                existingCounts =
                    JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(1), JsonOptions) ?? [];
            }
        }

        if (existingName is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CloudStateSnapshotImportResult(
                existingName, snapshotName, sourceSha256, true, existingCounts!);
        }

        var snapshot = ParseSnapshot(sourceJson);
        var counts = snapshot.GetDurableFamilyCounts();
        await ImportSnapshotAsync(connection, transaction, snapshot, snapshotName, sourceSha256, cancellationToken);

        var importName = $"legacy-cloud-state-{sourceSha256[..16]}";
        await using (var mark = new NpgsqlCommand("""
                                                  INSERT INTO CloudStateImports
                                                      (ImportName, SourceSnapshotName, SourceSchemaVersion,
                                                       SourceRevision, SourceUpdatedUtc, SourceSha256, ImportedCounts)
                                                  VALUES
                                                      (@name, @snapshotName, @schemaVersion,
                                                       @revision, @sourceUpdatedUtc, @sha256, @counts::jsonb)
                                                  """, connection, transaction))
        {
            mark.Parameters.AddWithValue("name", importName);
            mark.Parameters.AddWithValue("snapshotName", snapshotName);
            mark.Parameters.AddWithValue("schemaVersion", (object?)snapshot.SchemaVersion ?? DBNull.Value);
            mark.Parameters.AddWithValue("revision", snapshot.Revision);
            mark.Parameters.AddWithValue("sourceUpdatedUtc", (object?)sourceUpdatedUtc ?? DBNull.Value);
            mark.Parameters.AddWithValue("sha256", sourceSha256);
            mark.Parameters.AddWithValue("counts", JsonSerializer.Serialize(counts));
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteAsync(connection, transaction, """
                                                      UPDATE CloudStateMetadata
                                                      SET Revision = GREATEST(Revision, @revision),
                                                          UpdatedUtc = NOW()
                                                      WHERE StateKey = 'cloud-state'
                                                      """, cancellationToken, ("revision", snapshot.Revision));
        await transaction.CommitAsync(cancellationToken);
        return new CloudStateSnapshotImportResult(importName, snapshotName, sourceSha256, false, counts);
    }

    internal static LegacyCloudStateSnapshot ParseSnapshot(string sourceJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJson);
        try
        {
            return JsonSerializer.Deserialize<LegacyCloudStateSnapshot>(sourceJson, JsonOptions)
                   ?? throw new InvalidOperationException("The legacy cloud-state snapshot was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The legacy cloud-state snapshot is not valid JSON.", exception);
        }
    }

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>
    /// Returns redacted, aggregate counts from a legacy cloud-state document.
    /// This intentionally exposes no document identifiers, credentials, or payloads.
    /// </summary>
    public static IReadOnlyDictionary<string, int> GetLegacyDurableFamilyCounts(string sourceJson) =>
        ParseSnapshot(sourceJson).GetDurableFamilyCounts();

    internal static RobotIdentityLinkAuditEntry[] BuildLegacyIdentityLinkAudit(
        string inventoryDeviceId, DateTimeOffset occurredUtc) =>
        [new RobotIdentityLinkAuditEntry(
            "linked", null, inventoryDeviceId, "legacy-cloud-state-import", occurredUtc)];

    private async Task ImportSnapshotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        LegacyCloudStateSnapshot snapshot, string sourceSnapshotName, string sourceSha256,
        CancellationToken cancellationToken)
    {
        var account = snapshot.Account;
        if (account is not null)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE Accounts SET IsDefault=FALSE WHERE IsDefault AND AccountId<>@id",
                cancellationToken, ("id", account.AccountId));
            var encrypted = Protect(account.SecretAccessKey);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO Accounts
                    (AccountId, Email, FirstName, LastName, AccessKeyId, SecretAccessKeyCiphertext,
                     SecretWrappingKeyId, IsDefault)
                VALUES (@id, @email, @first, @last, @access, @secret, @keyId, TRUE)
                ON CONFLICT (AccountId) DO UPDATE SET
                    Email=EXCLUDED.Email, FirstName=EXCLUDED.FirstName, LastName=EXCLUDED.LastName,
                    AccessKeyId=EXCLUDED.AccessKeyId, SecretAccessKeyCiphertext=EXCLUDED.SecretAccessKeyCiphertext,
                    SecretWrappingKeyId=EXCLUDED.SecretWrappingKeyId, UpdatedUtc=NOW()
                """, cancellationToken, ("id", account.AccountId), ("email", account.Email),
                ("first", account.FirstName), ("last", account.LastName), ("access", account.AccessKeyId),
                ("secret", encrypted), ("keyId", _secretProtector.KeyId));
        }

        var devices = snapshot.AllDevices();
        if (snapshot.Robot is not null)
            await ExecuteAsync(connection, transaction,
                "UPDATE Devices SET IsDefault=FALSE WHERE IsDefault AND DeviceId<>@id",
                cancellationToken, ("id", snapshot.Robot.DeviceId));
        foreach (var device in devices)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO Devices
                    (DeviceId, RobotId, FriendlyName, FirmwareVersion, ApplicationVersion, IsActive,
                     CertificateThumbprint, IssuedIdentityId, BuildHash, ConfigHash, VerifiedSerialNumber,
                     SerialEvidenceSource, SerialEvidenceVerifiedUtc, RegistrationSource, IsHidden, IsDefault,
                     ArchivedUtc)
                VALUES (@deviceId,@robotId,@friendly,@firmware,@application,@active,@certificate,@identity,
                        @build,@config,@serial,@serialSource,@serialUtc,@registration,@hidden,@isDefault,@archived)
                ON CONFLICT (DeviceId) DO UPDATE SET
                    RobotId=EXCLUDED.RobotId, FriendlyName=EXCLUDED.FriendlyName,
                    FirmwareVersion=EXCLUDED.FirmwareVersion, ApplicationVersion=EXCLUDED.ApplicationVersion,
                    IsActive=EXCLUDED.IsActive, CertificateThumbprint=EXCLUDED.CertificateThumbprint,
                    IssuedIdentityId=EXCLUDED.IssuedIdentityId, BuildHash=EXCLUDED.BuildHash,
                    ConfigHash=EXCLUDED.ConfigHash, VerifiedSerialNumber=EXCLUDED.VerifiedSerialNumber,
                    SerialEvidenceSource=EXCLUDED.SerialEvidenceSource,
                    SerialEvidenceVerifiedUtc=EXCLUDED.SerialEvidenceVerifiedUtc,
                    RegistrationSource=EXCLUDED.RegistrationSource, IsHidden=EXCLUDED.IsHidden,
                    ArchivedUtc=EXCLUDED.ArchivedUtc, UpdatedUtc=NOW()
                """, cancellationToken,
                ("deviceId", device.DeviceId), ("robotId", device.RobotId), ("friendly", device.FriendlyName),
                ("firmware", device.FirmwareVersion), ("application", device.ApplicationVersion),
                ("active", device.IsActive), ("certificate", device.CertificateThumbprint),
                ("identity", device.IssuedIdentityId), ("build", device.BuildHash), ("config", device.ConfigHash),
                ("serial", device.VerifiedSerialNumber), ("serialSource", device.SerialEvidenceSource),
                ("serialUtc", device.SerialEvidenceVerifiedUtc), ("registration", device.RegistrationSource),
                ("hidden", device.IsHidden),
                ("isDefault", snapshot.Robot?.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase) == true),
                ("archived", device.ArchivedUtc));

            if (account is not null)
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO AccountDevices (AccountId, DeviceId)
                    VALUES (@accountId, @deviceId) ON CONFLICT DO NOTHING
                    """, cancellationToken, ("accountId", account.AccountId), ("deviceId", device.DeviceId));

            foreach (var mapping in device.HostMappings)
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO DeviceHostMappings (DeviceId, MappingKey, MappingValue)
                    VALUES (@deviceId,@key,@value)
                    ON CONFLICT (DeviceId,MappingKey) DO UPDATE SET MappingValue=EXCLUDED.MappingValue,UpdatedUtc=NOW()
                    """, cancellationToken, ("deviceId", device.DeviceId), ("key", mapping.Key),
                    ("value", mapping.Value));
        }

        if (snapshot.RobotProfile is not null)
        {
            var profileDevice = devices.FirstOrDefault(d =>
                d.RobotId.Equals(snapshot.RobotProfile.RobotId, StringComparison.OrdinalIgnoreCase));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO RobotProfiles (RobotId,DeviceId,Payload,CalibrationPayload,CreatedUtc,UpdatedUtc)
                VALUES (@robotId,@deviceId,@payload::jsonb,@calibration::jsonb,@created,@updated)
                ON CONFLICT (RobotId) DO UPDATE SET DeviceId=EXCLUDED.DeviceId,Payload=EXCLUDED.Payload,
                    CalibrationPayload=EXCLUDED.CalibrationPayload,UpdatedUtc=EXCLUDED.UpdatedUtc
                """, cancellationToken, ("robotId", snapshot.RobotProfile.RobotId),
                ("deviceId", profileDevice?.DeviceId),
                ("payload", JsonSerializer.Serialize(snapshot.RobotProfile.Payload)),
                ("calibration", JsonSerializer.Serialize(snapshot.RobotProfile.CalibrationPayload)),
                ("created", snapshot.RobotProfile.CreatedUtc), ("updated", snapshot.RobotProfile.UpdatedUtc));
        }

        foreach (var binding in snapshot.RobotCredentialBindings ?? [])
            await ExecuteAsync(connection, transaction, """
                INSERT INTO RobotCredentialBindings (AccessKeyFingerprint,DeviceId,ClaimedUtc,ClaimSource)
                VALUES (@fingerprint,@deviceId,@claimed,@source)
                ON CONFLICT (AccessKeyFingerprint) DO UPDATE SET DeviceId=EXCLUDED.DeviceId,
                    ClaimedUtc=EXCLUDED.ClaimedUtc,ClaimSource=EXCLUDED.ClaimSource
                """, cancellationToken, ("fingerprint", binding.AccessKeyFingerprint),
                ("deviceId", binding.DeviceId), ("claimed", binding.ClaimedUtc), ("source", binding.ClaimSource));

        await ImportUsersAsync(connection, transaction, snapshot, cancellationToken);
        await ImportLoopsAsync(connection, transaction, snapshot, devices, cancellationToken);
        await ImportTopologyChildrenAsync(connection, transaction, snapshot, sourceSnapshotName, sourceSha256,
            cancellationToken);
        await ImportCatalogsAsync(connection, transaction, snapshot, cancellationToken);
        await ImportSecretsAndTokensAsync(connection, transaction, snapshot, cancellationToken);
    }

    private async Task ImportUsersAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LegacyCloudStateSnapshot snapshot, CancellationToken ct)
    {
        foreach (var user in CanonicalUsers(snapshot.Users))
        {
            var secret = Protect(user.SecretAccessKey);
            await ExecuteAsync(c, tx, """
                INSERT INTO Users (UserId,Email,PasswordHash,PasswordSalt,FirstName,LastName,Gender,Birthday,
                    AccessKeyId,SecretAccessKeyCiphertext,SecretWrappingKeyId,IsActive,CreatedUtc)
                SELECT @id,@email,@hash,@salt,@first,@last,@gender,@birthday,@access,@secret,@keyId,@active,@created
                WHERE NOT EXISTS (
                    SELECT 1 FROM Users
                    WHERE LOWER(Email)=LOWER(@email) AND LOWER(UserId)<>LOWER(@id)
                )
                ON CONFLICT (UserId) DO UPDATE SET Email=EXCLUDED.Email,PasswordHash=EXCLUDED.PasswordHash,
                    PasswordSalt=EXCLUDED.PasswordSalt,FirstName=EXCLUDED.FirstName,LastName=EXCLUDED.LastName,
                    Gender=EXCLUDED.Gender,Birthday=EXCLUDED.Birthday,AccessKeyId=EXCLUDED.AccessKeyId,
                    SecretAccessKeyCiphertext=EXCLUDED.SecretAccessKeyCiphertext,
                    SecretWrappingKeyId=EXCLUDED.SecretWrappingKeyId,IsActive=EXCLUDED.IsActive,UpdatedUtc=NOW()
                """, ct, ("id", user.Id), ("email", user.Email), ("hash", user.PasswordHash),
                ("salt", user.Salt), ("first", user.FirstName), ("last", user.LastName),
                ("gender", user.Gender), ("birthday", user.Birthday), ("access", user.AccessKeyId),
                ("secret", secret), ("keyId", _secretProtector.KeyId), ("active", user.IsActive),
                ("created", user.CreatedUtc));
        }
    }

    internal static IReadOnlyList<UserRecord> CanonicalUsers(IEnumerable<UserRecord>? users)
    {
        var canonical = new List<UserRecord>();
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var user in users ?? [])
            if (emails.Add(user.Email.Trim()))
                canonical.Add(user);

        return canonical;
    }

    private static async Task ImportLoopsAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LegacyCloudStateSnapshot snapshot, DeviceRegistration[] devices, CancellationToken ct)
    {
        foreach (var loop in snapshot.Loops ?? [])
        {
            await ExecuteAsync(c, tx, """
                INSERT INTO Loops (LoopId,Name,OwnerAccountId,PrimaryRobotId,PrimaryRobotFriendlyId,IsSuspended,
                    CreatedUtc,UpdatedUtc)
                VALUES (@id,@name,@owner,@robot,@friendly,@suspended,@created,@updated)
                ON CONFLICT (LoopId) DO UPDATE SET Name=EXCLUDED.Name,OwnerAccountId=EXCLUDED.OwnerAccountId,
                    PrimaryRobotId=EXCLUDED.PrimaryRobotId,PrimaryRobotFriendlyId=EXCLUDED.PrimaryRobotFriendlyId,
                    IsSuspended=EXCLUDED.IsSuspended,UpdatedUtc=EXCLUDED.UpdatedUtc
                """, ct, ("id", loop.LoopId), ("name", loop.Name), ("owner", loop.OwnerAccountId),
                ("robot", loop.RobotId), ("friendly", loop.RobotFriendlyId), ("suspended", loop.IsSuspended),
                ("created", loop.CreatedUtc), ("updated", loop.UpdatedUtc));
            var device = devices.FirstOrDefault(d =>
                d.DeviceId.Equals(loop.RobotFriendlyId, StringComparison.OrdinalIgnoreCase) ||
                d.RobotId.Equals(loop.RobotId, StringComparison.OrdinalIgnoreCase));
            if (device is not null)
            {
                await ExecuteAsync(c, tx,
                    "UPDATE LoopDevices SET IsPrimary=FALSE WHERE LoopId=@loop AND DeviceId<>@device",
                    ct, ("loop", loop.LoopId), ("device", device.DeviceId));
                await ExecuteAsync(c, tx, """
                    INSERT INTO LoopDevices (LoopId,DeviceId,IsPrimary) VALUES (@loop,@device,TRUE)
                    ON CONFLICT (LoopId,DeviceId) DO UPDATE SET IsPrimary=TRUE
                    """, ct, ("loop", loop.LoopId), ("device", device.DeviceId));
            }
        }
    }

    private static async Task ImportTopologyChildrenAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LegacyCloudStateSnapshot s, string sourceSnapshotName, string sourceSha256, CancellationToken ct)
    {
        foreach (var member in s.LoopMembers ?? [])
        {
            if (!await ParentExistsAsync(c, tx, "Loops", "LoopId", member.LoopId, ct))
            {
                await QuarantineAsync(c, tx, sourceSnapshotName, sourceSha256, "LoopMember", member.Id,
                    $"missing-parent:Loops/LoopId={member.LoopId}", member, ct);
                continue;
            }

            await ExecuteAsync(c, tx, """
                INSERT INTO LoopMembers (MemberId,LoopId,AccountId,Email,FirstName,LastName,Gender,Birthday,IsChild,
                    PhoneNumber,Status,MemberType,Nickname,PhoneticName,FaceEnrolled,VoiceEnrolled,LegalGuardianId,
                    AgreementId,CreatedUtc,PortalEditedUtc)
                VALUES (@id,@loop,@account,@email,@first,@last,@gender,@birthday,@child,@phone,@status,@type,
                    @nickname,@phonetic,@face,@voice,@guardian,@agreement,@created,@portal)
                ON CONFLICT (MemberId) DO UPDATE SET FirstName=EXCLUDED.FirstName,LastName=EXCLUDED.LastName,
                    Gender=EXCLUDED.Gender,Birthday=EXCLUDED.Birthday,IsChild=EXCLUDED.IsChild,
                    Nickname=EXCLUDED.Nickname,PhoneticName=EXCLUDED.PhoneticName,
                    FaceEnrolled=EXCLUDED.FaceEnrolled,VoiceEnrolled=EXCLUDED.VoiceEnrolled,
                    LegalGuardianId=EXCLUDED.LegalGuardianId,PortalEditedUtc=EXCLUDED.PortalEditedUtc,UpdatedUtc=NOW()
                """, ct, ("id", member.Id), ("loop", member.LoopId), ("account", member.AccountId),
                ("email", member.Email), ("first", member.FirstName), ("last", member.LastName),
                ("gender", member.Gender), ("birthday", member.Birthday), ("child", member.IsChild),
                ("phone", member.PhoneNumber), ("status", member.Status), ("type", member.Type),
                ("nickname", member.Nickname), ("phonetic", member.PhoneticName), ("face", member.FaceEnrolled),
                ("voice", member.VoiceEnrolled), ("guardian", member.LegalGuardianId),
                ("agreement", member.AgreementId), ("created", member.CreatedUtc),
                ("portal", member.PortalEditedUtc));
        }

        foreach (var person in s.People ?? [])
        {
            var missingParents = new List<string>();
            if (!await ParentExistsAsync(c, tx, "Accounts", "AccountId", person.AccountId, ct))
                missingParents.Add($"Accounts/AccountId={person.AccountId}");
            if (!await ParentExistsAsync(c, tx, "Loops", "LoopId", person.LoopId, ct))
                missingParents.Add($"Loops/LoopId={person.LoopId}");
            if (missingParents.Count > 0)
            {
                await QuarantineAsync(c, tx, sourceSnapshotName, sourceSha256, "Person", person.PersonId,
                    $"missing-parent:{string.Join(',', missingParents)}", person, ct);
                continue;
            }

            await ExecuteAsync(c, tx, """
                INSERT INTO People (PersonId,AccountId,LoopId,RobotId,DisplayName,Alias,IsPrimary,CreatedUtc,UpdatedUtc)
                VALUES (@id,@account,@loop,@robot,@display,@alias,@primary,@created,@updated)
                ON CONFLICT (AccountId,LoopId,PersonId) DO UPDATE SET DisplayName=EXCLUDED.DisplayName,Alias=EXCLUDED.Alias,
                    IsPrimary=EXCLUDED.IsPrimary,UpdatedUtc=EXCLUDED.UpdatedUtc
                """, ct, ("id", person.PersonId), ("account", person.AccountId), ("loop", person.LoopId),
                ("robot", person.RobotId), ("display", person.DisplayName), ("alias", person.Alias),
                ("primary", person.IsPrimary), ("created", person.CreatedUtc), ("updated", person.UpdatedUtc));
        }

        foreach (var observation in s.RecognitionObservations ?? [])
        {
            var missingParents = new List<string>();
            if (!await ParentExistsAsync(c, tx, "Loops", "LoopId", observation.LoopId, ct))
                missingParents.Add($"Loops/LoopId={observation.LoopId}");
            if (!await ParentExistsAsync(c, tx, "LoopMembers", "MemberId", observation.MemberId, ct))
                missingParents.Add($"LoopMembers/MemberId={observation.MemberId}");
            if (missingParents.Count > 0)
            {
                await QuarantineAsync(c, tx, sourceSnapshotName, sourceSha256, "RecognitionObservation",
                    observation.ObservationId, $"missing-parent:{string.Join(',', missingParents)}", observation, ct);
                continue;
            }

            await ExecuteAsync(c, tx, """
                INSERT INTO RecognitionObservations (ObservationId,LoopId,MemberId,RobotId,Modality,Outcome,
                    Confidence,Source,ObservedUtc)
                VALUES (@id,@loop,@member,@robot,@modality,@outcome,@confidence,@source,@observed)
                ON CONFLICT (ObservationId) DO NOTHING
                """, ct, ("id", observation.ObservationId), ("loop", observation.LoopId),
                ("member", observation.MemberId), ("robot", observation.RobotId),
                ("modality", observation.Modality), ("outcome", observation.Outcome),
                ("confidence", observation.Confidence), ("source", observation.Source),
                ("observed", observation.ObservedUtc));
        }
    }

    private static async Task<bool> ParentExistsAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        string table, string keyColumn, string key, CancellationToken ct)
    {
        // Identifiers are selected exclusively by the importer, never from snapshot data.
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {table} WHERE {keyColumn}=@key)", c, tx);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task QuarantineAsync<T>(NpgsqlConnection c, NpgsqlTransaction tx,
        string sourceSnapshotName, string sourceSha256, string entityType, string entityKey, string reason,
        T payload, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, """
            INSERT INTO CloudStateImportRejections
                (SourceSnapshotName,SourceSha256,EntityType,EntityKey,Reason,Payload)
            VALUES (@snapshot,@sha,@type,@key,@reason,@payload::jsonb)
            ON CONFLICT (SourceSnapshotName,SourceSha256,EntityType,EntityKey,Reason)
            DO UPDATE SET Payload=EXCLUDED.Payload,RejectedUtc=NOW()
            """, ct, ("snapshot", sourceSnapshotName), ("sha", sourceSha256), ("type", entityType),
            ("key", entityKey), ("reason", reason), ("payload", JsonSerializer.Serialize(payload)));
    }
    private async Task ImportCatalogsAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LegacyCloudStateSnapshot s, CancellationToken ct)
    {
        foreach (var server in s.TrustedServers ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO TrustedServers (ServerId,CanonicalHost,DisplayName,ServerKind,IsListed,
                    AcceptsPublicConnections,ParticipatesInCloudSync,RequiresHttps,IsTrustRoot,IsActive,Description,
                    RegisteredAtUtc,UpdatedAtUtc,LastSeenAtUtc)
                VALUES (@id,@host,@display,@kind,@listed,@public,@sync,@https,@root,@active,@description,
                    @registered,@updated,@seen)
                ON CONFLICT (ServerId) DO UPDATE SET CanonicalHost=EXCLUDED.CanonicalHost,
                    DisplayName=EXCLUDED.DisplayName,IsActive=EXCLUDED.IsActive,UpdatedAtUtc=EXCLUDED.UpdatedAtUtc,
                    LastSeenAtUtc=EXCLUDED.LastSeenAtUtc
                """, ct, ("id", server.ServerId), ("host", server.CanonicalHost),
                ("display", server.DisplayName), ("kind", server.ServerKind), ("listed", server.IsListed),
                ("public", server.AcceptsPublicConnections), ("sync", server.ParticipatesInCloudSync),
                ("https", server.RequiresHttps), ("root", server.IsTrustRoot), ("active", server.IsActive),
                ("description", server.Description), ("registered", server.RegisteredAtUtc),
                ("updated", server.UpdatedAtUtc), ("seen", server.LastSeenAtUtc));
        foreach (var admission in s.TrustedServerAdmissions ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO TrustedServerAdmissions (AdmissionId,ServerId,CanonicalHost,ServerKind,Action,
                    ActorDeviceId,ActorFriendlyId,Reason,SignatureAlgorithm,SignatureKeyId,Payload,Signature,CreatedUtc)
                VALUES (@id,@server,@host,@kind,@action,@device,@friendly,@reason,@algorithm,@key,@payload,
                    @signature,@created) ON CONFLICT (AdmissionId) DO NOTHING
                """, ct, ("id", admission.AdmissionId), ("server", admission.ServerId),
                ("host", admission.CanonicalHost), ("kind", admission.ServerKind), ("action", admission.Action),
                ("device", admission.ActorDeviceId), ("friendly", admission.ActorFriendlyId),
                ("reason", admission.Reason), ("algorithm", admission.SignatureAlgorithm),
                ("key", admission.SignatureKeyId), ("payload", admission.Payload),
                ("signature", admission.Signature), ("created", admission.CreatedUtc));
        foreach (var anchor in s.RevokedIdentityGraphAnchors ?? [])
            await ExecuteAsync(c, tx, "INSERT INTO RevokedIdentityGraphAnchors (Anchor) VALUES (@anchor) ON CONFLICT DO NOTHING",
                ct, ("anchor", anchor));
        await ImportOperationalFamiliesAsync(c, tx, s, ct);
    }

    private async Task ImportOperationalFamiliesAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LegacyCloudStateSnapshot s, CancellationToken ct)
    {
        foreach (var update in s.Updates ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO UpdateManifests (UpdateId,CreatedUtc,FromVersion,ToVersion,Changes,Url,ShaHash,
                    ContentLength,Subsystem,Filter)
                VALUES (@id,@created,@from,@to,@changes,@url,@sha,@length,@subsystem,@filter)
                ON CONFLICT (UpdateId) DO NOTHING
                """, ct, ("id", update.UpdateId), ("created", update.CreatedUtc), ("from", update.FromVersion),
                ("to", update.ToVersion), ("changes", update.Changes), ("url", update.Url),
                ("sha", update.ShaHash), ("length", update.Length), ("subsystem", update.Subsystem),
                ("filter", update.Filter));
        foreach (var media in s.Media ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO MediaRecords (MediaPath,CreatedUtc,MediaType,Reference,AccountId,LoopId,BlobUri,
                    IsEncrypted,IsDeleted,Meta)
                VALUES (@path,@created,@type,@reference,@account,@loop,@uri,@encrypted,@deleted,@meta::jsonb)
                ON CONFLICT (MediaPath) DO NOTHING
                """, ct, ("path", media.Path), ("created", media.CreatedUtc), ("type", media.MediaType),
                ("reference", media.Reference), ("account", media.AccountId), ("loop", media.LoopId),
                ("uri", media.Url), ("encrypted", media.IsEncrypted), ("deleted", media.IsDeleted),
                ("meta", JsonSerializer.Serialize(media.Meta)));
        foreach (var exported in await ExportBackupPayloadsAsync(s.Backups ?? [], _backupPayloadStore, ct))
        {
            var backup = exported.Source;
            await ExecuteAsync(c, tx, """
                INSERT INTO BackupManifests (BackupId,AccountId,LoopId,Name,BlobUri,ContentSha256,ContentLength,
                    BackupSchemaVersion,CreatedUtc)
                VALUES (@id,@account,@loop,@name,@uri,@sha,@length,1,@created)
                ON CONFLICT (BackupId) DO NOTHING
                """, ct, ("id", backup.BackupId), ("account", s.Account?.AccountId), ("loop", backup.LoopId),
                ("name", backup.Name),
                ("uri", exported.Uri), ("sha", exported.Sha256), ("length", exported.Length),
                ("created", backup.CreatedUtc));
        }
        foreach (var keyRequest in s.KeyRequests ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO KeyRequests (RequestId,LoopId,PublicKey,EncryptedKey,CreatedUtc)
                VALUES (@id,@loop,@public,@encrypted,@created) ON CONFLICT (RequestId) DO NOTHING
                """, ct, ("id", keyRequest.RequestId), ("loop", keyRequest.LoopId),
                ("public", keyRequest.PublicKey), ("encrypted", keyRequest.EncryptedKey),
                ("created", keyRequest.CreatedUtc));
        await ImportReportsAsync(c, tx, s, ct);
    }

    private static async Task ImportReportsAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LegacyCloudStateSnapshot s, CancellationToken ct)
    {
        foreach (var item in s.Holidays ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO HolidayOverrides (HolidayId,EventId,Name,Category,Subcategory,LoopId,MemberId,IsEnabled,
                    EventDate,EndDate,Source,CountryCode,CreatedUtc)
                VALUES (@id,@event,@name,@category,@subcategory,@loop,@member,@enabled,@date,@end,@source,@country,@created)
                ON CONFLICT (HolidayId) DO NOTHING
                """, ct, ("id", item.Id), ("event", item.EventId), ("name", item.Name),
                ("category", item.Category), ("subcategory", item.Subcategory), ("loop", item.LoopId),
                ("member", item.MemberId), ("enabled", item.IsEnabled), ("date", item.Date),
                ("end", item.EndDate), ("source", item.Source), ("country", item.CountryCode),
                ("created", item.Created));
        foreach (var item in s.CommuteProfiles ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO CommuteProfiles (CommuteProfileId,LoopId,MemberId,IsEnabled,IsComplete,Mode,WorkHour,
                    WorkMinute,OriginName,DestinationName,TypicalDurationMinutes,CreatedUtc,UpdatedUtc)
                VALUES (@id,@loop,@member,@enabled,@complete,@mode,@hour,@minute,@origin,@destination,@duration,
                    @created,@updated) ON CONFLICT (CommuteProfileId) DO NOTHING
                """, ct, ("id", item.Id), ("loop", item.LoopId), ("member", item.MemberId),
                ("enabled", item.IsEnabled), ("complete", item.IsComplete), ("mode", item.Mode),
                ("hour", item.WorkHour), ("minute", item.WorkMinute), ("origin", item.OriginName),
                ("destination", item.DestinationName), ("duration", item.TypicalDurationMinutes),
                ("created", item.Created), ("updated", item.Updated));
        foreach (var item in s.CalendarEvents ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO CalendarEvents (CalendarEventId,LoopId,Summary,TimeLabel,EventDate,EndDate,IsAllDay,
                    IsEnabled,Source,MemberId,CreatedUtc)
                VALUES (@id,@loop,@summary,@time,@date,@end,@allDay,@enabled,@source,@member,@created)
                ON CONFLICT (CalendarEventId) DO NOTHING
                """, ct, ("id", item.Id), ("loop", item.LoopId), ("summary", item.Summary),
                ("time", item.TimeLabel), ("date", item.Date), ("end", item.EndDate),
                ("allDay", item.IsAllDay), ("enabled", item.IsEnabled), ("source", item.Source),
                ("member", item.MemberId), ("created", item.Created));
        foreach (var item in s.GreetingPresences ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO GreetingPresences (GreetingPresenceId,AccountId,LoopId,PersonId,SpeakerId,PreferredName,
                    LastSeenUtc,LastGreetedUtc,LastGreetingRoute,LastGreetingIntent,CreatedUtc,UpdatedUtc)
                VALUES (@id,@account,@loop,@person,@speaker,@preferred,@seen,@greeted,@route,@intent,@created,@updated)
                ON CONFLICT (GreetingPresenceId) DO NOTHING
                """, ct, ("id", item.Id), ("account", item.AccountId), ("loop", item.LoopId),
                ("person", item.PersonId), ("speaker", item.SpeakerId), ("preferred", item.PreferredName),
                ("seen", item.LastSeenUtc), ("greeted", item.LastGreetedUtc),
                ("route", item.LastGreetingRoute), ("intent", item.LastGreetingIntent),
                ("created", item.CreatedUtc), ("updated", item.UpdatedUtc));
    }

    private async Task ImportSecretsAndTokensAsync(NpgsqlConnection c, NpgsqlTransaction tx,
        LegacyCloudStateSnapshot s, CancellationToken ct)
    {
        foreach (var key in s.SymmetricKeys ?? [])
            await ExecuteAsync(c, tx, """
                INSERT INTO LoopSymmetricKeys (LoopId,EncryptedKey,WrappingKeyId,Algorithm)
                VALUES (@loop,@key,@keyId,'AES-256-GCM')
                ON CONFLICT (LoopId) DO UPDATE SET EncryptedKey=EXCLUDED.EncryptedKey,
                    WrappingKeyId=EXCLUDED.WrappingKeyId,Algorithm=EXCLUDED.Algorithm
                """, ct, ("loop", key.Key), ("key", Protect(key.Value)),
                ("keyId", _secretProtector.KeyId));

        foreach (var session in s.IssuedTokenSessions())
        {
            var token = session.Token!;
            var kind = token.StartsWith("hub-", StringComparison.OrdinalIgnoreCase) ? "hub" : "robot";
            var registeredDeviceId = session.TryGetRegisteredDeviceId();
            var knownDeviceId = s.AllDevices().Any(device =>
                device.DeviceId.Equals(session.DeviceId, StringComparison.OrdinalIgnoreCase))
                ? session.DeviceId
                : null;
            var durableDeviceId = registeredDeviceId ?? knownDeviceId;
            var expires = DateTimeOffset.UtcNow.AddYears(100);
            await ExecuteAsync(c, tx, """
                INSERT INTO CloudAuthTokens (TokenHash,TokenKind,TokenHint,AccountId,DeviceId,IssuedUtc,ExpiresUtc,Metadata)
                VALUES (@hash,@kind,@hint,@account,@device,@issued,@expires,@metadata::jsonb)
                ON CONFLICT (TokenHash) DO NOTHING
                """, ct, ("hash", Sha256(token)), ("kind", kind),
                ("hint", token.Length <= 12 ? token : token[..8]), ("account", session.AccountId),
                ("device", durableDeviceId), ("issued", session.CreatedUtc), ("expires", expires),
                ("metadata", JsonSerializer.Serialize(session.DurableTokenMetadata())));

            if (registeredDeviceId is { } inventoryDeviceId &&
                !string.IsNullOrWhiteSpace(session.DeviceId))
                await ExecuteAsync(c, tx, """
                    INSERT INTO RobotIdentityLinks (ObservedDeviceId,InventoryDeviceId,ClaimSource,ClaimedUtc,Audit)
                    VALUES (@observed,@inventory,'legacy-cloud-state-import',@claimed,@audit::jsonb)
                    ON CONFLICT (ObservedDeviceId) DO UPDATE SET InventoryDeviceId=EXCLUDED.InventoryDeviceId,
                        ClaimSource=EXCLUDED.ClaimSource,UpdatedUtc=NOW(),RevokedUtc=NULL
                    """, ct, ("observed", session.DeviceId), ("inventory", inventoryDeviceId),
                    ("claimed", session.CreatedUtc),
                    ("audit", JsonSerializer.Serialize(
                        BuildLegacyIdentityLinkAudit(inventoryDeviceId, session.CreatedUtc))));
        }
    }

    private byte[] Protect(string plaintext)
    {
        return _secretProtector.Protect(plaintext);
    }

    internal static async Task<ExportedLegacyBackup[]> ExportBackupPayloadsAsync(
        IReadOnlyList<BackupRecord> backups, IBackupPayloadStore? payloadStore,
        CancellationToken cancellationToken = default)
    {
        if (backups.Count == 0) return [];
        if (payloadStore is null)
            throw new InvalidOperationException(
                "The legacy snapshot contains backups. Configure an IBackupPayloadStore before importing it.");

        var exported = new List<ExportedLegacyBackup>(backups.Count);
        foreach (var backup in backups)
        {
            var payload = Encoding.UTF8.GetBytes(backup.SnapshotJson ?? string.Empty);
            var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var key = $"legacy-cloud-state/backups/{Uri.EscapeDataString(backup.BackupId)}/{sha256}.json";
            var uri = await payloadStore.StoreAsync(key, payload, sha256, cancellationToken);
            var verification = await payloadStore.LoadAsync(uri, cancellationToken)
                               ?? throw new InvalidOperationException(
                                   $"Backup payload '{backup.BackupId}' could not be read after export.");
            if (!payload.AsSpan().SequenceEqual(verification) ||
                !Sha256(Encoding.UTF8.GetString(verification)).Equals(sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Backup payload '{backup.BackupId}' failed SHA-256 verification after export.");
            exported.Add(new ExportedLegacyBackup(backup, uri, sha256, payload.LongLength));
        }
        return exported.ToArray();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            if (value is null)
                command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Unknown) { Value = DBNull.Value });
            else
                command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public interface IBackupPayloadStore
{
    Task<string> StoreAsync(string key, byte[] payload, string sha256,
        CancellationToken cancellationToken = default);
    Task<byte[]?> LoadAsync(string uri, CancellationToken cancellationToken = default);
}

public sealed class MediaContentBackupPayloadStore(IMediaContentStore mediaContentStore) : IBackupPayloadStore
{
    public async Task<string> StoreAsync(string key, byte[] payload, string sha256,
        CancellationToken cancellationToken = default)
    {
        await mediaContentStore.StoreAsync(key, "application/json", payload,
            new Dictionary<string, object?> { ["sha256"] = sha256, ["kind"] = "legacy-cloud-state-backup" },
            cancellationToken);
        return key;
    }

    public async Task<byte[]?> LoadAsync(string uri, CancellationToken cancellationToken = default) =>
        (await mediaContentStore.LoadAsync(uri, cancellationToken))?.Content;
}

public sealed class DirectoryBackupPayloadStore
    : IBackupPayloadStore
{
    private readonly string _rootDirectory;

    public DirectoryBackupPayloadStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public async Task<string> StoreAsync(string key, byte[] payload, string sha256,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveKey(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, payload, cancellationToken);
        return new Uri(path).AbsoluteUri;
    }

    public async Task<byte[]?> LoadAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        var path = Path.GetFullPath(parsed.LocalPath);
        if (!IsWithinRoot(path) || !File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private string ResolveKey(string key)
    {
        var path = Path.GetFullPath(Path.Combine(_rootDirectory,
            key.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithinRoot(path)) throw new InvalidOperationException("Backup payload key escapes its storage root.");
        return path;
    }

    private bool IsWithinRoot(string path) =>
        path.StartsWith(_rootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

internal sealed record ExportedLegacyBackup(BackupRecord Source, string Uri, string Sha256, long Length);

public sealed record CloudStateSnapshotImportResult(
    string ImportName,
    string SourceSnapshotName,
    string SourceSha256,
    bool AlreadyImported,
    IReadOnlyDictionary<string, int> ImportedCounts);

internal sealed class LegacyCloudStateSnapshot
{
    public string? SchemaVersion { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset? LastLoadedUtc { get; init; }
    public DateTimeOffset? LastSavedUtc { get; init; }
    public AccountProfile? Account { get; init; }
    public DeviceRegistration? Robot { get; init; }
    public RobotProfile? RobotProfile { get; init; }
    public DeviceRegistration[]? Devices { get; init; }
    public RobotCredentialBinding[]? RobotCredentialBindings { get; init; }
    public LegacyCloudSession[]? Sessions { get; init; }
    public Dictionary<string, string>? SymmetricKeys { get; init; }
    public KeyRequestRecord[]? KeyRequests { get; init; }
    public UpdateManifest[]? Updates { get; init; }
    public MediaRecord[]? Media { get; init; }
    public BackupRecord[]? Backups { get; init; }
    public CommuteProfileRecord[]? CommuteProfiles { get; init; }
    public CalendarEventRecord[]? CalendarEvents { get; init; }
    public GreetingPresenceRecord[]? GreetingPresences { get; init; }
    public LoopRecord[]? Loops { get; init; }
    public HolidayRecord[]? Holidays { get; init; }
    public LoopMemberRecord[]? LoopMembers { get; init; }
    public PersonRecord[]? People { get; init; }
    public UserRecord[]? Users { get; init; }
    public RecognitionObservationRecord[]? RecognitionObservations { get; init; }
    public string[]? RevokedIdentityGraphAnchors { get; init; }
    public TrustedServerAdmissionRecord[]? TrustedServerAdmissions { get; init; }
    public TrustedServerRecord[]? TrustedServers { get; init; }

    public DeviceRegistration[] AllDevices() => (Devices ?? [])
        .Append(Robot)
        .Where(device => device is not null && !string.IsNullOrWhiteSpace(device.DeviceId))
        .Cast<DeviceRegistration>()
        .DistinctBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public LegacyCloudSession[] IssuedTokenSessions() => (Sessions ?? [])
        .Where(session => session.Token is not null &&
                          (session.Token.StartsWith("token-", StringComparison.OrdinalIgnoreCase) ||
                           session.Token.StartsWith("hub-", StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    public Dictionary<string, int> GetDurableFamilyCounts() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["accounts"] = Account is null ? 0 : 1,
        ["devices"] = AllDevices().Length,
        ["robotProfiles"] = RobotProfile is null ? 0 : 1,
        ["robotCredentialBindings"] = RobotCredentialBindings?.Length ?? 0,
        ["issuedTokens"] = IssuedTokenSessions().Length,
        ["robotIdentityLinks"] = IssuedTokenSessions().Count(session =>
            !string.IsNullOrWhiteSpace(session.DeviceId) &&
            !string.IsNullOrWhiteSpace(session.TryGetRegisteredDeviceId())),
        ["symmetricKeys"] = SymmetricKeys?.Count ?? 0,
        ["keyRequests"] = KeyRequests?.Length ?? 0,
        ["updates"] = Updates?.Length ?? 0,
        ["media"] = Media?.Length ?? 0,
        ["backups"] = Backups?.Length ?? 0,
        ["commuteProfiles"] = CommuteProfiles?.Length ?? 0,
        ["calendarEvents"] = CalendarEvents?.Length ?? 0,
        ["greetingPresences"] = GreetingPresences?.Length ?? 0,
        ["loops"] = Loops?.Length ?? 0,
        ["holidays"] = Holidays?.Length ?? 0,
        ["loopMembers"] = LoopMembers?.Length ?? 0,
        ["people"] = People?.Length ?? 0,
        ["users"] = Users?.Length ?? 0,
        ["recognitionObservations"] = RecognitionObservations?.Length ?? 0,
        ["revokedIdentityGraphAnchors"] = RevokedIdentityGraphAnchors?.Length ?? 0,
        ["trustedServerAdmissions"] = TrustedServerAdmissions?.Length ?? 0,
        ["trustedServers"] = TrustedServers?.Length ?? 0
    };
}

internal sealed class LegacyCloudSession
{
    public string SessionId { get; init; } = string.Empty;
    public string Kind { get; init; } = "http";
    public string? AccountId { get; init; }
    public string? DeviceId { get; init; }
    public string? Token { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public Dictionary<string, object?> Metadata { get; init; } = [];

    public string? TryGetRegisteredDeviceId()
    {
        if (!Metadata.TryGetValue("registeredDeviceId", out var value) || value is null) return null;
        return value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : value.ToString();
    }

    public Dictionary<string, object?> DurableTokenMetadata()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "accountId", "loopId", "registeredDeviceId", "registeredRobotId" })
            if (Metadata.TryGetValue(key, out var value)) result[key] = value;
        return result;
    }
}
