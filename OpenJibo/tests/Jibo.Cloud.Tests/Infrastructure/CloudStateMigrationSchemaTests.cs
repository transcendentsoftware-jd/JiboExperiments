namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class CloudStateMigrationSchemaTests
{
    [Fact]
    public void NormalizedPeople_UsesAccountLoopPersonCompositeIdentity()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "004_normalize_cloud_state.state.sql"));

        Assert.Contains("PRIMARY KEY (AccountId, LoopId, PersonId)", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("PersonId TEXT NOT NULL PRIMARY KEY", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudAuthTokens_AllowObservedHardwareBeforeInventoryRegistration()
    {
        var forwardMigration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "006_allow_unlinked_cloud_auth_tokens.state.sql"));

        Assert.Contains(
            "DROP CONSTRAINT IF EXISTS cloudauthtokens_deviceid_fkey",
            forwardMigration,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RobotIdentitySuggestions_AreDurableAndCaseInsensitive()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "009_create_robot_identity_suggestions.state.sql"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS RobotIdentitySuggestions", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOWER(ObservedDeviceId), LOWER(ProposedRobotId)", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DismissedUtc IS NULL", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserDevices_EnforceOneCurrentOwnerPerDevice()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "008_create_user_devices.state.sql"));

        Assert.Contains("CREATE TABLE IF NOT EXISTS UserDevices", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS UX_UserDevices_Device", migration,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Devices_IndexVerifiedSerialNumbersCaseInsensitively()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "010_index_verified_serial_number.state.sql"));

        Assert.Contains("CREATE INDEX IF NOT EXISTS IX_Devices_VerifiedSerialNumber_CI", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON Devices (LOWER(VerifiedSerialNumber))", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE VerifiedSerialNumber IS NOT NULL", migration,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeUsageOutbox_IsPrivacyBoundImmutableAndDeliverySeparated()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "011_create_runtime_usage_outbox.state.sql"));

        Assert.Contains("RuntimeUsageRobotBindings", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SourceSubjectHmac BYTEA", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OCTET_LENGTH(SourceSubjectHmac) = 32", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RuntimeUsageDailyAccumulators", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IsIncomplete BOOLEAN NOT NULL DEFAULT FALSE", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RuntimeUsageAppliedEvents", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RecordRuntimeUsageEvent", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ScheduleRuntimeUsageSnapshot", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openjibo-runtime-usage.v1", migration, StringComparison.Ordinal);
        Assert.Contains("RuntimeUsageOutboxMessages", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WebSocketInboundBytes BIGINT", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CanonicalPayload", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RuntimeUsageOutboxDelivery", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BEFORE UPDATE OR DELETE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OLD.IsIncomplete AND NOT NEW.IsIncomplete", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bindings are append-only and may only be revoked once", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("grants no application, binding-administrator, or collector role", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE EXECUTE ON FUNCTION", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeviceId", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Serial", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transcript", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeUsageDeliveryBoundary_IsOwnerOnlyAndHeadOfLineSafe()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "012_runtime_usage_delivery_boundary.state.sql"));

        Assert.Contains("CREATE OR REPLACE FUNCTION ClaimRuntimeUsageOutbox", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE OR REPLACE FUNCTION AcknowledgeRuntimeUsageOutbox", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CREATE OR REPLACE FUNCTION QuarantineRuntimeUsageOutbox", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FOR UPDATE OF delivery SKIP LOCKED", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("earlier_delivery.DeliveryState <> 'acknowledged'", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LeaseExpiresUtc <= v_now", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_message_id IS NULL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_receipt_hash IS NULL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_quarantine_category IS NULL", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE ALL ON FUNCTION ClaimRuntimeUsageOutbox", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE ALL ON FUNCTION AcknowledgeRuntimeUsageOutbox", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE ALL ON FUNCTION QuarantineRuntimeUsageOutbox", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT EXECUTE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECURITY DEFINER", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeviceId", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transcript", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AudioContent", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SigV4ReplayObservation_IsShortLivedOpaqueAndOwnerOnly()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "013_create_sigv4_replay_observations.state.sql"));

        Assert.Contains("AwsSigV4ReplayObservations", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OCTET_LENGTH(ReplayDigest) = 32", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INTERVAL '15 minutes'", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ON CONFLICT (ReplayDigest) DO UPDATE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT 256", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE ALL ON FUNCTION ObserveAwsSigV4Replay", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT EXECUTE", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccessKeyId", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeviceId", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SigV4ReplayObservation_HardenedFunctionUsesRestrictedSearchPath()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "014_harden_sigv4_replay_observer.state.sql"));

        Assert.Contains("SECURITY DEFINER", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET search_path = pg_catalog, pg_temp", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public.AwsSigV4ReplayObservations", migration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REVOKE ALL ON FUNCTION public.ObserveAwsSigV4Replay", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT EXECUTE", migration, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HubTokenCredentialEpoch_IsPositiveAndDoesNotPersistCredentialMaterial()
    {
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "PostgreSql",
            "015_bind_hub_tokens_to_credential_epoch.state.sql"));

        Assert.Contains("CredentialEpoch BIGINT NOT NULL DEFAULT 1", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CHECK (CredentialEpoch > 0)", migration,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccessKeyId", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretAccessKey", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", migration, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Signature", migration, StringComparison.OrdinalIgnoreCase);
    }
}
