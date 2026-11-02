using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class RuntimePairingCredentialEvidenceTests
{
    internal static RuntimePairingCredentialEvidenceKey Key(byte value = 42) => RuntimePairingCredentialEvidenceKey.Create(
        new() { Enabled = true, HmacKey = Convert.ToBase64String(Enumerable.Repeat(value,32).ToArray()).TrimEnd('=').Replace('+','-').Replace('/','_') });
    internal static AwsSigV4Verification Verify(ICloudStateStore store, string device = "untrusted-body-id", bool weak = false)
    {
        var now = DateTimeOffset.UtcNow;
        var body = "{\"deviceId\":\"" + device + "\"}";
        var envelope = AwsSigV4RequestVerifierTests.Sign(store.GetAccount(), body, now,
            weak ? ["host", "x-amz-content-sha256", "x-amz-date"] : ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            advertisedPayloadHash: weak ? Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant() : null,
            region: "api", target: "Account_20151111.CreateHubToken");
        return new AwsSigV4RequestVerifier(store).Verify(envelope);
    }

    [Fact]
    public void CredentialBindingMatchesIndependentRegistrationVector()
    {
        var store = new Moq.Mock<ICloudStateStore>();
        store.Setup(s => s.GetAccount()).Returns(new AccountProfile { AccessKeyId = "id", SecretAccessKey = "secret" });
        using var key = Key();
        Assert.Equal("84OCcqfcja0Q53vcwRe5hy3HiPwofZ2Rfp5yX1MUnEM",Verify(store.Object).VerifiedProof!.CreatePairingCredentialEvidence(key)!.Binding);
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void RegistrationRequiresDedicatedOptInAndKey(bool enabled,bool dedicated)
    {
        var text = Convert.ToBase64String(new byte[32]).TrimEnd('=');
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["OpenJibo:Testing:SkipSttDependencyValidation"] = "true",
            ["OpenJibo:ManagedPairingCredentialEvidence:Enabled"] = enabled.ToString(),
            ["OpenJibo:ManagedPairingCredentialEvidence:HmacKey"] = dedicated ? text : null,
            ["OpenJibo:Security:HubChannelLeases:HmacKey"] = text,
            ["OpenJibo:Security:HubTokenReplayAdmission:HmacKey"] = text
        }).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddOpenJiboCloud(config);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(enabled && dedicated,provider.GetRequiredService<RuntimePairingCredentialEvidenceKey>().IsReady);
        Assert.Null(provider.GetService<RuntimePairingCredentialEvidenceOptions>());
    }

    [Fact]
    public void EvidenceUsesWholeVerifiedCredentialAndEnvironment_NotBodyTimestampOrShortFingerprint()
    {
        var store = new InMemoryCloudStateStore(); using var key = Key(); using var other = Key(43);
        var first = Verify(store).VerifiedProof!.CreatePairingCredentialEvidence(key)!;
        var changedBody = Verify(store, "different-device", weak: true);
        Assert.False(changedBody.OperationAuthenticated);
        Assert.Equal(first, changedBody.VerifiedProof!.CreatePairingCredentialEvidence(key));
        Assert.NotEqual(first, Verify(store).VerifiedProof!.CreatePairingCredentialEvidence(other));
        Assert.Equal(43, first.Binding.Length);
        Assert.True(key.MatchesCurrentCredential(first, store.GetAccount().AccessKeyId, store.GetAccount().SecretAccessKey));
        Assert.False(key.MatchesCurrentCredential(first, store.GetAccount().AccessKeyId, "changed-secret"));
        Assert.False(key.MatchesCurrentCredential(first, "changed-id", store.GetAccount().SecretAccessKey));
        Assert.DoesNotContain(first.Binding, first.ToString());
        Assert.DoesNotContain(store.GetAccount().SecretAccessKey, Verify(store).ToString());
        var roundtrip = new Dictionary<string,object?>(); first.WriteTo(roundtrip);
        Assert.Equal(first, RuntimePairingCredentialEvidence.Read(JsonSerializer.Deserialize<Dictionary<string,object?>>(JsonSerializer.Serialize(roundtrip))!));
        var fabricated = new AwsSigV4VerifiedProof("id","20261102","api","jibo",DateTimeOffset.UtcNow,new byte[32]);
        Assert.Null(fabricated.CreatePairingCredentialEvidence(key));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("padded")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("noncanonical")]
    public void InvalidOrDisabledKeyNeverCreatesEvidence(string scenario)
    {
        var text = scenario switch { "missing" => null, "padded" => Convert.ToBase64String(new byte[32]),
            "short" => Convert.ToBase64String(new byte[31]).TrimEnd('='), "long" => Convert.ToBase64String(new byte[65]).TrimEnd('='),
            "noncanonical" => "!", _ => Convert.ToBase64String(new byte[32]).TrimEnd('=') };
        using var key = RuntimePairingCredentialEvidenceKey.Create(new() { Enabled = scenario != "disabled", HmacKey = text });
        Assert.False(key.IsReady); Assert.Null(Verify(new InMemoryCloudStateStore()).VerifiedProof!.CreatePairingCredentialEvidence(key));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("unknown-version")]
    [InlineData("padded")]
    [InlineData("numeric")]
    [InlineData("duplicate-case")]
    [InlineData("short")]
    public void MalformedMetadataCannotSupplyEvidence(string scenario)
    {
        using var key = Key(); var evidence = Verify(new InMemoryCloudStateStore()).VerifiedProof!.CreatePairingCredentialEvidence(key)!;
        var metadata = new Dictionary<string, object?>(); evidence.WriteTo(metadata);
        switch (scenario)
        {
            case "partial": metadata.Remove(RuntimePairingCredentialEvidence.BindingKey); break;
            case "unknown-version": metadata[RuntimePairingCredentialEvidence.VersionKey] = "2"; break;
            case "padded": metadata[RuntimePairingCredentialEvidence.BindingKey] = evidence.Binding + "="; break;
            case "numeric": metadata[RuntimePairingCredentialEvidence.BindingKey] = 42; break;
            case "duplicate-case": metadata[RuntimePairingCredentialEvidence.BindingKey.ToUpperInvariant()] = evidence.Binding; break;
            case "short": metadata[RuntimePairingCredentialEvidence.BindingKey] = "YQ"; break;
        }
        Assert.Null(RuntimePairingCredentialEvidence.Read(metadata));
    }

    [Fact]
    public async Task InMemoryReadRequiresOriginalEvidenceExactTokenAndCurrentCredential()
    {
        var store = new InMemoryCloudStateStore(); using var key = Key(); using var other = Key(44);
        var verified = Verify(store); var binding = HubTokenCredentialBinding.FromVerification(verified)!;
        var evidence = verified.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
        var token = store.IssueHubToken("untrusted",false,binding,evidence);
        Assert.Equal(evidence,await store.ReadHubPairingCredentialEvidenceAsync(token,binding,key));
        Assert.Null(await store.ReadHubPairingCredentialEvidenceAsync(token.ToUpperInvariant(),binding,key));
        Assert.Null(await store.ReadHubPairingCredentialEvidenceAsync(" "+token,binding,key));
        Assert.Null(await store.ReadHubPairingCredentialEvidenceAsync(token,binding,other));
        Assert.Null(await store.ReadHubPairingCredentialEvidenceAsync(token,new(binding.CredentialFingerprint,binding.SignedAt,true),key));
        var old = store.IssueHubToken("old",false,binding);
        Assert.Null(await store.ReadHubPairingCredentialEvidenceAsync(old,binding,key));
        var stored = store.FindIssuedToken(token)!; stored.Metadata.Remove(RuntimePairingCredentialEvidence.BindingKey);
        Assert.Null(await store.ReadHubPairingCredentialEvidenceAsync(token,binding,key));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadHubPairingCredentialEvidenceAsync(token,binding,key,cancelled.Token));
        key.Dispose(); Assert.False(key.IsReady);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTokenIssuanceCapturesOnlyOptedInVerifiedEvidence(bool enabled)
    {
        var store = new InMemoryCloudStateStore(); using var key = Key();
        var handler = new CloudAuthProtocolHandler(store,pairingCredentialKey: enabled ? key : null);
        var now = DateTimeOffset.UtcNow;
        var request = AwsSigV4RequestVerifierTests.Sign(store.GetAccount(),"{}",now,
            ["host","x-amz-content-sha256","x-amz-date"],region:"api",target:"Account_20151111.CreateHubToken");
        var result = await handler.HandleAccountAsync("CreateHubToken",request);
        var json = JsonDocument.Parse(result.BodyText).RootElement; var token = json.GetProperty("token").GetString()!;
        var issued = store.FindIssuedToken(token)!;
        var evidence = RuntimePairingCredentialEvidence.Read(issued.Metadata);
        Assert.Equal(enabled,evidence is not null);
        if(enabled) Assert.True(key.MatchesCurrentCredential(evidence!,store.GetAccount().AccessKeyId,store.GetAccount().SecretAccessKey));
        var rejected = await handler.HandleAccountAsync("CreateHubToken",new ProtocolEnvelope());
        var unsigned = JsonDocument.Parse(rejected.BodyText).RootElement.GetProperty("token").GetString()!;
        Assert.Null(RuntimePairingCredentialEvidence.Read(store.FindIssuedToken(unsigned)!.Metadata));
    }
}
