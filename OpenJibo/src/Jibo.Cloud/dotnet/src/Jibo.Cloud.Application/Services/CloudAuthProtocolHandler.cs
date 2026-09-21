using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class CloudAuthProtocolHandler(
    ICloudStateStore stateStore,
    ILogger<CloudAuthProtocolHandler>? logger = null,
    RobotIdentitySuggestionStore? identitySuggestionStore = null,
    ReleaseSmokeAuthorizationOptions? releaseSmokeAuthorization = null,
    AwsSigV4RequestVerifier? awsSigV4Verifier = null,
    IAwsSigV4ReplayObservationPublisher? awsSigV4ReplayObservationPublisher = null) : ICloudAuthProtocolHandler
{
    private static readonly AwsSigV4OperationPolicy CreateHubTokenPolicy = new(
        "Account.CreateHubToken",
        "api",
        "jibo",
        ["Account_20151111.CreateHubToken", "Account_20160715.CreateHubToken"],
        ["api.jibo.com", "api.openjibo.com", "open-jibo.jibo.pro", "api.jibo.pro"]);
    private static readonly AwsSigV4OperationPolicy NewRobotTokenPolicy = new(
        "Notification.NewRobotToken",
        "us-east-1",
        "jibo",
        ["Notification_20150505.NewRobotToken", "Notification_20160715.NewRobotToken"],
        ["api.jibo.com", "api.openjibo.com", "open-jibo.jibo.pro", "api.jibo.pro"]);
    private readonly ILogger _logger = logger ?? NullLogger<CloudAuthProtocolHandler>.Instance;
    private readonly ReleaseSmokeAuthorizationOptions _releaseSmokeAuthorization =
        releaseSmokeAuthorization ?? new ReleaseSmokeAuthorizationOptions();
    private readonly AwsSigV4RequestVerifier _awsSigV4Verifier =
        awsSigV4Verifier ?? new AwsSigV4RequestVerifier(stateStore);
    private readonly IAwsSigV4ReplayObservationPublisher _awsSigV4ReplayObservationPublisher =
        awsSigV4ReplayObservationPublisher ?? NullAwsSigV4ReplayObservationPublisher.Instance;
    public ProtocolDispatchResult HandleAccount(string operation, ProtocolEnvelope envelope)
    {
        var account = stateStore.GetAccount();
        var body = envelope.TryParseBody();

        if (operation.Equals("CreateHubToken", StringComparison.OrdinalIgnoreCase))
        {
            var credentialVerification = ObserveLegacyCredential(envelope, CreateHubTokenPolicy);
            var credentialBinding = HubTokenCredentialBinding.FromVerification(credentialVerification);
            var deviceId = !string.IsNullOrWhiteSpace(envelope.DeviceId)
                ? envelope.DeviceId!
                : ReadString(body, "deviceId")
                  ?? ReadString(body, "serial_number")
                  ?? ReadString(body, "serialNumber")
                  ?? ReadString(body, "cpuid")
                  ?? ReadString(body, "cpuId")
                  ?? ReadString(body, "robotId");

            var defaultRobotIsSynthetic = RobotRegistrationSources.IsSynthetic(
                RobotRegistrationSources.Normalize(stateStore.GetRobot().RegistrationSource,
                    stateStore.GetRobot().DeviceId));

            // Real hardware often reaches the cloud through the hub-token flow before it has
            // completed a separate registration exchange. Preserve the observed identity on the
            // durable token/session, but do not promote it into visible inventory. A trusted
            // RobotIdentityLink can attach the session to an existing canonical robot; genuinely
            // new hardware remains an unlinked observed session until it is explicitly registered.
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var registrationSource = envelope.Headers.TryGetValue("X-OpenJibo-Registration-Source",
                    out var sourceHeader)
                    ? sourceHeader
                    : null;
                var smokeHubToken = TryIssueDeploymentSmokeHubToken(deviceId, registrationSource, envelope);
                if (smokeHubToken is not null) return smokeHubToken;
            }

            return ProtocolDispatchResult.Ok(new
            {
                // An empty request must not inherit a deployment-smoke robot as its identity.
                // Leave it unassigned until the physical client provides a real identity signal.
                token = stateStore.IssueHubToken(
                    deviceId,
                    useDefaultRobot: !defaultRobotIsSynthetic,
                    credentialBinding: credentialBinding),
                expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            });
        }

        if (operation.Equals("CreateAccessToken", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new
            {
                token = $"access-{account.AccountId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            });

        if (operation.Equals("CheckEmail", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            var emailExists = stateStore.GetUserByEmail(email) is not null ||
                              email.Equals(account.Email, StringComparison.OrdinalIgnoreCase);
            return ProtocolDispatchResult.Ok(new { exists = emailExists });
        }

        if (operation.Equals("Create", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            var password = ReadString(body, "password") ?? string.Empty;
            var firstName = ReadString(body, "firstName") ?? string.Empty;
            var lastName = ReadString(body, "lastName") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                return ProtocolDispatchResult.Raw(400, "{\"message\":\"Email and password are required\"}",
                    "application/json");

            var created = stateStore.CreateUser(email, password, firstName, lastName);
            if (created is null)
                return ProtocolDispatchResult.Raw(409,
                    "{\"message\":\"An account with this email already exists\"}",
                    "application/json");

            return ProtocolDispatchResult.Ok(BuildAccountResponse(created));
        }

        if (operation.Equals("Login", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            var password = ReadString(body, "password") ?? string.Empty;

            var authenticated = stateStore.AuthenticateUser(email, password);
            if (authenticated is null)
                return ProtocolDispatchResult.Raw(401,
                    "{\"message\":\"Invalid email or password\"}",
                    "application/json");

            return ProtocolDispatchResult.Ok(BuildAccountResponse(authenticated));
        }

        if (operation.Equals("Get", StringComparison.OrdinalIgnoreCase))
        {
            var ids = ReadStringArray(body, "ids");
            if (ids.Count == 0)
                return ProtocolDispatchResult.Ok(new[] { BuildAccountResponse(account) });

            var results = ids
                .Select(id =>
                {
                    var user = stateStore.GetUserById(id);
                    if (user is not null) return BuildAccountResponse(user);
                    return id.Equals(account.AccountId, StringComparison.OrdinalIgnoreCase)
                        ? BuildAccountResponse(account)
                        : null;
                })
                .Where(result => result is not null)
                .ToArray();

            return ProtocolDispatchResult.Ok(results);
        }

        switch (operation)
        {
            case "Update" or "ResetKeys" or "Remove" or "ActivateByCode" or "ResendActivationCode" or
                "ChangePassword" or "SendPasswordReset" or "PasswordResetByCode" or "UpdatePhoto" or "RemovePhoto" or
                "VerifyPhoneByCode" or "AcceptTerms" or "FacebookConnect" or "FacebookMobileConnect":
                return ProtocolDispatchResult.Ok(new
                {
                    id = account.AccountId,
                    email = account.Email,
                    firstName = account.FirstName,
                    lastName = account.LastName,
                    accessKeyId = account.AccessKeyId,
                    secretAccessKey = account.SecretAccessKey
                });
            case "ChangeEmail" or "SendPhoneVerificationCode":
                return ProtocolDispatchResult.Ok(new
                {
                    id = account.AccountId
                });
        }

        if (operation.Equals("GetAccountByAccessToken", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new
            {
                id = account.AccountId,
                accessKeyId = account.AccessKeyId,
                secretAccessKey = account.SecretAccessKey,
                email = account.Email,
                friendlyId = stateStore.GetRobot().RobotId,
                payload = ReadObject(body, "payload")
            });

        if (operation.Equals("Search", StringComparison.OrdinalIgnoreCase))
        {
            var query = (ReadString(body, "query") ?? string.Empty).ToLowerInvariant();
            var haystack = $"{account.Email} {account.FirstName} {account.LastName} {account.AccountId}"
                .ToLowerInvariant();

            return ProtocolDispatchResult.Ok(query.Length > 0 && haystack.Contains(query)
                ?
                [
                    BuildAccountResponse(account)
                ]
                : Array.Empty<object>());
        }

        if (operation.Equals("FacebookPrepareLogin", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new
            {
                url = "https://example.com/facebook-login",
                client_id = "fake-client-id",
                scope = "email",
                response_type = "token",
                state = $"fb-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                redirect_uri = "https://api.jibo.com/facebook/callback"
            });

        if (operation.Equals("ConfirmEmailReset", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new { });

        return ProtocolDispatchResult.Ok(new
        {
            id = account.AccountId,
            email = account.Email,
            firstName = account.FirstName,
            lastName = account.LastName
        });
    }

    public ProtocolDispatchResult HandleNotification(string operation, ProtocolEnvelope envelope)
    {
        if (!operation.Equals("NewRobotToken", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new { ok = true, operation });

        ObserveLegacyCredential(envelope, NewRobotTokenPolicy);

        var body = envelope.TryParseBody();
        var presentedDeviceId = ReadString(body, "deviceId")
                                ?? ReadString(body, "serial_number")
                                ?? ReadString(body, "serialNumber")
                                ?? ReadString(body, "cpuid")
                                ?? ReadString(body, "cpuId");
        var presentedRobotId = ReadString(body, "robotId")
                               ?? ReadString(body, "friendlyId")
                               ?? envelope.DeviceId;
        var deviceId = !string.IsNullOrWhiteSpace(presentedDeviceId)
            ? presentedDeviceId!
            : !string.IsNullOrWhiteSpace(presentedRobotId)
                ? presentedRobotId!
                : "unknown-device";

        var registrationSource = envelope.Headers.TryGetValue("X-OpenJibo-Registration-Source", out var sourceHeader)
            ? sourceHeader
            : null;
        var isDeploymentSmoke = string.Equals(registrationSource, RobotRegistrationSources.DeploymentSmoke,
            StringComparison.OrdinalIgnoreCase);
        DeploymentSmokeRegistrationAuthorization? smokeAuthorization = null;
        var usesReservedSmokeNamespace = deviceId.StartsWith(
            $"{ReleaseSmokeAuthorizationOptions.FixedPrefix}-", StringComparison.OrdinalIgnoreCase);
        if (isDeploymentSmoke || usesReservedSmokeNamespace)
        {
            var presentedSecret = envelope.Headers.TryGetValue("X-OpenJibo-Release-Smoke-Secret", out var secretHeader)
                ? secretHeader
                : null;
            if (!isDeploymentSmoke ||
                !_releaseSmokeAuthorization.TryAuthorize(deviceId, presentedSecret, out smokeAuthorization))
                return ProtocolDispatchResult.Raw(403, "{\"message\":\"Deployment smoke is not authorized.\"}",
                    "application/x-amz-json-1.1");
        }
        var existing = isDeploymentSmoke
            ? stateStore.GetOrCreateDeploymentSmokeDevice(smokeAuthorization!, envelope.FirmwareVersion,
                envelope.ApplicationVersion)
            : FindVisibleDeviceByIdentity(deviceId) ??
              stateStore.GetOrCreateDevice(deviceId, envelope.FirmwareVersion, envelope.ApplicationVersion,
                  registrationSource);
        if (!string.IsNullOrWhiteSpace(presentedRobotId))
            identitySuggestionStore?.Observe(existing.DeviceId, presentedRobotId,
                "auth:Notification.NewRobotToken", "robotId");

        var token = isDeploymentSmoke
            ? stateStore.IssueDeploymentSmokeRobotToken(deviceId)
            : stateStore.IssueRobotToken(existing.DeviceId);
        _logger.LogInformation(
            "Notification NewRobotToken issued deviceId={DeviceId} robotId={RobotId}",
            deviceId,
            presentedRobotId);

        return ProtocolDispatchResult.Ok(new
        {
            token
        });
    }

    private DeviceRegistration? FindVisibleDeviceByIdentity(string identity)
    {
        var normalized = identity.Trim();
        var candidates = stateStore.FindVisibleIdentityCandidates(normalized);
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
        {
            // Choosing among a duplicate would silently issue the reconnecting robot
            // another robot's token, so ambiguity must remain a non-match.
            _logger.LogWarning("NewRobotToken identity {Identity} matched multiple visible robot records; refusing automatic reuse.",
                normalized);
        }

        return null;
    }

    private AwsSigV4Verification ObserveLegacyCredential(
        ProtocolEnvelope envelope,
        AwsSigV4OperationPolicy policy)
    {
        var verification = _awsSigV4Verifier.Verify(envelope, policy);
        if (verification.Outcome == AwsSigV4VerificationOutcome.NotPresented)
        {
            _logger.LogDebug("Legacy SigV4 was not presented operation={Operation}", policy.Operation);
            return verification;
        }

        if (verification.CredentialAuthenticated)
        {
            if (verification.VerifiedProof is not null)
            {
                try
                {
                    _awsSigV4ReplayObservationPublisher.TryPublish(verification.VerifiedProof, policy.Operation);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception,
                        "Legacy SigV4 replay observation enqueue failed operation={Operation} shadow=true",
                        policy.Operation);
                }
            }

            _logger.LogInformation(
                "Legacy credential signature valid operation={Operation} outcome={Outcome} credentialFingerprint={CredentialFingerprint} scope={ScopeClassification} target={TargetClassification} payload={PayloadClassification} host={HostClassification} operationAuthenticated={OperationAuthenticated} robotIdentityProof=false shadow=true",
                policy.Operation,
                verification.Outcome,
                verification.AccessKeyFingerprint,
                verification.ScopeClassification,
                verification.TargetClassification,
                verification.PayloadClassification,
                verification.HostClassification,
                verification.OperationAuthenticated);
            return verification;
        }

        // Shadow mode: record bounded evidence without changing token issuance. Enforcement
        // remains closed until captured stock-robot traffic and cross-replica replay controls pass.
        _logger.LogWarning(
            "Legacy SigV4 did not verify operation={Operation} outcome={Outcome} credentialFingerprint={CredentialFingerprint}",
            policy.Operation,
            verification.Outcome,
            verification.AccessKeyFingerprint);
        return verification;
    }

    private ProtocolDispatchResult? TryIssueDeploymentSmokeHubToken(string deviceId, string? registrationSource,
        ProtocolEnvelope envelope)
    {
        var isDeploymentSmoke = string.Equals(registrationSource, RobotRegistrationSources.DeploymentSmoke,
            StringComparison.OrdinalIgnoreCase);
        var usesReservedNamespace = deviceId.StartsWith($"{ReleaseSmokeAuthorizationOptions.FixedPrefix}-",
            StringComparison.OrdinalIgnoreCase);
        if (!isDeploymentSmoke && !usesReservedNamespace) return null;

        var presentedSecret = envelope.Headers.TryGetValue("X-OpenJibo-Release-Smoke-Secret", out var secretHeader)
            ? secretHeader
            : null;
        if (!isDeploymentSmoke ||
            !_releaseSmokeAuthorization.TryAuthorize(deviceId, presentedSecret, out _))
            return ProtocolDispatchResult.Raw(403, "{\"message\":\"Deployment smoke is not authorized.\"}",
                "application/x-amz-json-1.1");

        var existing = stateStore.GetDevices().FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (existing is null ||
            !string.Equals(RobotRegistrationSources.Normalize(existing.RegistrationSource, existing.DeviceId),
                RobotRegistrationSources.DeploymentSmoke, StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Raw(403,
                "{\"message\":\"Deployment smoke registration requires NewRobotToken.\"}",
                "application/x-amz-json-1.1");

        var token = stateStore.IssueDeploymentSmokeHubToken(existing.DeviceId);
        return ProtocolDispatchResult.Ok(new
        {
            token,
            expires = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds()
        });
    }
    private static string? ReadString(JsonElement? body, string propertyName)
    {
        return body is { ValueKind: JsonValueKind.Object } element &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement? body, string propertyName)
    {
        if (body is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
            return [];

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static JsonElement? ReadObject(JsonElement? body, string propertyName)
    {
        return body is { ValueKind: JsonValueKind.Object } element &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : null;
    }

    private static object BuildAccountResponse(AccountProfile account)
    {
        return new
        {
            id = account.AccountId,
            email = account.Email,
            firstName = account.FirstName,
            lastName = account.LastName,
            gender = "unknown",
            birthday = 631152000000L,
            phoneNumber = "+10000000000",
            photoUrl = string.Empty,
            isActive = true,
            messagingAllowed = true,
            accessKeyId = account.AccessKeyId,
            secretAccessKey = account.SecretAccessKey,
            roles = Array.Empty<object>(),
            facebookConnected = false,
            termsAccepted = true
        };
    }

    private static object BuildAccountResponse(UserRecord user)
    {
        return new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            gender = user.Gender ?? "unknown",
            birthday = user.Birthday ?? 631152000000L,
            phoneNumber = "+10000000000",
            photoUrl = string.Empty,
            isActive = user.IsActive,
            messagingAllowed = true,
            accessKeyId = user.AccessKeyId,
            secretAccessKey = user.SecretAccessKey,
            roles = Array.Empty<object>(),
            facebookConnected = false,
            termsAccepted = true
        };
    }
}
