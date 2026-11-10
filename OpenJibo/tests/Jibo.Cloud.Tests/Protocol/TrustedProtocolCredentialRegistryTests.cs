using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.DependencyInjection;
using Jibo.Cloud.Infrastructure.Holidays;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class TrustedProtocolCredentialRegistryTests
{
    internal static readonly AwsSigV4Credential A = new("synthetic-credential-A", "synthetic-secret-for-A-0001");
    internal static readonly AwsSigV4Credential B = new("synthetic-credential-B", "synthetic-secret-for-B-0002");
    internal static TrustedProtocolCredentialRegistry Registry(RuntimePairingCredentialEvidenceKey key,
        params AwsSigV4Credential[] credentials) => new(Options(credentials), key);
    private static TrustedProtocolCredentialRegistryOptions Options(params AwsSigV4Credential[] credentials) => new()
    {
        Enabled = true,
        Credentials = credentials.Select(c => (TrustedProtocolCredentialEntry?)new() { AccessKeyId = c.AccessKeyId, SecretAccessKey = c.SecretAccessKey }).ToList()
    };
    internal static ProtocolEnvelope Sign(AwsSigV4Credential credential, string device = "synthetic-device-A",
        bool hub = true) => AwsSigV4RequestVerifierTests.Sign(
        new AccountProfile { AccessKeyId = credential.AccessKeyId, SecretAccessKey = credential.SecretAccessKey },
        JsonSerializer.Serialize(new { deviceId = device }), DateTimeOffset.UtcNow,
        ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"], region: hub ? "api" : "us-east-1",
        target: hub ? "Account_20151111.CreateHubToken" : "Notification_20150505.NewRobotToken");
    internal static InMemoryCloudStateStore Store(TrustedProtocolCredentialRegistry registry, string? path = null) =>
        new(new JsonFileSnapshotStore(path, new JsonSerializerOptions()), new NagerDateHolidayCalendarProvider(), trustedCredentials: registry);

    [Theory]
    [InlineData("empty")][InlineData("null-list")][InlineData("null-entry")]
    [InlineData("duplicate-id")][InlineData("shared-secret")][InlineData("short-secret")]
    [InlineData("long-secret")][InlineData("control-secret")][InlineData("space-id")]
    [InlineData("slash-id")][InlineData("comma-id")][InlineData("long-id")][InlineData("capacity")]
    [InlineData("null-id")][InlineData("null-secret")]
    public void Enabled_invalid_configuration_denies_the_entire_inventory(string scenario)
    {
        using var key = RuntimePairingCredentialEvidenceTests.Key();
        var options = Options(A, B);
        switch (scenario)
        {
            case "empty": options.Credentials = []; break;
            case "null-list": options.Credentials = null; break;
            case "null-entry": options.Credentials!.Add(null); break;
            case "duplicate-id": options.Credentials![1]!.AccessKeyId = A.AccessKeyId; break;
            case "shared-secret": options.Credentials![1]!.SecretAccessKey = A.SecretAccessKey; break;
            case "short-secret": options.Credentials![1]!.SecretAccessKey = "short"; break;
            case "long-secret": options.Credentials![1]!.SecretAccessKey = new string('x',257); break;
            case "control-secret": options.Credentials![1]!.SecretAccessKey = B.SecretAccessKey+"\n"; break;
            case "space-id": options.Credentials![1]!.AccessKeyId = " "+B.AccessKeyId; break;
            case "slash-id": options.Credentials![1]!.AccessKeyId = "invalid/id"; break;
            case "comma-id": options.Credentials![1]!.AccessKeyId = "invalid,id"; break;
            case "long-id": options.Credentials![1]!.AccessKeyId = new string('x',129); break;
            case "capacity": options.Credentials = Enumerable.Range(0,257).Select(i => (TrustedProtocolCredentialEntry?)new() { AccessKeyId=$"id-{i}",SecretAccessKey=$"synthetic-secret-{i:D4}" }).ToList(); break;
            case "null-id": options.Credentials![1]!.AccessKeyId=null; break;
            case "null-secret": options.Credentials![1]!.SecretAccessKey=null; break;
        }
        var registry = new TrustedProtocolCredentialRegistry(options,key);
        Assert.True(registry.Required); Assert.False(registry.IsReady);
        Assert.Null(registry.Resolve(A.AccessKeyId)); Assert.Null(registry.Resolve(B.AccessKeyId));
    }

    [Fact]
    public void Complete_ids_and_secrets_are_verified_without_account_or_additional_fallback()
    {
        using var key = RuntimePairingCredentialEvidenceTests.Key();
        var registry = Registry(key,A,B);
        var store = new Mock<ICloudStateStore>(MockBehavior.Strict);
        var verifier = new AwsSigV4RequestVerifier(store.Object,trustedCredentials:registry);
        foreach (var credential in new[] { A,B })
            Assert.True(verifier.Verify(Sign(credential)).CredentialAuthenticated);
        Assert.Equal(AwsSigV4VerificationOutcome.SignatureMismatch,
            verifier.Verify(Sign(A with { SecretAccessKey = B.SecretAccessKey })).Outcome);
        foreach (var id in new[] { A.AccessKeyId.ToLowerInvariant(), A.AccessKeyId+"-unknown" })
            Assert.Equal(AwsSigV4VerificationOutcome.UnknownCredential,
                verifier.Verify(Sign(A with { AccessKeyId=id }), additionalCredential:A with { AccessKeyId=id }).Outcome);
        var empty = new AwsSigV4RequestVerifier(store.Object,trustedCredentials:Registry(key));
        Assert.Equal(AwsSigV4VerificationOutcome.UnknownCredential,empty.Verify(Sign(A),additionalCredential:A).Outcome);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void Snapshot_is_immutable_and_secret_objects_are_redacted()
    {
        using var key = RuntimePairingCredentialEvidenceTests.Key();
        var options = Options(A,B); var registry = new TrustedProtocolCredentialRegistry(options,key);
        options.Credentials![0]!.SecretAccessKey = "replacement-secret-0000"; options.Credentials.Clear(); options.Enabled=false;
        Assert.True(registry.IsReady); Assert.Equal(A,registry.Resolve(A.AccessKeyId));
        foreach (var text in new[] { registry.ToString(),options.ToString(), A.ToString(),new TrustedProtocolCredentialEntry { AccessKeyId=A.AccessKeyId,SecretAccessKey=A.SecretAccessKey }.ToString() })
        { Assert.DoesNotContain(A.AccessKeyId,text); Assert.DoesNotContain(A.SecretAccessKey,text); }
    }

    [Fact]
    public void Full_evidence_and_matching_binding_are_required_and_wrong_environment_is_denied()
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); using var other=RuntimePairingCredentialEvidenceTests.Key(43);
        var registry=Registry(key,A,B); var verified=new AwsSigV4RequestVerifier(new InMemoryCloudStateStore(),trustedCredentials:registry).Verify(Sign(A));
        var metadata=new Dictionary<string,object?>(); HubTokenCredentialBinding.FromVerification(verified)!.WriteTo(metadata);
        var evidence=verified.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
        Assert.False(registry.IsCurrentBinding(metadata)); evidence.WriteTo(metadata);
        Assert.True(registry.IsCurrentBinding(metadata)); Assert.False(registry.IsCurrentBinding(metadata,other));
        var bVerified=new AwsSigV4RequestVerifier(new InMemoryCloudStateStore(),trustedCredentials:registry).Verify(Sign(B));
        HubTokenCredentialBinding.FromVerification(bVerified)!.WriteTo(metadata);
        Assert.False(registry.IsCurrentBinding(metadata));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Removal_or_rotation_on_restart_denies_old_bearers_and_preserves_other_credential(bool rotate)
    {
        var folder=Path.Combine(Path.GetTempPath(),"jibo-registry-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            using var key=RuntimePairingCredentialEvidenceTests.Key(); var before=Registry(key,A,B);
            var first=Store(before,Path.Combine(folder,"state.json"));
            var verifier=new AwsSigV4RequestVerifier(first,trustedCredentials:before);
            var verifiedA=verifier.Verify(Sign(A)); var verifiedB=verifier.Verify(Sign(B));
            var bindingA=HubTokenCredentialBinding.FromVerification(verifiedA)!; var bindingB=HubTokenCredentialBinding.FromVerification(verifiedB)!;
            var evidenceA=verifiedA.VerifiedProof!.CreatePairingCredentialEvidence(key)!; var evidenceB=verifiedB.VerifiedProof!.CreatePairingCredentialEvidence(key)!;
            var aHub=first.IssueHubToken("synthetic-device-A",false,bindingA,evidenceA);
            var aRobot=first.IssueRobotTokenWithCredentialEvidence("synthetic-device-A",bindingA,evidenceA);
            var bHub=first.IssueHubToken("synthetic-device-B",false,bindingB,evidenceB);
            Assert.NotNull(first.FindIssuedToken(aHub)); Assert.NotNull(first.FindIssuedToken(aRobot));
            var replacement=A with { SecretAccessKey="rotated-synthetic-secret-0003" };
            var after=rotate?Registry(key,replacement,B):Registry(key,B);
            var restarted=Store(after,Path.Combine(folder,"state.json"));
            Assert.Null(restarted.FindIssuedToken(aHub)); Assert.Null(restarted.FindIssuedToken(aRobot)); Assert.NotNull(restarted.FindIssuedToken(bHub));
            Assert.False(await ((ICloudStateStore)restarted).IsHubTokenCredentialBindingCurrentAsync(aHub,bindingA));
            Assert.Null(await restarted.ReadHubPairingCredentialEvidenceAsync(aHub,bindingA,key));
            Assert.Null(await restarted.ReadRuntimeCredentialEvidenceAsync(aRobot,bindingA,key,"synthetic-device-A"));
            Assert.Equal(evidenceB,await restarted.ReadRuntimeCredentialEvidenceAsync(bHub,bindingB,key,"synthetic-device-B"));
            var fresh=new AwsSigV4RequestVerifier(restarted,trustedCredentials:after);
            Assert.False(fresh.Verify(Sign(A)).CredentialAuthenticated);
            if(rotate)
            {
                var proof=fresh.Verify(Sign(replacement)); Assert.True(proof.CredentialAuthenticated);
                var newToken=restarted.IssueHubToken("synthetic-device-A",false,HubTokenCredentialBinding.FromVerification(proof),proof.VerifiedProof!.CreatePairingCredentialEvidence(key));
                Assert.NotNull(restarted.FindIssuedToken(newToken)); Assert.Null(restarted.FindIssuedToken(aHub));
            }
        }
        finally { Directory.Delete(folder,true); }
    }

    [Theory][InlineData(true)][InlineData(false)]
    public async Task Token_issuance_requires_trusted_signature_before_state_changes(bool hub)
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); var registry=Registry(key,A,B); var store=Store(registry);
        var handler=new CloudAuthProtocolHandler(store,awsSigV4Verifier:new(store,trustedCredentials:registry),pairingCredentialKey:key);
        var denied=hub?await handler.HandleAccountAsync("CreateHubToken",new ProtocolEnvelope()):handler.HandleNotification("NewRobotToken",new ProtocolEnvelope());
        Assert.Equal(401,denied.StatusCode); Assert.DoesNotContain("token",denied.BodyText);
        var accepted=hub?await handler.HandleAccountAsync("CreateHubToken",Sign(A)):handler.HandleNotification("NewRobotToken",Sign(A,hub:false));
        Assert.Equal(200,accepted.StatusCode);
        var token=JsonDocument.Parse(accepted.BodyText).RootElement.GetProperty("token").GetString()!;
        Assert.NotNull(store.FindIssuedToken(token));
    }

    [Fact]
    public void Disabled_mode_preserves_legacy_and_enabled_mode_requires_dedicated_evidence_key()
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key(); var options=Options(A);options.Enabled=false;
        var registry=new TrustedProtocolCredentialRegistry(options,key); var store=Store(registry); var account=store.GetAccount();
        Assert.False(registry.Required);
        Assert.True(new AwsSigV4RequestVerifier(store,trustedCredentials:registry).Verify(Sign(new(account.AccessKeyId,account.SecretAccessKey))).CredentialAuthenticated);
        using var missing=RuntimePairingCredentialEvidenceKey.Create(new());
        var blocked=Registry(missing,A); Assert.True(blocked.Required);Assert.False(blocked.IsReady);
        Assert.Null(blocked.Resolve(A.AccessKeyId));
    }

    [Fact]
    public void Dependency_injection_shares_registry_with_signature_and_token_readers()
    {
        var folder=Directory.CreateTempSubdirectory("jibo-registry-di-");
        try
        {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["OpenJibo:Testing:SkipSttDependencyValidation"]="true",
            ["OpenJibo:State:Backend"]="File",
            ["OpenJibo:State:PersistencePath"]=Path.Combine(folder.FullName,"state.json"),
            ["OpenJibo:PersonalMemory:Backend"]="File",
            ["OpenJibo:PersonalMemory:PersistencePath"]=Path.Combine(folder.FullName,"memory.json"),
            ["OpenJibo:ManagedPairingCredentialEvidence:Enabled"]="true",
            ["OpenJibo:ManagedPairingCredentialEvidence:HmacKey"]=Convert.ToBase64String(Enumerable.Repeat((byte)42,32).ToArray()).TrimEnd('='),
            [TrustedProtocolCredentialRegistryOptions.SectionName+":Enabled"]="true",
            [TrustedProtocolCredentialRegistryOptions.SectionName+":Credentials:0:AccessKeyId"]=A.AccessKeyId,
            [TrustedProtocolCredentialRegistryOptions.SectionName+":Credentials:0:SecretAccessKey"]=A.SecretAccessKey
        }).Build();
        var services=new ServiceCollection();services.AddLogging();services.AddOpenJiboCloud(config);
        using var provider=services.BuildServiceProvider();
        var registry=provider.GetRequiredService<TrustedProtocolCredentialRegistry>();Assert.True(registry.IsReady);
        var verifier=provider.GetRequiredService<AwsSigV4RequestVerifier>();Assert.True(verifier.TrustedCredentialsRequired);
        Assert.True(verifier.Verify(Sign(A)).CredentialAuthenticated);
        var store=provider.GetRequiredService<ICloudStateStore>(); Assert.Null(store.FindIssuedToken(store.IssueRobotToken("unbound-device")));
        Assert.Null(provider.GetService<TrustedProtocolCredentialRegistryOptions>());
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void Maximum_inventory_is_accepted_and_disposed_key_denies_it()
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key();
        var credentials=Enumerable.Range(0,256).Select(i=>new AwsSigV4Credential($"synthetic-id-{i}",$"synthetic-secret-{i:D4}")).ToArray();
        var registry=Registry(key,credentials);Assert.True(registry.IsReady);
        Assert.Equal(credentials[^1],registry.Resolve(credentials[^1].AccessKeyId));
        key.Dispose(); Assert.False(registry.IsReady);Assert.Null(registry.Resolve(credentials[0].AccessKeyId));
    }

    [Theory][InlineData("missing-key")][InlineData("empty")][InlineData("bad-enabled")]
    public void Normal_host_registration_refuses_invalid_inventory_with_sanitized_error(string scenario)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["OpenJibo:Testing:SkipSttDependencyValidation"]="true",
            [TrustedProtocolCredentialRegistryOptions.SectionName+":Enabled"]=scenario=="bad-enabled"?A.SecretAccessKey:"true",
            [TrustedProtocolCredentialRegistryOptions.SectionName+":Credentials:0:AccessKeyId"]=scenario=="empty"?null:A.AccessKeyId,
            [TrustedProtocolCredentialRegistryOptions.SectionName+":Credentials:0:SecretAccessKey"]=scenario=="empty"?null:A.SecretAccessKey
        }).Build();
        var services=new ServiceCollection();services.AddLogging();
        var error=Assert.Throws<InvalidOperationException>(()=>services.AddOpenJiboCloud(config));
        Assert.Null(error.InnerException);Assert.DoesNotContain(A.AccessKeyId,error.ToString());Assert.DoesNotContain(A.SecretAccessKey,error.ToString());
    }

    [Theory][InlineData("a")][InlineData("b")][InlineData("cross-device")][InlineData("shared-account")][InlineData("unsigned")]
    public async Task Protocol_access_uses_registry_and_whole_evidence_before_identity_or_provider_work(string scenario)
    {
        using var key=RuntimePairingCredentialEvidenceTests.Key();var registry=Registry(key,A,B);var store=Store(registry);
        var verifier=new AwsSigV4RequestVerifier(store,trustedCredentials:registry);
        var evidenceA=verifier.Verify(Sign(A)).VerifiedProof!.CreatePairingCredentialEvidence(key)!;
        var evidenceB=verifier.Verify(Sign(B)).VerifiedProof!.CreatePairingCredentialEvidence(key)!;
        var client=new MappedClient(new Dictionary<string,RuntimeAccessIdentity>
        {
            [evidenceA.Binding]=new(Guid.Parse("d5100000-0000-0000-0000-000000000001"),"synthetic-device-A"),
            [evidenceB.Binding]=new(Guid.Parse("d5100000-0000-0000-0000-000000000002"),"synthetic-device-B")
        });
        var account=store.GetAccount();
        var envelope=scenario switch
        {
            "b"=>Sign(B,"synthetic-device-B"),
            "cross-device"=>Sign(A,"synthetic-device-B"),
            "shared-account"=>Sign(new(account.AccessKeyId,account.SecretAccessKey)),
            "unsigned"=>new ProtocolEnvelope(),
            _=>Sign(A)
        };
        var authorized=await new RuntimeProtocolAccessPolicy(store,client,key,verifier).AuthorizeAsync(envelope,default);
        Assert.Equal(scenario is "a" or "b",authorized is not null);
        Assert.Equal(scenario is "a" or "b"?1:0,client.Calls);
        if(authorized is not null)Assert.Equal(scenario=="b"?"synthetic-device-B":"synthetic-device-A",authorized.DeviceId);
    }

    private sealed class MappedClient(Dictionary<string,RuntimeAccessIdentity> identities):IRuntimeAccessClient
    {
        public bool Enabled=>true;
        public bool IsReady=>true;
        public int Calls;
        public RuntimeAccessIdentity? ResolveIdentity(RuntimePairingCredentialEvidence evidence)=>identities.GetValueOrDefault(evidence.Binding);
        public Task<DateTimeOffset?> AuthorizeAsync(RuntimePairingCredentialEvidence evidence,byte[] session,string operation,CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();Calls++;return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddSeconds(3));}
    }
}
