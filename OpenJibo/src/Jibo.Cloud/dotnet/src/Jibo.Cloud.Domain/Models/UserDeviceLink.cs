using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Jibo.Cloud.Domain.Models;

public sealed record UserDeviceLink(
    string UserId,
    string DeviceId,
    string ClaimSource,
    DateTimeOffset LinkedUtc,
    string? OwnershipEpoch = null,
    string? LegacyIntegrationEpoch = null)
{
    // Old snapshots and database rows get a stable identity without a write on read.
    [JsonIgnore]
    public string EffectiveOwnershipEpoch => OwnershipEpoch ?? Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{UserId.ToLowerInvariant()}\n{DeviceId.ToLowerInvariant()}\n{LinkedUtc.ToUnixTimeMilliseconds()}")));

    [JsonIgnore]
    public string EffectiveLegacyIntegrationEpoch => LegacyIntegrationEpoch ?? EffectiveOwnershipEpoch;
}
