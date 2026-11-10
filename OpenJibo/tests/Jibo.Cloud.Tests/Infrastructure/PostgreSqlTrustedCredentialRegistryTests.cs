using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Tests.Protocol;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed partial class PostgreSqlCloudStateFacadeIntegrationTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task Trusted_registry_restart_removes_or_rotates_old_tokens_without_affecting_other_credentials()
    {
        await using var database = await CloudStateTestDatabase.CreateAsync();
        await using var source = new PostgreSqlCloudStateDataSource(database.ConnectionString, 2);
        using var key = RuntimePairingCredentialEvidenceTests.Key();
        var a = TrustedProtocolCredentialRegistryTests.A; var b = TrustedProtocolCredentialRegistryTests.B;
        foreach (var mode in new[] { "remove", "secret", "id" })
        {
            var before = TrustedProtocolCredentialRegistryTests.Registry(key, a, b);
            var first = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector(), trustedCredentials: before);
            first.UpsertDevice(new DeviceRegistration { DeviceId = "synthetic-device-A", RobotId = "synthetic-robot-A" });
            first.UpsertDevice(new DeviceRegistration { DeviceId = "synthetic-device-B", RobotId = "synthetic-robot-B" });
            var verifier = new AwsSigV4RequestVerifier(first, trustedCredentials: before);
            var verifiedA = verifier.Verify(TrustedProtocolCredentialRegistryTests.Sign(a));
            var verifiedB = verifier.Verify(TrustedProtocolCredentialRegistryTests.Sign(b));
            var bindingA = HubTokenCredentialBinding.FromVerification(verifiedA)!;
            var bindingB = HubTokenCredentialBinding.FromVerification(verifiedB)!;
            var evidenceA = verifiedA.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
            var evidenceB = verifiedB.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
            var aHub = first.IssueHubToken("synthetic-device-A", false, bindingA, evidenceA);
            var aRobot = first.IssueRobotTokenWithCredentialEvidence("synthetic-device-A", bindingA, evidenceA);
            var bHub = first.IssueHubToken("synthetic-device-B", false, bindingB, evidenceB);
            Assert.NotNull(first.FindIssuedToken(aHub));
            var replacement = mode == "id" ? a with { AccessKeyId = "synthetic-new-id-A" }
                : a with { SecretAccessKey = "rotated-synthetic-secret-A-0003" };
            var after = mode == "remove" ? TrustedProtocolCredentialRegistryTests.Registry(key, b)
                : TrustedProtocolCredentialRegistryTests.Registry(key, replacement, b);
            var restarted = new PostgreSqlCloudStateStore(source, new PlaintextTestProtector(), trustedCredentials: after);
            Assert.Null(restarted.FindIssuedToken(aHub)); Assert.Null(restarted.FindIssuedToken(aRobot));
            Assert.NotNull(restarted.FindIssuedToken(bHub));
            Assert.False(await restarted.IsHubTokenCredentialBindingCurrentAsync(aHub, bindingA));
            Assert.Null(await restarted.ReadHubPairingCredentialEvidenceAsync(aHub, bindingA, key));
            Assert.Null(await restarted.ReadRuntimeCredentialEvidenceAsync(aRobot, bindingA, key, "synthetic-device-A"));
            Assert.Equal(evidenceB, await restarted.ReadHubPairingCredentialEvidenceAsync(bHub, bindingB, key));
            Assert.Equal(evidenceB, await restarted.ReadRuntimeCredentialEvidenceAsync(bHub, bindingB, key, "synthetic-device-B"));
            Assert.Null(await restarted.ReadRuntimeCredentialEvidenceAsync(bHub, bindingB, key, "synthetic-device-A"));
            if (mode != "remove")
            {
                var fresh = new AwsSigV4RequestVerifier(restarted, trustedCredentials: after)
                    .Verify(TrustedProtocolCredentialRegistryTests.Sign(replacement));
                Assert.True(fresh.CredentialAuthenticated);
                var token = restarted.IssueHubToken("synthetic-device-A", false,
                    HubTokenCredentialBinding.FromVerification(fresh), fresh.VerifiedProof!.CreatePairingCredentialEvidence(key));
                Assert.NotNull(restarted.FindIssuedToken(token)); Assert.Null(restarted.FindIssuedToken(aHub));
            }
            Assert.Null(restarted.FindIssuedToken(restarted.IssueRobotToken("synthetic-device-A")));
        }
    }
}
