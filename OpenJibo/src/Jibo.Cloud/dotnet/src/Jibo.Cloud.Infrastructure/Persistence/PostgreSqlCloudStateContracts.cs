using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Infrastructure.Persistence;

internal interface ICloudStateMetadataRepository
{
    Task<CloudStateMetadataRecord> GetAsync(CancellationToken cancellationToken = default);
    Task<bool> HasLegacySnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}

internal sealed record CloudStateMetadataRecord(int SchemaVersion, long Revision, DateTimeOffset UpdatedUtc);

public interface ICloudAccountRepository
{
    Task<AccountProfile?> GetByIdAsync(string accountId, CancellationToken cancellationToken = default);
    Task<AccountProfile?> GetDefaultAsync(CancellationToken cancellationToken = default);
    Task<AccountProfile> UpsertAsync(AccountProfile account, bool? isDefault = null,
        CancellationToken cancellationToken = default);
}

public interface ICloudDeviceRepository
{
    Task<DeviceRegistration?> GetByDeviceIdAsync(string deviceId,
        CancellationToken cancellationToken = default);
    Task<DeviceRegistration?> FindByFriendlyIdAsync(string friendlyId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceRegistration>> FindVisibleIdentityCandidatesAsync(string accountId, string identity,
        CancellationToken cancellationToken = default);
    Task<DeviceRegistration?> GetDefaultAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceRegistration>> ListForAccountAsync(string accountId, bool includeArchived = false,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceRegistration>> ListAllAsync(bool includeArchived = true,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListAccountIdsAsync(string deviceId,
        CancellationToken cancellationToken = default);
    Task<DeviceRegistration> UpsertAsync(DeviceRegistration device, string? accountId = null,
        bool? isDefault = null, CancellationToken cancellationToken = default);
    Task<DeviceRegistration> UpdateFriendlyNameAsync(string deviceId, string friendlyName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Friendly-name updates are not supported by this device repository.");
    Task<RobotCredentialBinding?> GetCredentialBindingAsync(string accessKeyFingerprint,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RobotCredentialBinding>> ListCredentialBindingsAsync(string deviceId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RobotCredentialBinding>> ListCredentialBindingsForAccountAsync(string accountId,
        CancellationToken cancellationToken = default);
    Task<RobotCredentialBinding> BindCredentialAsync(string deviceId, string accessKeyFingerprint,
        string claimSource, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RobotCredentialBinding>> SwapCredentialBindingsAsync(string firstAccessKeyFingerprint,
        string secondAccessKeyFingerprint, string claimSource, CancellationToken cancellationToken = default);
    Task<int> MoveCredentialBindingsAsync(string sourceDeviceId, string targetDeviceId, string claimSource,
        CancellationToken cancellationToken = default);
    Task<int> MergeForAdministrationAsync(string sourceDeviceId, string targetDeviceId,
        IReadOnlyList<string> expectedCredentialFingerprints, string claimSource,
        CancellationToken cancellationToken = default);
    Task<DeviceRegistration?> FindByCredentialFingerprintAsync(string accessKeyFingerprint,
        CancellationToken cancellationToken = default);
}

public interface IUserDeviceLinkRepository
{
    Task<UserDeviceLink> LinkAsync(string userId, string deviceId, string claimSource,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListDeviceIdsForUserAsync(string userId,
        CancellationToken cancellationToken = default);
    Task<UserDeviceLink?> FindByDeviceAsync(string deviceId,
        CancellationToken cancellationToken = default);
}

public interface ICloudAuthTokenRepository
{
    Task<CloudAuthTokenRecord> IssueAsync(string token, string tokenKind, string? accountId, string? deviceId,
        DateTimeOffset expiresUtc, IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default);
    Task<CloudAuthTokenRecord?> FindValidAsync(string token, DateTimeOffset? now = null,
        CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(string token, CancellationToken cancellationToken = default);
    Task<int> RevokeForAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task<int> RevokeForDeviceAsync(string deviceId, string tokenKind,
        CancellationToken cancellationToken = default);
}

public interface IRobotIdentityLinkRepository
{
    Task<RobotIdentityLinkRecord?> FindAsync(string observedDeviceId,
        CancellationToken cancellationToken = default);
    Task<RobotIdentityLinkRecord> UpsertAsync(string observedDeviceId, string inventoryDeviceId,
        string claimSource, IReadOnlyList<RobotIdentityLinkAuditEntry>? audit = null,
        CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(string observedDeviceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RobotIdentityLinkRecord>> ListForAccountAsync(string accountId,
        CancellationToken cancellationToken = default);
}

public interface IRobotProfileRepository
{
    Task<RobotProfile?> GetAsync(string robotId, CancellationToken cancellationToken = default);
    Task<RobotProfile> UpsertAsync(RobotProfile profile, string? deviceId,
        CancellationToken cancellationToken = default);
}

public sealed record CloudAuthTokenRecord(
    string TokenKind,
    string? AccountId,
    string? DeviceId,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? RevokedUtc,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record RobotIdentityLinkRecord(
    string ObservedDeviceId,
    string InventoryDeviceId,
    string ClaimSource,
    DateTimeOffset ClaimedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? RevokedUtc,
    IReadOnlyList<RobotIdentityLinkAuditEntry> Audit);

public sealed record RobotIdentityLinkAuditEntry(
    string Action,
    string? PreviousInventoryDeviceId,
    string? InventoryDeviceId,
    string Source,
    DateTimeOffset OccurredUtc);

public interface ICloudStateSecretProtector
{
    string KeyId { get; }
    byte[] Protect(string plaintext);
    string Unprotect(byte[] ciphertext);
}
