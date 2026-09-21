using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class AwsSigV4RequestVerifierTests
{
    private static readonly DateTimeOffset SignedAt =
        new(2026, 9, 20, 12, 34, 56, TimeSpan.Zero);

    [Theory]
    [InlineData("Notification_20150505.NewRobotToken", "api.jibo.com")]
    [InlineData("Notification_20160715.NewRobotToken", "api.openjibo.com")]
    [InlineData("Notification_20150505.NewRobotToken", "open-jibo.jibo.pro")]
    [InlineData("Notification_20160715.NewRobotToken", "api.jibo.pro")]
    public void Verify_AcceptsPayloadAndTargetBoundKnownVersionAndHost(string target, string host)
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            target: target,
            host: host);

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt.AddMinutes(1))).Verify(envelope, new AwsSigV4OperationPolicy(
            "Notification.NewRobotToken",
            "us-east-1",
            "jibo",
            ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
            ["api.jibo.com", "api.openjibo.com", "open-jibo.jibo.pro", "api.jibo.pro"]));

        Assert.Equal(AwsSigV4VerificationOutcome.VerifiedPayloadAndTarget, result.Outcome);
        Assert.True(result.CredentialAuthenticated);
        Assert.True(result.PayloadBound);
        Assert.True(result.TargetBound);
        Assert.Equal(AwsSigV4ScopeClassification.Match, result.ScopeClassification);
        Assert.Equal(AwsSigV4TargetClassification.MatchSigned, result.TargetClassification);
        Assert.Equal(AwsSigV4HostClassification.Match, result.HostClassification);
        Assert.True(result.OperationAuthenticated);
        Assert.NotNull(result.AccessKeyFingerprint);
        Assert.NotNull(result.VerifiedProof);
        Assert.Equal(nameof(AwsSigV4VerifiedProof), result.VerifiedProof.ToString());
    }

    [Fact]
    public void Verify_ClassifiesCapturedLegacyShapeAsCredentialOnly()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{\"deviceId\":\"Royal-Current-Sage-Canvas\"}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"],
            advertisedPayloadHash: HexSha256([]));

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope, new AwsSigV4OperationPolicy(
            "Notification.NewRobotToken",
            "us-east-1",
            "jibo",
            ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
            ["api.jibo.com"]));

        Assert.Equal(AwsSigV4VerificationOutcome.VerifiedCredentialOnly, result.Outcome);
        Assert.True(result.CredentialAuthenticated);
        Assert.False(result.PayloadBound);
        Assert.False(result.TargetBound);
        Assert.Equal(AwsSigV4ScopeClassification.Match, result.ScopeClassification);
        Assert.Equal(AwsSigV4TargetClassification.MatchUnsigned, result.TargetClassification);
        Assert.Equal(AwsSigV4PayloadClassification.Mismatch, result.PayloadClassification);
        Assert.False(result.OperationAuthenticated);
    }

    [Fact]
    public void Verify_ClassifiesCapturedCreateHubTokenScopeWithoutAuthenticatingUnsignedTarget()
    {
        var store = new InMemoryCloudStateStore();
        var policy = new AwsSigV4OperationPolicy(
            "Account.CreateHubToken",
            "api",
            "jibo",
            ["Account_20151111.CreateHubToken", "Account_20160715.CreateHubToken"],
            ["api.jibo.com"]);
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"],
            advertisedPayloadHash: HexSha256([]),
            region: "api",
            target: "Account_20151111.CreateHubToken");

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope, policy);

        Assert.Equal(AwsSigV4VerificationOutcome.VerifiedCredentialOnly, result.Outcome);
        Assert.Equal(AwsSigV4ScopeClassification.Match, result.ScopeClassification);
        Assert.Equal(AwsSigV4TargetClassification.MatchUnsigned, result.TargetClassification);
        Assert.Equal(AwsSigV4PayloadClassification.Mismatch, result.PayloadClassification);
        Assert.False(result.OperationAuthenticated);
    }

    [Theory]
    [InlineData("wrong-region", "jibo")]
    [InlineData("us-east-1", "wrong-service")]
    public void Verify_ClassifiesSignedScopeMismatchWithoutRejectingCredential(string region, string service)
    {
        var store = new InMemoryCloudStateStore();
        var policy = new AwsSigV4OperationPolicy(
            "Notification.NewRobotToken",
            "us-east-1",
            "jibo",
            ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
            ["api.jibo.com"]);
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            region: region,
            service: service);

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope, policy);

        Assert.True(result.CredentialAuthenticated);
        Assert.Equal(AwsSigV4VerificationOutcome.VerifiedCredentialOnly, result.Outcome);
        Assert.Equal(AwsSigV4ScopeClassification.Mismatch, result.ScopeClassification);
        Assert.Equal(AwsSigV4TargetClassification.MatchSigned, result.TargetClassification);
        Assert.False(result.OperationAuthenticated);
    }

    [Fact]
    public void Verify_ClassifiesSignedTargetMismatchWithoutAuthenticatingOperation()
    {
        var store = new InMemoryCloudStateStore();
        var policy = new AwsSigV4OperationPolicy(
            "Notification.NewRobotToken",
            "us-east-1",
            "jibo",
            ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
            ["api.jibo.com"]);
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            target: "Notification_20150505.OtherOperation");

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope, policy);

        Assert.True(result.CredentialAuthenticated);
        Assert.Equal(AwsSigV4TargetClassification.Mismatch, result.TargetClassification);
        Assert.False(result.OperationAuthenticated);
    }

    [Fact]
    public void Verify_ClassifiesUnsignedTargetMismatchWithoutAuthenticatingOperation()
    {
        var store = new InMemoryCloudStateStore();
        var policy = new AwsSigV4OperationPolicy(
            "Notification.NewRobotToken",
            "us-east-1",
            "jibo",
            ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
            ["api.jibo.com"]);
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"],
            target: "Notification_20150505.OtherOperation");

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope, policy);

        Assert.True(result.CredentialAuthenticated);
        Assert.Equal(AwsSigV4VerificationOutcome.VerifiedCredentialOnly, result.Outcome);
        Assert.Equal(AwsSigV4TargetClassification.Mismatch, result.TargetClassification);
        Assert.False(result.OperationAuthenticated);
    }

    [Fact]
    public void Verify_ClassifiesMissingUnsignedTarget()
    {
        var store = new InMemoryCloudStateStore();
        var policy = new AwsSigV4OperationPolicy(
            "Notification.NewRobotToken",
            "us-east-1",
            "jibo",
            ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
            ["api.jibo.com"]);
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);
        envelope.Headers.Remove("X-Amz-Target");

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope, policy);

        Assert.True(result.CredentialAuthenticated);
        Assert.Equal(AwsSigV4TargetClassification.Missing, result.TargetClassification);
        Assert.False(result.OperationAuthenticated);
    }

    [Fact]
    public void Verify_ClassifiesSignedUnexpectedHostWithoutAuthenticatingOperation()
    {
        var store = new InMemoryCloudStateStore();
        var policy = new AwsSigV4OperationPolicy(
            "Notification.NewRobotToken",
            "us-east-1",
            "jibo",
            ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
            ["api.jibo.com", "api.openjibo.com"]);
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            host: "unexpected.invalid");

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope, policy);

        Assert.True(result.CredentialAuthenticated);
        Assert.Equal(AwsSigV4HostClassification.Mismatch, result.HostClassification);
        Assert.Equal(AwsSigV4VerificationOutcome.VerifiedCredentialOnly, result.Outcome);
        Assert.False(result.OperationAuthenticated);
    }

    [Fact]
    public void Verify_RejectsChangedSignedHeader()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"]);
        envelope.Headers["X-Amz-Target"] = "Account_20151111.ResetKeys";

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope);

        Assert.Equal(AwsSigV4VerificationOutcome.SignatureMismatch, result.Outcome);
        Assert.False(result.CredentialAuthenticated);
    }

    [Fact]
    public void Verify_RejectsExpiredRequestBeforeSignatureUse()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt.AddMinutes(6))).Verify(envelope);

        Assert.Equal(AwsSigV4VerificationOutcome.Expired, result.Outcome);
        Assert.False(result.CredentialAuthenticated);
    }

    [Fact]
    public void Verify_RejectsUnknownCredentialWithoutLeakingIt()
    {
        var store = new InMemoryCloudStateStore();
        var unknown = new AccountProfile
        {
            AccessKeyId = "unknown-access-key",
            SecretAccessKey = "unknown-secret-key"
        };
        var envelope = Sign(
            unknown,
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope);

        Assert.Equal(AwsSigV4VerificationOutcome.UnknownCredential, result.Outcome);
        Assert.DoesNotContain(unknown.AccessKeyId, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(unknown.SecretAccessKey, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_RejectsNonCanonicalSignedHeaderList()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);
        envelope.Headers["Authorization"] = envelope.Headers["Authorization"]
            .Replace("SignedHeaders=host;x-amz-content-sha256;x-amz-date",
                "SignedHeaders=x-amz-date;host;x-amz-content-sha256",
                StringComparison.Ordinal);

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope);

        Assert.Equal(AwsSigV4VerificationOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void Verify_UsesWireHostHeaderInsteadOfHarnessHostNameOverride()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);
        envelope = new ProtocolEnvelope
        {
            Method = envelope.Method,
            HostName = "harness-override.invalid",
            Path = envelope.Path,
            Headers = envelope.Headers,
            BodyText = envelope.BodyText,
            BodyBytes = envelope.BodyBytes
        };

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope);

        Assert.Equal(AwsSigV4VerificationOutcome.VerifiedCredentialOnly, result.Outcome);
    }

    [Fact]
    public void Verify_RejectsQueryBearingEnvelopeUntilRawQueryIsPreserved()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);
        envelope.QueryParameters["duplicate"] = "normalized";

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope);

        Assert.Equal(AwsSigV4VerificationOutcome.Malformed, result.Outcome);
    }

    [Theory]
    [InlineData("GET", "/")]
    [InlineData("POST", "/v1/dispatch")]
    public void Verify_RejectsRequestsOutsideObservedRootPostContract(string method, string path)
    {
        var store = new InMemoryCloudStateStore();
        var signed = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);
        var envelope = new ProtocolEnvelope
        {
            Method = method,
            HostName = signed.HostName,
            Path = path,
            Headers = signed.Headers,
            BodyText = signed.BodyText,
            BodyBytes = signed.BodyBytes
        };

        var result = new AwsSigV4RequestVerifier(
            store,
            new FixedTimeProvider(SignedAt)).Verify(envelope);

        Assert.Equal(AwsSigV4VerificationOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void HandleAccount_ObservesFailedSignatureWithoutRejectingOrLoggingSecrets()
    {
        var store = new InMemoryCloudStateStore();
        var unknown = new AccountProfile
        {
            AccessKeyId = "unknown-access-key",
            SecretAccessKey = "unknown-secret-key"
        };
        var envelope = Sign(
            unknown,
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"]);
        var logger = new ListLogger<CloudAuthProtocolHandler>();
        var publisher = new RecordingReplayPublisher();
        var handler = new CloudAuthProtocolHandler(
            store,
            logger,
            awsSigV4Verifier: new AwsSigV4RequestVerifier(store, new FixedTimeProvider(SignedAt)),
            awsSigV4ReplayObservationPublisher: publisher);

        var response = handler.HandleAccount("CreateHubToken", envelope);
        var messages = string.Join('\n', logger.Messages);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(nameof(AwsSigV4VerificationOutcome.UnknownCredential), messages, StringComparison.Ordinal);
        Assert.DoesNotContain(unknown.AccessKeyId, messages, StringComparison.Ordinal);
        Assert.DoesNotContain(unknown.SecretAccessKey, messages, StringComparison.Ordinal);
        Assert.Empty(publisher.Operations);
    }

    [Theory]
    [InlineData("Account_20151111.CreateHubToken")]
    [InlineData("Account_20160715.CreateHubToken")]
    public void HandleAccount_LogsBoundedCapturedShapeWithoutChangingTokenIssuance(string target)
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"],
            advertisedPayloadHash: HexSha256([]),
            region: "api",
            target: target);
        var logger = new ListLogger<CloudAuthProtocolHandler>();
        var publisher = new RecordingReplayPublisher();
        var handler = new CloudAuthProtocolHandler(
            store,
            logger,
            awsSigV4Verifier: new AwsSigV4RequestVerifier(store, new FixedTimeProvider(SignedAt)),
            awsSigV4ReplayObservationPublisher: publisher);

        var response = handler.HandleAccount("CreateHubToken", envelope);
        var messages = string.Join('\n', logger.Messages);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("operation=Account.CreateHubToken", messages, StringComparison.Ordinal);
        Assert.Contains("scope=Match", messages, StringComparison.Ordinal);
        Assert.Contains("target=MatchUnsigned", messages, StringComparison.Ordinal);
        Assert.Contains("payload=Mismatch", messages, StringComparison.Ordinal);
        Assert.Contains("host=Match", messages, StringComparison.Ordinal);
        Assert.Contains("operationAuthenticated=False", messages, StringComparison.Ordinal);
        Assert.Contains("shadow=true", messages, StringComparison.Ordinal);
        Assert.Equal(["Account.CreateHubToken"], publisher.Operations);
        using var payload = JsonDocument.Parse(response.BodyText);
        var token = payload.RootElement.GetProperty("token").GetString();
        var issued = Assert.IsType<CloudSession>(store.FindIssuedToken(token!));
        Assert.True(HubTokenCredentialBinding.TryRead(issued.Metadata, out var binding));
        Assert.NotNull(binding);
        Assert.False(binding.OperationAuthenticated);
        Assert.Equal(store.GetAccount().CredentialEpoch, binding.CredentialEpoch);
    }

    [Fact]
    public void HandleAccount_LogsOperationAuthenticatedForFullyBoundKnownRequest()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            region: "api",
            target: "Account_20160715.CreateHubToken",
            host: "api.jibo.pro");
        var logger = new ListLogger<CloudAuthProtocolHandler>();
        var handler = new CloudAuthProtocolHandler(
            store,
            logger,
            awsSigV4Verifier: new AwsSigV4RequestVerifier(store, new FixedTimeProvider(SignedAt)));

        var response = handler.HandleAccount("CreateHubToken", envelope);
        var messages = string.Join('\n', logger.Messages);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("host=Match", messages, StringComparison.Ordinal);
        Assert.Contains("operationAuthenticated=True", messages, StringComparison.Ordinal);
        Assert.Contains("shadow=true", messages, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(response.BodyText);
        var token = payload.RootElement.GetProperty("token").GetString();
        var issued = Assert.IsType<CloudSession>(store.FindIssuedToken(token!));
        Assert.True(HubTokenCredentialBinding.TryRead(issued.Metadata, out var binding));
        Assert.NotNull(binding);
        Assert.True(binding.OperationAuthenticated);
        Assert.Equal(store.GetAccount().CredentialEpoch, binding.CredentialEpoch);
    }

    [Fact]
    public void HandleAccount_ReplayPublisherFailureDoesNotChangeTokenIssuance()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"],
            advertisedPayloadHash: HexSha256([]),
            region: "api",
            target: "Account_20151111.CreateHubToken");
        var logger = new ListLogger<CloudAuthProtocolHandler>();
        var handler = new CloudAuthProtocolHandler(
            store,
            logger,
            awsSigV4Verifier: new AwsSigV4RequestVerifier(store, new FixedTimeProvider(SignedAt)),
            awsSigV4ReplayObservationPublisher: new ThrowingReplayPublisher());

        var response = handler.HandleAccount("CreateHubToken", envelope);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains(logger.Messages, message =>
            message.Contains("replay observation enqueue failed", StringComparison.Ordinal));
    }

    [Fact]
    public void HandleNotification_LogsBoundedCapturedShapeWithoutChangingTokenIssuance()
    {
        var store = new InMemoryCloudStateStore();
        var body = "{\"deviceId\":\"captured-shape-robot\"}";
        var envelope = Sign(
            store.GetAccount(),
            body,
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date"],
            advertisedPayloadHash: HexSha256([]));
        var logger = new ListLogger<CloudAuthProtocolHandler>();
        var handler = new CloudAuthProtocolHandler(
            store,
            logger,
            awsSigV4Verifier: new AwsSigV4RequestVerifier(store, new FixedTimeProvider(SignedAt)));

        var response = handler.HandleNotification("NewRobotToken", envelope);
        var messages = string.Join('\n', logger.Messages);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("scope=Match", messages, StringComparison.Ordinal);
        Assert.Contains("target=MatchUnsigned", messages, StringComparison.Ordinal);
        Assert.Contains("payload=Mismatch", messages, StringComparison.Ordinal);
        Assert.Contains("operationAuthenticated=False", messages, StringComparison.Ordinal);
        Assert.Contains("shadow=true", messages, StringComparison.Ordinal);
        Assert.DoesNotContain(store.GetAccount().AccessKeyId, messages, StringComparison.Ordinal);
        Assert.DoesNotContain(store.GetAccount().SecretAccessKey, messages, StringComparison.Ordinal);
        Assert.DoesNotContain(envelope.Headers["Authorization"], messages, StringComparison.Ordinal);
    }

    [Fact]
    public void HandleNotification_LogsOperationAuthenticatedForFullyBoundKnownRequest()
    {
        var store = new InMemoryCloudStateStore();
        var envelope = Sign(
            store.GetAccount(),
            "{\"deviceId\":\"fully-bound-robot\"}",
            SignedAt,
            ["host", "x-amz-content-sha256", "x-amz-date", "x-amz-target"],
            target: "Notification_20160715.NewRobotToken",
            host: "open-jibo.jibo.pro");
        var logger = new ListLogger<CloudAuthProtocolHandler>();
        var handler = new CloudAuthProtocolHandler(
            store,
            logger,
            awsSigV4Verifier: new AwsSigV4RequestVerifier(store, new FixedTimeProvider(SignedAt)));

        var response = handler.HandleNotification("NewRobotToken", envelope);
        var messages = string.Join('\n', logger.Messages);

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("host=Match", messages, StringComparison.Ordinal);
        Assert.Contains("operationAuthenticated=True", messages, StringComparison.Ordinal);
        Assert.Contains("shadow=true", messages, StringComparison.Ordinal);
    }

    private static ProtocolEnvelope Sign(
        AccountProfile account,
        string body,
        DateTimeOffset signedAt,
        IReadOnlyList<string> signedHeaders,
        string? advertisedPayloadHash = null,
        string region = "us-east-1",
        string service = "jibo",
        string target = "Notification_20150505.NewRobotToken",
        string host = "api.jibo.com")
    {
        var timestamp = signedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = signedAt.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = advertisedPayloadHash ?? HexSha256(Encoding.UTF8.GetBytes(body));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = host,
            ["X-Amz-Date"] = timestamp,
            ["X-Amz-Content-Sha256"] = payloadHash,
            ["X-Amz-Target"] = target
        };
        var canonicalHeaders = string.Join('\n', signedHeaders.Select(header =>
            $"{header}:{headers[header]}"));
        var signedHeadersText = string.Join(';', signedHeaders);
        var canonicalRequest = string.Join('\n',
            "POST",
            "/",
            string.Empty,
            canonicalHeaders + "\n",
            signedHeadersText,
            payloadHash);
        var scope = $"{date}/{region}/{service}/aws4_request";
        var stringToSign = string.Join('\n',
            "AWS4-HMAC-SHA256",
            timestamp,
            scope,
            HexSha256(Encoding.UTF8.GetBytes(canonicalRequest)));
        var signingKey = DeriveSigningKey(account.SecretAccessKey, date, region, service);
        var signature = Convert.ToHexString(HMACSHA256.HashData(
            signingKey,
            Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
        headers["Authorization"] =
            $"AWS4-HMAC-SHA256 Credential={account.AccessKeyId}/{scope}, " +
            $"SignedHeaders={signedHeadersText}, Signature={signature}";

        return new ProtocolEnvelope
        {
            Method = "POST",
            HostName = "api.jibo.com",
            Path = "/",
            Headers = headers,
            BodyText = body,
            BodyBytes = Encoding.UTF8.GetBytes(body)
        };
    }

    private static byte[] DeriveSigningKey(string secret, string date, string region, string service)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes($"AWS4{secret}"), Encoding.UTF8.GetBytes(date));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(serviceKey, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string HexSha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingReplayPublisher : IAwsSigV4ReplayObservationPublisher
    {
        public List<string> Operations { get; } = [];

        public bool TryPublish(AwsSigV4VerifiedProof proof, string operation)
        {
            Operations.Add(operation);
            return true;
        }
    }

    private sealed class ThrowingReplayPublisher : IAwsSigV4ReplayObservationPublisher
    {
        public bool TryPublish(AwsSigV4VerifiedProof proof, string operation) =>
            throw new InvalidOperationException("simulated publisher failure");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoOpScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));

        private sealed class NoOpScope : IDisposable
        {
            public static NoOpScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
