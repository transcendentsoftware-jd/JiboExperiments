using System.Text.Json;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class HubTokenCredentialBindingTests
{
    [Fact]
    public void Metadata_RoundTripsThroughJsonWithoutCredentialMaterial()
    {
        var binding = new HubTokenCredentialBinding(
            "0123456789abcdef",
            new DateTimeOffset(2026, 9, 21, 5, 0, 0, TimeSpan.Zero),
            operationAuthenticated: false);
        var metadata = new Dictionary<string, object?>();
        binding.WriteTo(metadata);
        var json = JsonSerializer.Serialize(metadata);
        var rehydrated = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;

        Assert.True(HubTokenCredentialBinding.TryRead(rehydrated, out var observed));
        Assert.Equal(binding, observed);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Signature", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Metadata_PartialMalformedOrDuplicateCaseFailsClosed()
    {
        var partial = new Dictionary<string, object?>
        {
            [HubTokenCredentialBinding.VersionMetadataKey] = "1"
        };
        Assert.True(HubTokenCredentialBinding.ContainsMetadata(partial));
        Assert.False(HubTokenCredentialBinding.TryRead(partial, out _));

        var duplicateCase = new Dictionary<string, object?>
        {
            [HubTokenCredentialBinding.VersionMetadataKey] = "1",
            [HubTokenCredentialBinding.VersionMetadataKey.ToUpperInvariant()] = "1"
        };
        Assert.False(HubTokenCredentialBinding.TryRead(duplicateCase, out _));

        var nonCanonicalBoolean = new Dictionary<string, object?>();
        new HubTokenCredentialBinding(
            "0123456789abcdef",
            DateTimeOffset.UtcNow,
            operationAuthenticated: true).WriteTo(nonCanonicalBoolean);
        nonCanonicalBoolean[HubTokenCredentialBinding.OperationAuthenticatedMetadataKey] = "True";
        Assert.False(HubTokenCredentialBinding.TryRead(nonCanonicalBoolean, out _));
    }
}
