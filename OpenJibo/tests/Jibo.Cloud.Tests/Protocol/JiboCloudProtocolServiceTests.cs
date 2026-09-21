using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Media;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class JiboCloudProtocolServiceTests
{
    private readonly JiboCloudProtocolService _service = new(new InMemoryCloudStateStore());

    [Fact]
    public async Task ListLoops_RehydratesAnEmptyPartiallyOnboardedState()
    {
        var persistencePath = Path.Combine(Path.GetTempPath(), $"openjibo-loop-list-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(persistencePath, "{\"SchemaVersion\":\"1\",\"Loops\":[]}");
            var store = new InMemoryCloudStateStore(persistencePath);
            var service = new JiboCloudProtocolService(store);

            var result = await service.DispatchAsync(new ProtocolEnvelope
            {
                Method = "POST",
                HostName = "api.jibo.com",
                ServicePrefix = "Loop_20160324",
                Operation = "ListLoops",
                DeviceId = "observed-runtime-robot"
            });

            using var payload = JsonDocument.Parse(result.BodyText);
            Assert.Single(payload.RootElement.EnumerateArray());
            Assert.Equal("openjibo-default-loop", payload.RootElement[0].GetProperty("id").GetString());
        }
        finally
        {
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
        }
    }

    [Fact]
    public async Task ListLoops_ReturnsOnlyTheRequestingRobotsLoop()
    {
        var store = new InMemoryCloudStateStore();
        store.AddLoop("Other", store.GetAccount().AccountId, "other-robot", "other-robot");
        store.AddLoop("Observed", store.GetAccount().AccountId, "observed-runtime-robot", "observed-runtime-robot");
        var service = new JiboCloudProtocolService(store);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            Method = "POST",
            HostName = "api.jibo.com",
            ServicePrefix = "Loop_20160324",
            Operation = "ListLoops",
            DeviceId = "observed-runtime-robot"
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Single(payload.RootElement.EnumerateArray());
        Assert.Equal("observed-runtime-robot", payload.RootElement[0].GetProperty("robotFriendlyId").GetString());
    }

    [Fact]
    public void CreateHubToken_WithDeviceId_BindsSessionWithoutCreatingVisibleInventory()
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store);

        var kitchenResult = handler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"BOJW-KITCHEN-0001"}"""
        });
        var officeResult = handler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"BOJW-OFFICE-0002"}"""
        });

        using var kitchenPayload = JsonDocument.Parse(kitchenResult.BodyText);
        using var officePayload = JsonDocument.Parse(officeResult.BodyText);
        var kitchenToken = kitchenPayload.RootElement.GetProperty("token").GetString()!;
        var officeToken = officePayload.RootElement.GetProperty("token").GetString()!;
        Assert.NotEqual(kitchenToken, officeToken);

        var kitchenSession = store.FindSessionByToken(kitchenToken);
        var officeSession = store.FindSessionByToken(officeToken);

        Assert.NotNull(kitchenSession);
        Assert.NotNull(officeSession);
        Assert.Equal("BOJW-KITCHEN-0001", kitchenSession.DeviceId);
        Assert.Equal("BOJW-OFFICE-0002", officeSession.DeviceId);
        Assert.NotEqual(kitchenSession.DeviceId, officeSession.DeviceId);
        Assert.False(HubTokenCredentialBinding.ContainsMetadata(kitchenSession.Metadata));
        Assert.False(HubTokenCredentialBinding.ContainsMetadata(officeSession.Metadata));
        Assert.DoesNotContain(store.GetDevices(), device => device.DeviceId == "BOJW-KITCHEN-0001");
        Assert.DoesNotContain(store.GetDevices(), device => device.DeviceId == "BOJW-OFFICE-0002");
    }

    [Fact]
    public void CreateHubToken_ReconnectInheritsExplicitCanonicalRobotLinkWithoutDuplicateInventory()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "Royal-Current-Sage-Canvas",
            FriendlyName = "Royal-Current-Sage-Canvas"
        });
        var handler = new CloudAuthProtocolHandler(store);

        var firstResult = handler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"5c0b221fdf9d450019c5e254"}"""
        });
        using var firstPayload = JsonDocument.Parse(firstResult.BodyText);
        var firstSession = store.OpenSession("neo-hub-listen", null,
            firstPayload.RootElement.GetProperty("token").GetString(), "neohub.openjibo.com", "/v1/listen");

        Assert.True(store.BindSessionToDevice(firstSession.SessionId, "Royal-Current-Sage-Canvas"));

        var reconnectResult = handler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"5c0b221fdf9d450019c5e254"}"""
        });
        using var reconnectPayload = JsonDocument.Parse(reconnectResult.BodyText);
        var reconnect = store.OpenSession("neo-hub-listen", null,
            reconnectPayload.RootElement.GetProperty("token").GetString(), "neohub.openjibo.com", "/v1/listen");

        Assert.Equal("5c0b221fdf9d450019c5e254", reconnect.DeviceId);
        Assert.Equal("Royal-Current-Sage-Canvas", reconnect.Metadata["registeredDeviceId"]?.ToString());
        Assert.Equal("Royal-Current-Sage-Canvas", reconnect.Metadata["registeredRobotId"]?.ToString());
        Assert.DoesNotContain(store.GetDevices(), device => device.DeviceId == "5c0b221fdf9d450019c5e254");
    }

    [Fact]
    public async Task UpdateRobot_WithExplicitRegisteredId_BindsFollowingEmptyHubTokenToThatRobot()
    {
        var store = new InMemoryCloudStateStore();
        var authHandler = new CloudAuthProtocolHandler(store);
        var service = new JiboCloudProtocolService(store, authHandler: authHandler);

        authHandler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"Royal-Current-Sage-Canvas"}"""
        });
        await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Robot_20160225",
            Operation = "UpdateRobot",
            BodyText = """{"id":"Royal-Current-Sage-Canvas","payload":{"serialNumber":"BOJW-1000-0017-1114-0008"}}"""
        });

        var hubResult = authHandler.HandleAccount("CreateHubToken", new ProtocolEnvelope { BodyText = "{}" });
        using var hubPayload = JsonDocument.Parse(hubResult.BodyText);
        var hubToken = hubPayload.RootElement.GetProperty("token").GetString()!;

        Assert.Equal("Royal-Current-Sage-Canvas", store.GetRobot().DeviceId);
        Assert.Equal("Royal-Current-Sage-Canvas", store.FindSessionByToken(hubToken)?.DeviceId);
    }

    [Fact]
    public async Task CreateHubToken_ReturnsTokenAndExpiry()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Account_20160715",
            Operation = "CreateHubToken",
            BodyText = "{}"
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.StartsWith("hub-", payload.RootElement.GetProperty("token").GetString());
        Assert.True(payload.RootElement.GetProperty("expires").GetInt64() > 0);
    }

    [Fact]
    public async Task NewRobotToken_UsesBodyDeviceId_WhenHeaderDeviceIdIsEmpty()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Notification_20160715",
            Operation = "NewRobotToken",
            DeviceId = string.Empty,
            BodyText = """{"deviceId":"robot-123"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("robot-123", payload.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void NewRobotToken_DeploymentSmokeReusesSyntheticDeviceAndReplacesPriorToken()
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store, releaseSmokeAuthorization: new ReleaseSmokeAuthorizationOptions
        {
            Enabled = true,
            Secret = "test-smoke-secret",
            MaxConcurrentDevices = 6
        });
        var envelope = new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"open-jibo-smoke-staging-primary"}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-OpenJibo-Registration-Source"] = RobotRegistrationSources.DeploymentSmoke,
                ["X-OpenJibo-Release-Smoke-Secret"] = "test-smoke-secret"
            }
        };

        var first = handler.HandleNotification("NewRobotToken", envelope);
        var second = handler.HandleNotification("NewRobotToken", envelope);

        using var firstPayload = JsonDocument.Parse(first.BodyText);
        using var secondPayload = JsonDocument.Parse(second.BodyText);
        var firstToken = firstPayload.RootElement.GetProperty("token").GetString()!;
        var secondToken = secondPayload.RootElement.GetProperty("token").GetString()!;
        var device = Assert.Single(store.GetDevices(), candidate =>
            candidate.DeviceId == "open-jibo-smoke-staging-primary");
        Assert.Equal(RobotRegistrationSources.DeploymentSmoke, device.RegistrationSource);
        Assert.True(device.IsHidden);
        Assert.NotNull(device.ArchivedUtc);
        Assert.NotEqual(firstToken, secondToken);
        Assert.Null(store.FindSessionByToken(firstToken));
        Assert.NotNull(store.FindSessionByToken(secondToken));
        var smokeSession = Assert.Single(store.GetSessions(), session => session.Kind == "robot" &&
            session.DeviceId == device.DeviceId);
        Assert.InRange(smokeSession.ExpiresUtc!.Value - smokeSession.CreatedUtc,
            TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
    }

    [Fact]
    public void NewRobotToken_DeploymentSmokeHeaderOutsideFixedNamespaceCannotUseSyntheticBehavior()
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store, releaseSmokeAuthorization: new ReleaseSmokeAuthorizationOptions
        {
            Enabled = true,
            Secret = "test-smoke-secret",
            MaxConcurrentDevices = 6
        });

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"real-robot-with-spoofed-header"}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-OpenJibo-Registration-Source"] = RobotRegistrationSources.DeploymentSmoke,
                ["X-OpenJibo-Release-Smoke-Secret"] = "test-smoke-secret"
            }
        });

        Assert.Equal(403, result.StatusCode);
        Assert.DoesNotContain(store.GetDevices(), candidate =>
            candidate.DeviceId == "real-robot-with-spoofed-header");
    }

    [Theory]
    [InlineData(false, "test-smoke-secret", "open-jibo-smoke-staging-primary")]
    [InlineData(true, null, "open-jibo-smoke-staging-primary")]
    [InlineData(true, "wrong-secret", "open-jibo-smoke-staging-primary")]
    [InlineData(true, "test-smoke-secret", "open-jibo-smoke-staging-concurrent-7")]
    [InlineData(true, "test-smoke-secret", "open-jibo-smoke-staging-concurrent-01")]
    [InlineData(true, "test-smoke-secret", "open-jibo-smoke-staging-arbitrary")]
    public void NewRobotToken_DeploymentSmokeFailsClosedWhenAuthorizationIsInvalid(bool enabled,
        string? presentedSecret, string deviceId)
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store, releaseSmokeAuthorization: new ReleaseSmokeAuthorizationOptions
        {
            Enabled = enabled,
            Secret = "test-smoke-secret",
            MaxConcurrentDevices = 6
        });
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-OpenJibo-Registration-Source"] = RobotRegistrationSources.DeploymentSmoke
        };
        if (presentedSecret is not null) headers["X-OpenJibo-Release-Smoke-Secret"] = presentedSecret;

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = JsonSerializer.Serialize(new { deviceId }),
            Headers = headers
        });

        Assert.Equal(403, result.StatusCode);
        Assert.DoesNotContain(store.GetDevices(), candidate => candidate.DeviceId == deviceId);
        Assert.DoesNotContain("test-smoke-secret", result.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-secret", result.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void NewRobotToken_ReservedSmokeNamespaceRequiresExplicitSmokeClassification()
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store, releaseSmokeAuthorization: new ReleaseSmokeAuthorizationOptions
        {
            Enabled = true,
            Secret = "test-smoke-secret",
            MaxConcurrentDevices = 6
        });

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"open-jibo-smoke-staging-primary"}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-OpenJibo-Release-Smoke-Secret"] = "test-smoke-secret"
            }
        });

        Assert.Equal(403, result.StatusCode);
        Assert.DoesNotContain(store.GetDevices(), candidate =>
            candidate.DeviceId == "open-jibo-smoke-staging-primary");
    }

    [Fact]
    public void CreateHubToken_CannotUseDeploymentSmokeRegistrationPath()
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store, releaseSmokeAuthorization: new ReleaseSmokeAuthorizationOptions
        {
            Enabled = true,
            Secret = "test-smoke-secret",
            MaxConcurrentDevices = 6
        });

        var result = handler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"open-jibo-smoke-staging-primary"}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-OpenJibo-Registration-Source"] = RobotRegistrationSources.DeploymentSmoke,
                ["X-OpenJibo-Release-Smoke-Secret"] = "test-smoke-secret"
            }
        });

        Assert.Equal(403, result.StatusCode);
        Assert.DoesNotContain(store.GetDevices(), candidate =>
            candidate.DeviceId == "open-jibo-smoke-staging-primary");
    }
    [Fact]
    public void CreateHubToken_DeploymentSmokeRequiresPriorAuthorizedRegistration()
    {
        const string deviceId = "open-jibo-smoke-staging-primary";
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store, releaseSmokeAuthorization: new ReleaseSmokeAuthorizationOptions
        {
            Enabled = true,
            Secret = "test-smoke-secret",
            MaxConcurrentDevices = 6
        });
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-OpenJibo-Registration-Source"] = RobotRegistrationSources.DeploymentSmoke,
            ["X-OpenJibo-Release-Smoke-Secret"] = "test-smoke-secret"
        };

        var registration = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = JsonSerializer.Serialize(new { deviceId }),
            Headers = headers
        });
        var hubResult = handler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = JsonSerializer.Serialize(new { deviceId }),
            Headers = headers
        });

        Assert.Equal(200, registration.StatusCode);
        Assert.Equal(200, hubResult.StatusCode);
        using var payload = JsonDocument.Parse(hubResult.BodyText);
        var token = payload.RootElement.GetProperty("token").GetString();
        var issued = store.FindIssuedToken(token!);
        Assert.NotNull(issued);
        Assert.Equal("hub", issued.Kind);
        Assert.Equal(deviceId, issued.DeviceId);
        Assert.InRange(issued.ExpiresUtc!.Value - issued.CreatedUtc,
            TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));

        var rejected = handler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = JsonSerializer.Serialize(new { deviceId }),
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
            {
                ["X-OpenJibo-Release-Smoke-Secret"] = "wrong-secret"
            }
        });
        Assert.Equal(403, rejected.StatusCode);
    }

    [Theory]
    [InlineData("open-jibo-smoke-staging-primary")]
    [InlineData("open-jibo-smoke-staging-concurrent-1")]
    [InlineData("open-jibo-smoke-staging-concurrent-6")]
    public void NewRobotToken_DeploymentSmokeAcceptsOnlyConfiguredBoundedIdentities(string deviceId)
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store, releaseSmokeAuthorization: new ReleaseSmokeAuthorizationOptions
        {
            Enabled = true,
            Secret = "test-smoke-secret",
            MaxConcurrentDevices = 6
        });

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = JsonSerializer.Serialize(new { deviceId }),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-OpenJibo-Registration-Source"] = RobotRegistrationSources.DeploymentSmoke,
                ["X-OpenJibo-Release-Smoke-Secret"] = "test-smoke-secret"
            }
        });

        Assert.Equal(200, result.StatusCode);
        Assert.Contains(store.GetDevices(), candidate => candidate.DeviceId == deviceId && candidate.IsHidden &&
            candidate.ArchivedUtc is not null &&
            candidate.RegistrationSource == RobotRegistrationSources.DeploymentSmoke);
    }

    [Fact]
    public void NormalStoreCreation_RejectsReservedSmokeNamespaceEvenWithPhysicalClassification()
    {
        var store = new InMemoryCloudStateStore();

        var error = Assert.Throws<InvalidOperationException>(() =>
            store.GetOrCreateDevice("open-jibo-smoke-staging-primary", null, null,
                RobotRegistrationSources.Physical));

        Assert.Contains("authenticated smoke registration", error.Message);
        Assert.DoesNotContain(store.GetDevices(), candidate =>
            candidate.DeviceId == "open-jibo-smoke-staging-primary");
    }

    [Fact]
    public void NormalStoreUpsert_RejectsNewReservedSmokeNamespaceRecord()
    {
        var store = new InMemoryCloudStateStore();

        var error = Assert.Throws<InvalidOperationException>(() => store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "open-jibo-smoke-staging-primary",
            RobotId = "open-jibo-smoke-staging-primary",
            RegistrationSource = RobotRegistrationSources.Physical
        }));

        Assert.Contains("authenticated smoke registration", error.Message);
        Assert.DoesNotContain(store.GetDevices(), candidate =>
            candidate.DeviceId == "open-jibo-smoke-staging-primary");
    }

    [Fact]
    public void NewRobotToken_RealRobotTokensRetainNormalDurability()
    {
        var store = new InMemoryCloudStateStore();
        var handler = new CloudAuthProtocolHandler(store);
        var envelope = new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"physical-token-durability"}"""
        };

        var first = handler.HandleNotification("NewRobotToken", envelope);
        var second = handler.HandleNotification("NewRobotToken", envelope);

        using var firstPayload = JsonDocument.Parse(first.BodyText);
        using var secondPayload = JsonDocument.Parse(second.BodyText);
        Assert.NotNull(store.FindSessionByToken(firstPayload.RootElement.GetProperty("token").GetString()!));
        Assert.NotNull(store.FindSessionByToken(secondPayload.RootElement.GetProperty("token").GetString()!));
        var issuedSessions = store.GetSessions().Where(session => session.Kind == "robot" &&
            session.DeviceId == "physical-token-durability").ToArray();
        Assert.Equal(2, issuedSessions.Length);
        Assert.All(issuedSessions, session =>
        {
            Assert.Equal("physical-token-durability", session.Metadata["registeredDeviceId"]?.ToString());
            Assert.Equal("robot-physical-token-durability", session.Metadata["registeredRobotId"]?.ToString());
        });
    }

    [Fact]
    public void NewRobotToken_MismatchedPresentedNameBecomesSuggestionWithoutRenamingDevice()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "Royal-Current-Sage-Canvas",
            FriendlyName = "Royal-Current-Sage-Canvas"
        });
        var suggestions = new RobotIdentitySuggestionStore(store);
        var handler = new CloudAuthProtocolHandler(store, identitySuggestionStore: suggestions);

        handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"Royal-Current-Sage-Canvas","robotId":"Coral-Watt-Serrano-Woven"}"""
        });

        var device = store.GetDevices().Single(item => item.DeviceId == "Royal-Current-Sage-Canvas");
        Assert.Equal("Royal-Current-Sage-Canvas", device.RobotId);
        Assert.Equal("Coral-Watt-Serrano-Woven",
            suggestions.GetSuggestion(device.DeviceId)?.ProposedRobotId);
    }

    [Fact]
    public async Task AccountCreate_LoginAndCheckEmail_UseUserBackedAuth()
    {
        var create = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Account_20160715",
            Operation = "Create",
            BodyText = """{"email":"new-user@example.com","password":"secret","firstName":"New","lastName":"User"}"""
        });

        Assert.Equal(200, create.StatusCode);
        using var createPayload = JsonDocument.Parse(create.BodyText);
        Assert.Equal("new-user@example.com", createPayload.RootElement.GetProperty("email").GetString());
        var createdId = createPayload.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(createdId));

        var checkEmail = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Account_20160715",
            Operation = "CheckEmail",
            BodyText = """{"email":"new-user@example.com"}"""
        });

        using var checkPayload = JsonDocument.Parse(checkEmail.BodyText);
        Assert.True(checkPayload.RootElement.GetProperty("exists").GetBoolean());

        var login = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Account_20160715",
            Operation = "Login",
            BodyText = """{"email":"new-user@example.com","password":"secret"}"""
        });

        using var loginPayload = JsonDocument.Parse(login.BodyText);
        Assert.Equal(200, login.StatusCode);
        Assert.Equal("new-user@example.com", loginPayload.RootElement.GetProperty("email").GetString());
        Assert.Equal(createdId, loginPayload.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetRobot_UsesConfiguredRobotId_WhenPresent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:Robot:RobotId"] = "robot-configured-123"
            })
            .Build();
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(), configuration: configuration);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Robot_20160225",
            Operation = "GetRobot",
            BodyText = """{"id":"robot-requested-456"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("robot-configured-123", payload.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void NewRobotToken_FriendlyIdentityReusesExistingCanonicalDevice()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "physical-device-001",
            RobotId = "Royal-Current-Sage-Canvas",
            FriendlyName = "Royal-Current-Sage-Canvas",
            RegistrationSource = RobotRegistrationSources.Physical
        });
        var handler = new CloudAuthProtocolHandler(store);

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"Royal-Current-Sage-Canvas"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        var session = store.FindSessionByToken(payload.RootElement.GetProperty("token").GetString()!);
        Assert.NotNull(session);
        Assert.Equal("physical-device-001", session.DeviceId);
        Assert.Equal("physical-device-001", session.Metadata["registeredDeviceId"]?.ToString());
        Assert.Equal("Royal-Current-Sage-Canvas", session.Metadata["registeredRobotId"]?.ToString());
        Assert.DoesNotContain(store.GetDevices(), device =>
            device.DeviceId.Equals("Royal-Current-Sage-Canvas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewRobotToken_ExactDeviceIdWinsOverOtherIdentityAliases()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "shared-identity",
            RobotId = "canonical-device",
            FriendlyName = "Canonical device"
        });
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "other-device",
            RobotId = "shared-identity",
            FriendlyName = "Other device"
        });
        var handler = new CloudAuthProtocolHandler(store);

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"shared-identity"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        var session = store.FindSessionByToken(payload.RootElement.GetProperty("token").GetString()!);
        Assert.NotNull(session);
        Assert.Equal("shared-identity", session.DeviceId);
    }

    [Fact]
    public void NewRobotToken_AmbiguousAliasesDoNotReuseAnArbitraryRobot()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "physical-device-a",
            RobotId = "shared-robot-name",
            FriendlyName = "Shared robot"
        });
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "physical-device-b",
            RobotId = "shared-robot-name",
            FriendlyName = "Shared robot"
        });
        var handler = new CloudAuthProtocolHandler(store);

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"shared-robot-name"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        var session = store.FindSessionByToken(payload.RootElement.GetProperty("token").GetString()!);
        Assert.NotNull(session);
        Assert.Equal("shared-robot-name", session.DeviceId);
        Assert.Contains(store.GetDevices(), device => device.DeviceId == "shared-robot-name");
    }

    [Fact]
    public void NewRobotToken_AmbiguousFriendlyNameDoesNotReuseAnArbitraryRobot()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "physical-device-a",
            RobotId = "robot-a",
            FriendlyName = "Shared robot"
        });
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "physical-device-b",
            RobotId = "robot-b",
            FriendlyName = "Shared robot"
        });
        var handler = new CloudAuthProtocolHandler(store);

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"Shared robot"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        var session = store.FindSessionByToken(payload.RootElement.GetProperty("token").GetString()!);
        Assert.NotNull(session);
        Assert.Equal("Shared robot", session.DeviceId);
        Assert.Contains(store.GetDevices(), device => device.DeviceId == "Shared robot");
    }
    [Fact]
    public void NewRobotToken_UniqueVerifiedSerialReusesExistingCanonicalDevice()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "physical-device-001",
            RobotId = "Royal-Current-Sage-Canvas",
            FriendlyName = "Royal",
            VerifiedSerialNumber = "BOJW-1000-0017-0815-0075"
        });
        var handler = new CloudAuthProtocolHandler(store);

        var result = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"serialNumber":"BOJW-1000-0017-0815-0075"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        var session = store.FindSessionByToken(payload.RootElement.GetProperty("token").GetString()!);
        Assert.NotNull(session);
        Assert.Equal("physical-device-001", session.DeviceId);
    }

    [Fact]
    public void NewRobotToken_HiddenOrArchivedAliasesDoNotReceiveLiveRobotTokens()
    {
        var store = new InMemoryCloudStateStore();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "archived-device",
            RobotId = "retired-robot",
            FriendlyName = "Retired robot",
            ArchivedUtc = DateTimeOffset.UtcNow
        });
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "hidden-device",
            RobotId = "hidden-robot",
            FriendlyName = "Hidden robot",
            IsHidden = true
        });
        var handler = new CloudAuthProtocolHandler(store);

        var archivedResult = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"retired-robot"}"""
        });
        var hiddenResult = handler.HandleNotification("NewRobotToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"hidden-robot"}"""
        });

        using var archivedPayload = JsonDocument.Parse(archivedResult.BodyText);
        using var hiddenPayload = JsonDocument.Parse(hiddenResult.BodyText);
        Assert.Equal("retired-robot", store.FindSessionByToken(
            archivedPayload.RootElement.GetProperty("token").GetString()!)!.DeviceId);
        Assert.Equal("hidden-robot", store.FindSessionByToken(
            hiddenPayload.RootElement.GetProperty("token").GetString()!)!.DeviceId);
    }
    [Fact]
    public async Task GetRobot_WithRequestedId_DoesNotMutateRobotOrProfile()
    {
        var store = new InMemoryCloudStateStore();
        var originalRobot = store.GetRobot();
        var originalProfile = store.GetRobotProfile();
        var service = new JiboCloudProtocolService(store);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Robot_20160225",
            Operation = "GetRobot",
            BodyText = """{"id":"robot-requested-456"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("robot-requested-456", payload.RootElement.GetProperty("id").GetString());
        Assert.Equal(originalRobot.DeviceId, store.GetRobot().DeviceId);
        Assert.Equal(originalRobot.RobotId, store.GetRobot().RobotId);
        Assert.Equal(originalProfile.RobotId, store.GetRobotProfile().RobotId);
    }


    [Fact]
    public async Task PlanConversion_IsNonDestructiveAndReportsSelfHostedHostBlocker()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20161026",
            Operation = "PlanConversion",
            BodyText =
                """{"deviceId":"robot-plan","targetMode":"open-jibo-self-hosted","rollbackSnapshotId":"rollback-plan"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("willWriteRobot").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("canPrepareRobot").GetBoolean());
        Assert.Equal("open-jibo-self-hosted", payload.RootElement.GetProperty("targetMode").GetString());
        var readiness = payload.RootElement.GetProperty("conversionReadiness");
        Assert.Contains(readiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-self-hosted-target-host");
    }

    [Fact]
    public async Task PlanConversion_WithManagedModePredictsHostedMappingsWithoutIssuingToken()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20161026",
            Operation = "AuditConversion",
            BodyText =
                """{"deviceId":"robot-plan-ready","targetMode":"open-jibo","rollbackSnapshotId":"rollback-plan-ready"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.False(payload.RootElement.GetProperty("willWriteRobot").GetBoolean());
        Assert.True(payload.RootElement.GetProperty("canPrepareRobot").GetBoolean());
        Assert.Equal("api.openjibo.com", payload.RootElement.GetProperty("targetHost").GetString());
        Assert.Equal("api.openjibo.com",
            payload.RootElement.GetProperty("hostMappings").GetProperty("api.jibo.com").GetString());
        Assert.Equal("api.openjibo.com",
            payload.RootElement.GetProperty("hostMappings").GetProperty("api-socket.jibo.com").GetString());
        Assert.Equal("api.openjibo.com",
            payload.RootElement.GetProperty("hostMappings").GetProperty("open-jibo-socket.openjibo.com").GetString());
        Assert.Equal("api.openjibo.com",
            payload.RootElement.GetProperty("hostMappings").GetProperty("neo-hub.jibo.com").GetString());
        Assert.Equal("api.openjibo.com",
            payload.RootElement.GetProperty("hostMappings").GetProperty("neohub.openjibo.com").GetString());
    }


    [Fact]
    public async Task AuditConversion_WhenBaselineAuditRequiredReportsMissingBaselineBlocker()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20161026",
            Operation = "AuditConversion",
            BodyText =
                """{"deviceId":"robot-baseline-blocked","targetMode":"open-jibo","rollbackSnapshotId":"rollback-baseline","requireBaselineAudit":true}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.False(payload.RootElement.GetProperty("canPrepareRobot").GetBoolean());
        var baseline = payload.RootElement.GetProperty("baselineEvidence");
        Assert.True(baseline.GetProperty("requireBaselineAudit").GetBoolean());
        Assert.False(baseline.GetProperty("hasMinimumBaseline").GetBoolean());
        var readiness = payload.RootElement.GetProperty("conversionReadiness");
        Assert.Contains(readiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-baseline-audit");
        Assert.Contains(readiness.GetProperty("requiredEvidence").EnumerateArray(),
            evidence => evidence.GetString() == "baseline-audit-when-required");
    }

    [Fact]
    public async Task PrepareAndSetupRobot_CarriesBaselineEvidenceIntoConnectionProof()
    {
        var prepare = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20161026",
            Operation = "PrepareRobot",
            FirmwareVersion = "1.9.2",
            ApplicationVersion = "1.0.20",
            BodyText =
                """{"deviceId":"robot-baseline-ready","targetMode":"open-jibo","rollbackSnapshotId":"rollback-baseline-ready","stockMode":"oobe","distribution":"stock-us","requireBaselineAudit":true}"""
        });

        Assert.Equal(200, prepare.StatusCode);
        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();
        var prepareBaseline = preparePayload.RootElement.GetProperty("baselineEvidence");
        Assert.True(prepareBaseline.GetProperty("hasMinimumBaseline").GetBoolean());
        Assert.Equal("1.9.2", prepareBaseline.GetProperty("firmwareVersion").GetString());
        Assert.Equal("oobe", prepareBaseline.GetProperty("stockMode").GetString());
        Assert.True(preparePayload.RootElement.GetProperty("conversionReadiness").GetProperty("canWriteRobot")
            .GetBoolean());

        var setup = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20161026",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-baseline-ready"}"""
        });

        Assert.Equal(200, setup.StatusCode);
        using var setupPayload = JsonDocument.Parse(setup.BodyText);
        var setupBaseline = setupPayload.RootElement.GetProperty("baselineEvidence");
        Assert.True(setupBaseline.GetProperty("hasMinimumBaseline").GetBoolean());
        Assert.Equal("stock-us", setupBaseline.GetProperty("distribution").GetString());
        Assert.Equal("oobe", setupBaseline.GetProperty("stockMode").GetString());
    }

    [Fact]
    public async Task PrepareRobot_IssuesOobeTokenAndSetupCompletes()
    {
        var prepare = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "192.168.1.50",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText = """{"deviceId":"robot-abc","loopId":"loop-123","rollbackSnapshotId":"rollback-abc"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        Assert.Equal(200, prepare.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.StartsWith("oobe-", token);
        Assert.Equal("robot-abc", preparePayload.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("loop-123", preparePayload.RootElement.GetProperty("loopId").GetString());
        Assert.Equal("open-jibo", preparePayload.RootElement.GetProperty("targetMode").GetString());
        Assert.Equal("api.openjibo.com", preparePayload.RootElement.GetProperty("targetHost").GetString());
        Assert.Equal("rollback-abc", preparePayload.RootElement.GetProperty("rollbackSnapshotId").GetString());
        Assert.Equal("api.openjibo.com",
            preparePayload.RootElement.GetProperty("hostMappings").GetProperty("api.jibo.com").GetString());
        Assert.True(preparePayload.RootElement.GetProperty("conversionReadiness").GetProperty("canWriteRobot")
            .GetBoolean());

        var setup = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "192.168.1.50",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"open-jibo-abc"}"""
        });

        using var setupPayload = JsonDocument.Parse(setup.BodyText);
        Assert.Equal(200, setup.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(setupPayload.RootElement.GetProperty("accessKeyId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(setupPayload.RootElement.GetProperty("secretAccessKey").GetString()));
        Assert.False(setupPayload.RootElement.GetProperty("serviceMode").GetBoolean());
        Assert.Equal("open-jibo-abc", setupPayload.RootElement.GetProperty("robotId").GetString());
        Assert.Equal("open-jibo", setupPayload.RootElement.GetProperty("targetMode").GetString());
        Assert.Equal("api.openjibo.com", setupPayload.RootElement.GetProperty("targetHost").GetString());
        Assert.Equal("rollback-abc", setupPayload.RootElement.GetProperty("rollbackSnapshotId").GetString());
        Assert.Equal("api.openjibo.com",
            setupPayload.RootElement.GetProperty("hostMappings").GetProperty("api.jibo.com").GetString());
        Assert.True(setupPayload.RootElement.GetProperty("conversionReadiness").GetProperty("canWriteRobot")
            .GetBoolean());
    }

    [Fact]
    public async Task VerifiedSerialEvidence_IsPersistedOnlyThroughPreparedOobeClaim()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText = """{"deviceId":"Royal-Current-Sage-Canvas","rollbackSnapshotId":"serial-claim","serialNumber":"BOJW-1000-0017-1114-0008","serialVerified":true,"serialVerificationMethod":"physical-label"}"""
        });
        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"Royal-Current-Sage-Canvas"}"""
        });

        Assert.Equal(200, setup.StatusCode);
        var device = store.FindDeviceByFriendlyId("Royal-Current-Sage-Canvas");
        Assert.NotNull(device);
        Assert.Equal("BOJW-1000-0017-1114-0008", device.VerifiedSerialNumber);
        Assert.Equal("oobe-verified:physical-label", device.SerialEvidenceSource);
        Assert.NotNull(device.SerialEvidenceVerifiedUtc);

        var passive = await service.DispatchAsync(new ProtocolEnvelope
        {
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = """{"id":"unclaimed-serial","serialNumber":"BOJW-1000-0017-1114-0008"}"""
        });
        Assert.Equal(200, passive.StatusCode);
        Assert.Null(store.FindDeviceByFriendlyId("unclaimed-serial")!.VerifiedSerialNumber);

        var invalid = await service.DispatchAsync(new ProtocolEnvelope
        {
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = """{"id":"invalid-serial","serialNumber":"BOJW-1000-0017-1114-0008","serialVerified":true}"""
        });
        Assert.Equal(400, invalid.StatusCode);
        Assert.Null(store.FindDeviceByFriendlyId("invalid-serial"));
    }

    [Fact]
    public async Task PrepareAndStatus_ReturnSignedOnboardingSessionForProviderReturnBinding()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);

        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-signed-session","loopId":"loop-signed-session","rollbackSnapshotId":"rollback-signed-session"}"""
        });

        Assert.Equal(200, prepare.StatusCode);
        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();
        var onboardingSession = preparePayload.RootElement.GetProperty("onboardingSession");
        Assert.Equal(token, onboardingSession.GetProperty("token").GetString());
        Assert.Equal("robot-signed-session", onboardingSession.GetProperty("deviceId").GetString());
        Assert.Equal("loop-signed-session", onboardingSession.GetProperty("loopId").GetString());
        Assert.Equal("open-jibo", onboardingSession.GetProperty("targetMode").GetString());
        Assert.Equal("api.openjibo.com", onboardingSession.GetProperty("targetHost").GetString());
        Assert.Equal("rollback-signed-session", onboardingSession.GetProperty("rollbackSnapshotId").GetString());
        Assert.Equal("HMAC-SHA256", onboardingSession.GetProperty("signatureAlgorithm").GetString());
        Assert.False(string.IsNullOrWhiteSpace(onboardingSession.GetProperty("nonce").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(onboardingSession.GetProperty("state").GetString()));
        Assert.True(onboardingSession.GetProperty("expires").GetInt64() > 0);

        var payload = onboardingSession.GetProperty("signaturePayload").GetString()!;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(store.GetAccount().SecretAccessKey));
        var expectedSignature =
            Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        Assert.Equal(expectedSignature, onboardingSession.GetProperty("signature").GetString());

        var status = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "GetStatus",
            BodyText = $$"""{"token":"{{token}}"}"""
        });

        using var statusPayload = JsonDocument.Parse(status.BodyText);
        var statusSession = statusPayload.RootElement.GetProperty("onboardingSession");
        Assert.Equal(onboardingSession.GetProperty("nonce").GetString(),
            statusSession.GetProperty("nonce").GetString());
        Assert.Equal(onboardingSession.GetProperty("state").GetString(),
            statusSession.GetProperty("state").GetString());
        Assert.Equal(onboardingSession.GetProperty("signature").GetString(),
            statusSession.GetProperty("signature").GetString());
    }

    [Fact]
    public async Task GetStatus_ReturnsPreparedConversionMetadata()
    {
        var prepare = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText = """{"deviceId":"robot-status","loopId":"loop-status"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();
        var prepareReadiness = preparePayload.RootElement.GetProperty("conversionReadiness");
        Assert.False(prepareReadiness.GetProperty("canWriteRobot").GetBoolean());
        Assert.Contains(prepareReadiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-rollback-snapshot");

        var status = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "GetStatus",
            BodyText = $$"""{"token":"{{token}}"}"""
        });

        Assert.Equal(200, status.StatusCode);
        using var statusPayload = JsonDocument.Parse(status.BodyText);
        Assert.True(statusPayload.RootElement.GetProperty("prepared").GetBoolean());
        Assert.True(statusPayload.RootElement.GetProperty("accepted").GetBoolean());
        Assert.False(statusPayload.RootElement.GetProperty("complete").GetBoolean());
        Assert.False(statusPayload.RootElement.GetProperty("expired").GetBoolean());
        Assert.Equal("robot-status", statusPayload.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("loop-status", statusPayload.RootElement.GetProperty("loopId").GetString());
        Assert.Equal("open-jibo", statusPayload.RootElement.GetProperty("targetMode").GetString());
        Assert.True(statusPayload.RootElement.GetProperty("expires").GetInt64() > 0);
        Assert.Equal("api.openjibo.com", statusPayload.RootElement.GetProperty("targetHost").GetString());
        var readiness = statusPayload.RootElement.GetProperty("conversionReadiness");
        Assert.False(readiness.GetProperty("canWriteRobot").GetBoolean());
        Assert.Contains(readiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-rollback-snapshot");
        var hostMappings = statusPayload.RootElement.GetProperty("hostMappings");
        Assert.Equal("api.openjibo.com", hostMappings.GetProperty("api.jibo.com").GetString());
        Assert.Equal("api.openjibo.com", hostMappings.GetProperty("api-socket.jibo.com").GetString());
        Assert.Equal("api.openjibo.com", hostMappings.GetProperty("open-jibo-socket.openjibo.com").GetString());
        Assert.Equal("api.openjibo.com", hostMappings.GetProperty("neo-hub.jibo.com").GetString());
        Assert.Equal("api.openjibo.com", hostMappings.GetProperty("neohub.openjibo.com").GetString());
    }

    [Fact]
    public async Task GetStatus_ReturnsReadyWhenPreparedWithRollbackSnapshotAndMode()
    {
        var prepare = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-ready","loopId":"loop-ready","targetMode":"open-jibo-self-hosted","targetHost":"jibo.ready.home.arpa","rollbackSnapshotId":"rollback-20260630"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var status = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "GetStatus",
            BodyText = $$"""{"token":"{{token}}"}"""
        });

        Assert.Equal(200, status.StatusCode);
        using var statusPayload = JsonDocument.Parse(status.BodyText);
        Assert.Equal("open-jibo-self-hosted", statusPayload.RootElement.GetProperty("targetMode").GetString());
        Assert.Equal("rollback-20260630", statusPayload.RootElement.GetProperty("rollbackSnapshotId").GetString());
        var readiness = statusPayload.RootElement.GetProperty("conversionReadiness");
        Assert.True(readiness.GetProperty("canWriteRobot").GetBoolean());
        Assert.Empty(readiness.GetProperty("blockers").EnumerateArray());
        Assert.Contains(readiness.GetProperty("requiredEvidence").EnumerateArray(),
            evidence => evidence.GetString() == "rollback-snapshot");
    }

    [Fact]
    public async Task GetStatus_RequiresExplicitTargetHostForSelfHostedMode()
    {
        var prepare = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-self-hosted","targetMode":"open-jibo-self-hosted","rollbackSnapshotId":"rollback-self-hosted"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var status = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "GetStatus",
            BodyText = $$"""{"token":"{{token}}"}"""
        });

        Assert.Equal(200, status.StatusCode);
        using var statusPayload = JsonDocument.Parse(status.BodyText);
        Assert.Equal("api.openjibo.com", statusPayload.RootElement.GetProperty("targetHost").GetString());
        var readiness = statusPayload.RootElement.GetProperty("conversionReadiness");
        Assert.False(readiness.GetProperty("canWriteRobot").GetBoolean());
        Assert.Contains(readiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-self-hosted-target-host");
        Assert.Contains(readiness.GetProperty("requiredEvidence").EnumerateArray(),
            evidence => evidence.GetString() == "self-hosted-target-host-when-self-hosted");
    }

    [Fact]
    public async Task SetupRobot_WithSelfHostedTargetHost_WritesOwnerSuppliedMappings()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-self-hosted-ready","targetMode":"open-jibo-self-hosted","targetHost":"jibo.home.arpa","rollbackSnapshotId":"rollback-self-hosted-ready"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-self-hosted-ready"}"""
        });

        Assert.Equal(200, setup.StatusCode);
        var robot = store.GetRobot();
        Assert.Equal("jibo.home.arpa", robot.HostMappings["api.jibo.com"]);
        Assert.Equal("jibo.home.arpa", robot.HostMappings["api-socket.jibo.com"]);
        Assert.Equal("jibo.home.arpa", robot.HostMappings["neo-hub.jibo.com"]);
    }

    [Fact]
    public async Task VerifyConnection_AfterPreparedSetupReturnsConversionProof()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-proof","loopId":"loop-proof","targetMode":"open-jibo-self-hosted","targetHost":"jibo.proof.home.arpa","rollbackSnapshotId":"rollback-proof"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-proof"}"""
        });

        Assert.Equal(200, setup.StatusCode);

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$"""{"token":"{{token}}"}"""
        });

        Assert.Equal(200, proof.StatusCode);
        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.True(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.True(proofPayload.RootElement.GetProperty("prepared").GetBoolean());
        Assert.True(proofPayload.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(OpenJiboCloudBuildInfo.Version, proofPayload.RootElement.GetProperty("cloudVersion").GetString());
        Assert.Equal("robot-robot-proof", proofPayload.RootElement.GetProperty("robotId").GetString());
        Assert.Equal("robot-proof", proofPayload.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("loop-proof", proofPayload.RootElement.GetProperty("loopId").GetString());
        Assert.Equal("open-jibo-self-hosted", proofPayload.RootElement.GetProperty("targetMode").GetString());
        Assert.Equal("jibo.proof.home.arpa", proofPayload.RootElement.GetProperty("targetHost").GetString());
        Assert.Equal("rollback-proof", proofPayload.RootElement.GetProperty("rollbackSnapshotId").GetString());
        Assert.Equal("jibo.proof.home.arpa",
            proofPayload.RootElement.GetProperty("hostMappings").GetProperty("api.jibo.com").GetString());
        Assert.Equal("jibo.proof.home.arpa",
            proofPayload.RootElement.GetProperty("storedHostMappings").GetProperty("api.jibo.com").GetString());
        Assert.True(proofPayload.RootElement.GetProperty("hostMappingsMatch").GetBoolean());
        Assert.Empty(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray());
        Assert.True(proofPayload.RootElement.GetProperty("conversionReadiness").GetProperty("canWriteRobot")
            .GetBoolean());
    }

    [Fact]
    public async Task VerifyConnection_WhenStoredHostMappingsDriftReportsConnectionBlocker()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-drift","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-drift"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-drift"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var robot = store.GetRobot();
        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = robot.DeviceId,
            RobotId = robot.RobotId,
            FriendlyName = robot.FriendlyName,
            FirmwareVersion = robot.FirmwareVersion,
            ApplicationVersion = robot.ApplicationVersion,
            IsActive = robot.IsActive,
            CertificateThumbprint = robot.CertificateThumbprint,
            IssuedIdentityId = robot.IssuedIdentityId,
            BuildHash = robot.BuildHash,
            ConfigHash = robot.ConfigHash,
            HostMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api.jibo.com"] = "wrong.home.arpa",
                ["api-socket.jibo.com"] = "jibo.expected.home.arpa",
                ["neo-hub.jibo.com"] = "jibo.expected.home.arpa"
            }
        });

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$"""{"token":"{{token}}"}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.False(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.False(proofPayload.RootElement.GetProperty("hostMappingsMatch").GetBoolean());
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "host-mapping-mismatch");
        Assert.Equal("wrong.home.arpa",
            proofPayload.RootElement.GetProperty("storedHostMappings").GetProperty("api.jibo.com").GetString());
    }

    [Fact]
    public async Task VerifyConnection_WhenRobotReportsDifferentConnectedHostReportsBlocker()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-host-proof","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-host-proof"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-host-proof"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$"""{"token":"{{token}}","reportedConnectionHost":"https://wrong.home.arpa:443/v1/oobe"}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.False(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.Equal("wrong.home.arpa", proofPayload.RootElement.GetProperty("reportedConnectionHost").GetString());
        Assert.False(proofPayload.RootElement.GetProperty("reportedConnectionHostMatches").GetBoolean());
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "reported-connection-host-mismatch");
    }


    [Fact]
    public async Task VerifyConnection_WhenLiveRobotProofRequiredWithoutReportedEvidenceReportsBlockers()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-live-proof","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-live-proof"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-live-proof"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$"""{"token":"{{token}}","requireReportedConnectionProof":true}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.False(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.True(proofPayload.RootElement.GetProperty("requiresReportedConnectionProof").GetBoolean());
        Assert.False(proofPayload.RootElement.GetProperty("reportedConnectionProofComplete").GetBoolean());
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-reported-connection-host");
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-reported-host-mappings");
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionProofGuidance").EnumerateArray(),
            guidance => guidance.GetProperty("blocker").GetString() == "missing-reported-connection-host" &&
                        guidance.GetProperty("severity").GetString() == "release-gate" &&
                        guidance.GetProperty("ownerAction").GetString()!.Contains("reportedConnectionHost", StringComparison.Ordinal));
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionProofGuidance").EnumerateArray(),
            guidance => guidance.GetProperty("blocker").GetString() == "missing-reported-host-mappings" &&
                        guidance.GetProperty("severity").GetString() == "release-gate" &&
                        guidance.GetProperty("ownerAction").GetString()!.Contains("api-socket.jibo.com", StringComparison.Ordinal));
    }


    [Fact]
    public async Task VerifyConnection_WhenLiveRobotProofRequiresPartialDnsMappingsReportsIncompleteBlocker()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-partial-dns-proof","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-partial-dns-proof"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-partial-dns-proof"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$$"""{"token":"{{{token}}}","requireLiveRobotProof":true,"reportedConnectionHost":"jibo.expected.home.arpa","reportedHostMappings":{"api.jibo.com":"jibo.expected.home.arpa"}}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.False(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.False(proofPayload.RootElement.GetProperty("reportedHostMappingsMatch").GetBoolean());
        Assert.False(proofPayload.RootElement.GetProperty("reportedConnectionProofComplete").GetBoolean());
        Assert.False(proofPayload.RootElement.GetProperty("reportedHostMappingCompleteness").GetProperty("complete").GetBoolean());
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "incomplete-reported-host-mappings");
        Assert.Contains(proofPayload.RootElement.GetProperty("reportedHostMappingCompleteness").GetProperty("missingHosts").EnumerateArray(),
            host => host.GetString() == "api-socket.jibo.com");
        Assert.Contains(proofPayload.RootElement.GetProperty("reportedHostMappingCompleteness").GetProperty("missingHosts").EnumerateArray(),
            host => host.GetString() == "open-jibo-socket.openjibo.com");
        Assert.Contains(proofPayload.RootElement.GetProperty("reportedHostMappingCompleteness").GetProperty("missingHosts").EnumerateArray(),
            host => host.GetString() == "neo-hub.jibo.com");
        Assert.Contains(proofPayload.RootElement.GetProperty("reportedHostMappingCompleteness").GetProperty("missingHosts").EnumerateArray(),
            host => host.GetString() == "neohub.openjibo.com");
    }


    [Fact]
    public async Task VerifyConnection_WhenFreshProofRequiredWithoutObservationReportsBlocker()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-fresh-proof-missing","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-fresh-proof-missing"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-fresh-proof-missing"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$$"""{"token":"{{{token}}}","requireLiveRobotProof":true,"requireFreshConnectionProof":true,"reportedConnectionHost":"jibo.expected.home.arpa","reportedHostMappings":{"api.jibo.com":"jibo.expected.home.arpa","api-socket.jibo.com":"jibo.expected.home.arpa","open-jibo-socket.openjibo.com":"jibo.expected.home.arpa","neo-hub.jibo.com":"jibo.expected.home.arpa","neohub.openjibo.com":"jibo.expected.home.arpa"}}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.False(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.True(proofPayload.RootElement.GetProperty("requiresFreshConnectionProof").GetBoolean());
        Assert.False(proofPayload.RootElement.GetProperty("reportedConnectionProof").GetProperty("fresh").GetBoolean());
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-proof-observed-at");
    }

    [Fact]
    public async Task VerifyConnection_WhenFreshObservedProofMatchesReturnsProofMetadata()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-fresh-proof","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-fresh-proof"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-fresh-proof"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var observedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$$"""{"token":"{{{token}}}","requireLiveRobotProof":true,"requireFreshConnectionProof":true,"connectionProofObservedAt":"{{{observedAt}}}","connectionProofSource":"robot-conversion-harness","connectionProofId":"capture-42","reportedConnectionHost":"jibo.expected.home.arpa","reportedHostMappings":{"api.jibo.com":"jibo.expected.home.arpa","api-socket.jibo.com":"jibo.expected.home.arpa","open-jibo-socket.openjibo.com":"jibo.expected.home.arpa","neo-hub.jibo.com":"jibo.expected.home.arpa","neohub.openjibo.com":"jibo.expected.home.arpa"}}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        var proofMetadata = proofPayload.RootElement.GetProperty("reportedConnectionProof");
        Assert.True(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.True(proofMetadata.GetProperty("complete").GetBoolean());
        Assert.True(proofMetadata.GetProperty("fresh").GetBoolean());
        Assert.Equal(observedAt, proofMetadata.GetProperty("observedAt").GetInt64());
        Assert.Equal(900, proofMetadata.GetProperty("maxAgeSeconds").GetInt64());
        Assert.Equal(120, proofMetadata.GetProperty("acceptedFutureClockSkewSeconds").GetInt64());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(observedAt).AddMinutes(15).ToUnixTimeMilliseconds(),
            proofMetadata.GetProperty("freshUntil").GetInt64());
        Assert.Equal("robot-conversion-harness", proofMetadata.GetProperty("source").GetString());
        Assert.Equal("capture-42", proofMetadata.GetProperty("id").GetString());
    }


    [Fact]
    public async Task VerifyConnection_WhenFreshProofUsesCustomMaxAgeReportsStaleBlockerAndPolicy()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-custom-proof-window","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-custom-proof-window"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-custom-proof-window"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var observedAt = DateTimeOffset.UtcNow.AddSeconds(-125).ToUnixTimeMilliseconds();
        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$$"""{"token":"{{{token}}}","requireLiveRobotProof":true,"requireFreshConnectionProof":true,"connectionProofMaxAgeSeconds":60,"connectionProofObservedAt":"{{{observedAt}}}","reportedConnectionHost":"jibo.expected.home.arpa","reportedHostMappings":{"api.jibo.com":"jibo.expected.home.arpa","api-socket.jibo.com":"jibo.expected.home.arpa","open-jibo-socket.openjibo.com":"jibo.expected.home.arpa","neo-hub.jibo.com":"jibo.expected.home.arpa","neohub.openjibo.com":"jibo.expected.home.arpa"}}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        var proofMetadata = proofPayload.RootElement.GetProperty("reportedConnectionProof");
        Assert.False(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.False(proofMetadata.GetProperty("fresh").GetBoolean());
        Assert.Equal(60, proofMetadata.GetProperty("maxAgeSeconds").GetInt64());
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "stale-proof-observed-at");
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionProofGuidance").EnumerateArray(),
            guidance => guidance.GetProperty("blocker").GetString() == "stale-proof-observed-at" &&
                        guidance.GetProperty("severity").GetString() == "release-gate");
    }

    [Fact]
    public async Task VerifyConnection_WhenRobotReportsLegacyHostMappingsEchoesDnsProof()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-dns-proof","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-dns-proof"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-dns-proof"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$$"""{"token":"{{{token}}}","reportedHostMappings":{"api.jibo.com":"https://jibo.expected.home.arpa:443","api-socket.jibo.com":"jibo.expected.home.arpa","open-jibo-socket.openjibo.com":"jibo.expected.home.arpa","neo-hub.jibo.com":"jibo.expected.home.arpa","neohub.openjibo.com":"jibo.expected.home.arpa"}}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.True(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.True(proofPayload.RootElement.GetProperty("reportedHostMappingsMatch").GetBoolean());
        Assert.Equal("jibo.expected.home.arpa",
            proofPayload.RootElement.GetProperty("reportedHostMappings").GetProperty("api.jibo.com").GetString());
        Assert.Empty(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray());
    }

    [Fact]
    public async Task VerifyConnection_WhenRobotReportsLegacyHostMappingDriftReportsBlocker()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-dns-drift","targetMode":"open-jibo-self-hosted","targetHost":"jibo.expected.home.arpa","rollbackSnapshotId":"rollback-dns-drift"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-dns-drift"}"""
        });
        Assert.Equal(200, setup.StatusCode);

        var proof = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "VerifyConnection",
            BodyText = $$$"""{"token":"{{{token}}}","reportedDnsMappings":{"api.jibo.com":"wrong.home.arpa","api-socket.jibo.com":"jibo.expected.home.arpa","open-jibo-socket.openjibo.com":"jibo.expected.home.arpa","neo-hub.jibo.com":"jibo.expected.home.arpa","neohub.openjibo.com":"jibo.expected.home.arpa"}}"""
        });

        using var proofPayload = JsonDocument.Parse(proof.BodyText);
        Assert.False(proofPayload.RootElement.GetProperty("connected").GetBoolean());
        Assert.False(proofPayload.RootElement.GetProperty("reportedHostMappingsMatch").GetBoolean());
        Assert.Contains(proofPayload.RootElement.GetProperty("connectionBlockers").EnumerateArray(),
            blocker => blocker.GetString() == "reported-host-mapping-mismatch");
    }

    [Fact]
    public async Task SetupRobot_UpdatesIdentityGraphRobotAndOpenJiboHostMappings()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            FirmwareVersion = "1.9.2",
            ApplicationVersion = "1.0.20",
            BodyText = """{"id":"robot-converted"}"""
        });

        Assert.Equal(200, result.StatusCode);
        var robot = store.GetRobot();
        Assert.Equal("robot-converted", robot.DeviceId);
        Assert.Equal("robot-robot-converted", robot.RobotId);
        Assert.Equal("1.9.2", robot.FirmwareVersion);
        Assert.Equal("1.0.20", robot.ApplicationVersion);
        Assert.Equal("api.openjibo.com", robot.HostMappings["api.jibo.com"]);
        Assert.Equal("api.openjibo.com", robot.HostMappings["api-socket.jibo.com"]);
        Assert.Equal("api.openjibo.com", robot.HostMappings["neo-hub.jibo.com"]);

        var snapshot = store.GetIdentityGraph();
        Assert.Contains(snapshot.EvidenceSignals, signal =>
            signal.SignalKind == "host-mapping" &&
            signal.SignalId == "api.jibo.com" &&
            signal.Value == "api.openjibo.com");
    }

    [Fact]
    public async Task SetupRobot_RejectsDeploymentSmokeRegistrationBeforeIdentityWrite()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-OpenJibo-Registration-Source"] = "deployment-smoke"
            },
            BodyText =
                """{"token":"smoke-token","id":"open-jibo-smoke-robot-123","targetMode":"open-jibo","rollbackSnapshotId":"rollback-smoke"}"""
        });

        Assert.Equal(403, result.StatusCode);
        Assert.Null(store.FindDeviceByFriendlyId("open-jibo-smoke-robot-123"));
    }

    [Fact]
    public async Task UpdateRobot_RejectsReservedSmokeNamespaceAndPreservesRealRobot()
    {
        var store = new InMemoryCloudStateStore();
        var original = store.GetRobot().DeviceId;
        var service = new JiboCloudProtocolService(store);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Robot_20160225",
            Operation = "UpdateRobot",
            BodyText = """{"id":"open-jibo-smoke-staging-primary"}"""
        });

        Assert.Equal(403, result.StatusCode);
        Assert.Equal(original, store.GetRobot().DeviceId);
        Assert.DoesNotContain(store.GetDevices(), candidate =>
            candidate.DeviceId == "open-jibo-smoke-staging-primary");
    }


    [Fact]
    public async Task SetupRobot_WithPreparedTokenRejectsUnsafeBodyOverridesBeforeIdentityWrite()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var originalRobot = store.GetRobot();

        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-safe-override","targetMode":"open-jibo","rollbackSnapshotId":"rollback-safe-override"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText =
                $$"""{"token":"{{token}}","id":"robot-should-not-write","targetMode":"open-jibo-self-hosted","targetHost":""}"""
        });

        Assert.Equal(409, setup.StatusCode);
        using var setupPayload = JsonDocument.Parse(setup.BodyText);
        var readiness = setupPayload.RootElement.GetProperty("conversionReadiness");
        Assert.False(readiness.GetProperty("canWriteRobot").GetBoolean());
        Assert.Contains(readiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-self-hosted-target-host");
        Assert.Equal(originalRobot.DeviceId, store.GetRobot().DeviceId);
    }

    [Fact]
    public async Task ReconnectRobot_WithPreparedToken_ReturnsOk()
    {
        var prepare = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "192.168.1.50",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText = """{"deviceId":"robot-reconnect","rollbackSnapshotId":"rollback-reconnect"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var reconnect = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "192.168.1.50",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "ReconnectRobot",
            BodyText = $$"""{"token":"{{token}}","id":"open-jibo-reconnect"}"""
        });

        Assert.Equal(200, reconnect.StatusCode);
        using var reconnectPayload = JsonDocument.Parse(reconnect.BodyText);
        Assert.Equal("ok", reconnectPayload.RootElement.GetProperty("result").GetString());
        Assert.Equal("open-jibo-reconnect", reconnectPayload.RootElement.GetProperty("robotId").GetString());
        Assert.Equal("open-jibo", reconnectPayload.RootElement.GetProperty("targetMode").GetString());
        Assert.Equal("api.openjibo.com", reconnectPayload.RootElement.GetProperty("targetHost").GetString());
        Assert.Equal("rollback-reconnect", reconnectPayload.RootElement.GetProperty("rollbackSnapshotId").GetString());
        Assert.Equal("api.openjibo.com",
            reconnectPayload.RootElement.GetProperty("hostMappings").GetProperty("api.jibo.com").GetString());
        Assert.True(reconnectPayload.RootElement.GetProperty("conversionReadiness").GetProperty("canWriteRobot")
            .GetBoolean());
    }


    [Fact]
    public async Task SetupRobot_WithPreparedTokenMissingRollbackSnapshot_IsBlockedBeforeIdentityWrite()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        var originalRobot = store.GetRobot();

        var prepare = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText = """{"deviceId":"robot-blocked","loopId":"loop-blocked"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var setup = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "SetupRobot",
            BodyText = $$"""{"token":"{{token}}","id":"robot-should-not-write"}"""
        });

        Assert.Equal(409, setup.StatusCode);
        using var setupPayload = JsonDocument.Parse(setup.BodyText);
        Assert.Equal("conversion readiness blocked", setupPayload.RootElement.GetProperty("error").GetString());
        var readiness = setupPayload.RootElement.GetProperty("conversionReadiness");
        Assert.False(readiness.GetProperty("canWriteRobot").GetBoolean());
        Assert.Contains(readiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "missing-rollback-snapshot");
        Assert.Equal(originalRobot.DeviceId, store.GetRobot().DeviceId);
    }

    [Fact]
    public async Task GetStatus_ReportsUnsupportedTargetModeBlocker()
    {
        var prepare = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "PrepareRobot",
            BodyText =
                """{"deviceId":"robot-unsupported","targetMode":"open-jibo-experimental","rollbackSnapshotId":"rollback-unsupported"}"""
        });

        using var preparePayload = JsonDocument.Parse(prepare.BodyText);
        var token = preparePayload.RootElement.GetProperty("token").GetString();

        var status = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20160715",
            Operation = "GetStatus",
            BodyText = $$"""{"token":"{{token}}"}"""
        });

        Assert.Equal(200, status.StatusCode);
        using var statusPayload = JsonDocument.Parse(status.BodyText);
        Assert.Equal("open-jibo-experimental", statusPayload.RootElement.GetProperty("targetMode").GetString());
        var readiness = statusPayload.RootElement.GetProperty("conversionReadiness");
        Assert.False(readiness.GetProperty("canWriteRobot").GetBoolean());
        Assert.Contains(readiness.GetProperty("blockers").EnumerateArray(),
            blocker => blocker.GetString() == "unsupported-target-mode");
        Assert.Contains(readiness.GetProperty("supportedTargetModes").EnumerateArray(),
            mode => mode.GetString() == "open-jibo-self-hosted");
    }

    [Fact]
    public async Task GetUpdateFrom_WithoutStagedUpdate_ReturnsNoContent()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "GetUpdateFrom",
            BodyText = """{"subsystem":"robot","fromVersion":"1.0.0"}"""
        });

        Assert.Equal(204, result.StatusCode);
        Assert.Equal(string.Empty, result.BodyText);
    }

    [Fact]
    public async Task GetUpdateFrom_IgnoresSameVersionUpdates()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText = """{"fromVersion":"1.0.0","toVersion":"1.0.1","changes":"Bug fix","subsystem":"robot"}"""
        });

        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "GetUpdateFrom",
            BodyText = """{"subsystem":"robot","fromVersion":"1.0.1"}"""
        });

        Assert.Equal(204, result.StatusCode);
        Assert.Equal(string.Empty, result.BodyText);
    }

    [Fact]
    public async Task GetUpdateFrom_ReturnsMatchingFromVersionUpdate()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText = """{"fromVersion":"1.0.0","toVersion":"1.0.1","changes":"Bug fix","subsystem":"robot"}"""
        });

        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "GetUpdateFrom",
            BodyText = """{"subsystem":"robot","fromVersion":"1.0.0"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal("1.0.0", payload.RootElement.GetProperty("fromVersion").GetString());
        Assert.Equal("1.0.1", payload.RootElement.GetProperty("toVersion").GetString());
    }

    [Fact]
    public async Task ListUpdatesFrom_IgnoresSameVersionUpdates()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText = """{"fromVersion":"1.0.0","toVersion":"1.0.1","changes":"Bug fix","subsystem":"robot"}"""
        });

        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "ListUpdatesFrom",
            BodyText = """{"subsystem":"robot","fromVersion":"1.0.1"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Empty(payload.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task ListUpdatesFrom_UsesFromVersionAsLowerBound()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText = """{"fromVersion":"1.0.0","toVersion":"1.0.1","changes":"Older update","subsystem":"robot"}"""
        });

        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText = """{"fromVersion":"1.0.1","toVersion":"1.0.2","changes":"Newer update","subsystem":"robot"}"""
        });

        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "ListUpdatesFrom",
            BodyText = """{"subsystem":"robot","fromVersion":"1.0.1"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Single(payload.RootElement.EnumerateArray());
        Assert.Equal("1.0.2", payload.RootElement[0].GetProperty("toVersion").GetString());
    }

    [Fact]
    public async Task GetUpdateFrom_UsesFromVersionAsLowerBound()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText = """{"fromVersion":"1.0.0","toVersion":"1.0.1","changes":"Older update","subsystem":"robot"}"""
        });

        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText = """{"fromVersion":"1.0.1","toVersion":"1.0.2","changes":"Newer update","subsystem":"robot"}"""
        });

        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "GetUpdateFrom",
            BodyText = """{"subsystem":"robot","fromVersion":"1.0.1"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal("1.0.2", payload.RootElement.GetProperty("toVersion").GetString());
    }

    [Fact]
    public async Task SchedulerGetUpdate_ReturnsWrappedUpdateList()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "127.0.0.1",
            Method = "GET",
            Path = "/update"
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.True(payload.RootElement.TryGetProperty("updates", out var updates));
        Assert.Equal(JsonValueKind.Array, updates.ValueKind);
        Assert.Empty(updates.EnumerateArray());
    }

    [Fact]
    public async Task SchedulerCheckUpdates_ReturnsWrappedUpdateList()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/check-updates",
            BodyText = """{"filter":"robot"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("OK", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Empty(data.EnumerateArray());
    }

    [Fact]
    public async Task DispatchAsync_WithoutConfiguredAcceptedHosts_AllowsUnknownHosts()
    {
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(), null,
            new ConfigurationBuilder().Build());

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "example.invalid",
            Method = "POST",
            ServicePrefix = "Account_20160715",
            Operation = "CreateHubToken",
            BodyText = "{}"
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.StartsWith("hub-", payload.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public async Task DispatchAsync_WithConfiguredAcceptedHosts_RejectsUnknownHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:AcceptedHosts:0"] = "api.jibo.com"
            })
            .Build();

        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(), null, configuration);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "example.invalid",
            Method = "POST",
            ServicePrefix = "Account_20160715",
            Operation = "CreateHubToken",
            BodyText = "{}"
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.False(payload.RootElement.GetProperty("accepted").GetBoolean());
        Assert.Equal("example.invalid", payload.RootElement.GetProperty("host").GetString());
    }

    [Fact]
    public async Task LegacyLoopSuspendPath_ReturnsOk()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/v1/loop/suspend"
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task LoopList_ReturnsSeededMembers()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Loop_20160715",
            Operation = "List"
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        var loops = payload.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(loops);
        var members = loops[0].GetProperty("members").EnumerateArray().ToArray();
        Assert.NotEmpty(members);
        Assert.Contains(members, member => member.GetProperty("type").GetString() == "owner");
        Assert.DoesNotContain(members, member => member.GetProperty("type").GetString() == "robot");
    }

    [Fact]
    public async Task LoopAddLoop_CreatesOwnerAndRobotScopedLoop()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Loop_20160715",
            Operation = "AddLoop",
            BodyText = """{"name":"Kitchen Loop","robotId":"robot-kitchen","robotFriendlyId":"kitchen-jibo"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal("loop-kitchen-jibo", payload.RootElement.GetProperty("id").GetString());
        Assert.Equal("Kitchen Loop", payload.RootElement.GetProperty("name").GetString());
        Assert.Contains(payload.RootElement.GetProperty("members").EnumerateArray(),
            member => member.GetProperty("type").GetString() == "owner");
    }

    [Fact]
    public async Task SetupRobot_WithoutLoopId_CreatesRobotScopedLoop()
    {
        var first = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20161026",
            Operation = "SetupRobot",
            BodyText = """{"id":"robot-onboard-1"}"""
        });
        var second = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "OOBE_20161026",
            Operation = "SetupRobot",
            BodyText = """{"id":"robot-onboard-2"}"""
        });

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        using var firstPayload = JsonDocument.Parse(first.BodyText);
        using var secondPayload = JsonDocument.Parse(second.BodyText);
        var firstLoopId = firstPayload.RootElement.GetProperty("loopId").GetString();
        var secondLoopId = secondPayload.RootElement.GetProperty("loopId").GetString();
        Assert.Equal("loop-robot-onboard-1", firstLoopId);
        Assert.Equal("loop-robot-onboard-2", secondLoopId);
        Assert.NotEqual(firstLoopId, secondLoopId);
    }

    [Fact]
    public async Task LoopListMembers_ReturnsSeededMembersForDefaultLoop()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Loop_20160715",
            Operation = "ListMembers",
            BodyText = """{"loopId":"openjibo-default-loop"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        var members = payload.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(members);
        Assert.All(members, member => Assert.Equal("openjibo-default-loop", member.GetProperty("loopId").GetString()));
        Assert.Contains(members, member => member.GetProperty("type").GetString() == "owner");
    }

    [Fact]
    public async Task LoopInviteMember_ReturnsUpdatedLoop()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Loop_20160715",
            Operation = "InviteMember",
            BodyText = """{"loopId":"openjibo-default-loop","email":"friend@example.com","firstName":"Friend"}"""
        });

        Assert.Equal(200, result.StatusCode);
        using var payload = JsonDocument.Parse(result.BodyText);
        var members = payload.RootElement.GetProperty("members").EnumerateArray().ToArray();
        Assert.Contains(members,
            member => member.GetProperty("account").GetProperty("email").GetString() == "friend@example.com");
    }

    [Fact]
    public async Task LoopRecognitionObservation_CanBeListedForConversionSmokeEvidence()
    {
        var invite = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Loop_20160715",
            Operation = "InviteMember",
            BodyText =
                """{"loopId":"openjibo-default-loop","email":"recognized@example.com","firstName":"Recognized"}"""
        });

        using var invitePayload = JsonDocument.Parse(invite.BodyText);
        var memberId = invitePayload.RootElement
            .GetProperty("members")
            .EnumerateArray()
            .Single(member =>
                member.GetProperty("account").GetProperty("email").GetString() == "recognized@example.com")
            .GetProperty("id")
            .GetString();

        var record = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Loop_20160715",
            Operation = "RecordRecognitionObservation",
            BodyText =
                $$"""{"loopId":"openjibo-default-loop","memberId":"{{memberId}}","modality":"face","outcome":"recognized","confidence":0.97,"source":"conversion-smoke"}"""
        });

        Assert.Equal(200, record.StatusCode);

        var list = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Loop_20160715",
            Operation = "ListRecognitionObservations",
            BodyText = """{"loopId":"openjibo-default-loop"}"""
        });

        Assert.Equal(200, list.StatusCode);
        using var listPayload = JsonDocument.Parse(list.BodyText);
        var observation = Assert.Single(listPayload.RootElement.EnumerateArray());
        Assert.Equal(memberId, observation.GetProperty("memberId").GetString());
        Assert.Equal("face", observation.GetProperty("modality").GetString());
        Assert.Equal("recognized", observation.GetProperty("outcome").GetString());
        Assert.Equal("conversion-smoke", observation.GetProperty("source").GetString());
    }

    [Fact]
    public async Task SchedulerStatusEndpoints_DefaultToNotBackingUpAndNoDownload()
    {
        var backupStatus = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/backup-status"
        });

        using var backupPayload = JsonDocument.Parse(backupStatus.BodyText);
        Assert.Equal(200, backupStatus.StatusCode);
        Assert.Equal("OK", backupPayload.RootElement.GetProperty("status").GetString());
        Assert.False(backupPayload.RootElement.GetProperty("data").GetBoolean());

        var downloadStatus = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/download-status"
        });

        using var downloadPayload = JsonDocument.Parse(downloadStatus.BodyText);
        Assert.Equal(200, downloadStatus.StatusCode);
        Assert.Equal("OK", downloadPayload.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, downloadPayload.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task SchedulerBackupRobot_StartsBackupThenClearsIt()
    {
        var start = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/backup-robot"
        });

        using var startPayload = JsonDocument.Parse(start.BodyText);
        Assert.Equal(200, start.StatusCode);
        Assert.Equal("OK", startPayload.RootElement.GetProperty("status").GetString());

        var immediate = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/backup-status"
        });

        using var immediatePayload = JsonDocument.Parse(immediate.BodyText);
        Assert.True(immediatePayload.RootElement.GetProperty("data").GetBoolean());

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);

            var finished = await _service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "localhost",
                Method = "POST",
                Path = "/backup-status"
            });

            using var finishedPayload = JsonDocument.Parse(finished.BodyText);
            if (!finishedPayload.RootElement.GetProperty("data").GetBoolean())
                return;
        }

        Assert.Fail("Backup status did not clear within the expected time window.");
    }

    [Fact]
    public async Task SchedulerOtaUpdate_ProgressesFromBackupToDownloadAndCompletes()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText =
                """{"fromVersion":"12.10.0","toVersion":"12.10.1","changes":"OTA progress","subsystem":"robot","length":1200}"""
        });

        var start = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/ota-update"
        });

        using var startPayload = JsonDocument.Parse(start.BodyText);
        Assert.Equal(200, start.StatusCode);
        Assert.Equal("OK", startPayload.RootElement.GetProperty("status").GetString());

        var backing = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/backup-status"
        });

        using var backingPayload = JsonDocument.Parse(backing.BodyText);
        Assert.True(backingPayload.RootElement.GetProperty("data").GetBoolean());

        var downloadingPayload = await WaitForSchedulerDownloadDataKindAsync(JsonValueKind.Object);
        Assert.Equal("OK", downloadingPayload.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Object, downloadingPayload.GetProperty("data").ValueKind);

        var completedDownloadPayload = await WaitForSchedulerDownloadDataKindAsync(JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, completedDownloadPayload.GetProperty("data").ValueKind);

        var updates = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "GET",
            Path = "/update"
        });

        using var updatesPayload = JsonDocument.Parse(updates.BodyText);
        Assert.Contains(updatesPayload.RootElement.GetProperty("updates").EnumerateArray(),
            item => item.GetProperty("subsystem").GetString() == "robot" &&
                    item.GetProperty("changes").GetString() == "OTA progress" &&
                    item.GetProperty("downloaded").GetBoolean());
    }

    [Fact]
    public async Task SchedulerOtaUpdate_UsesRequestBodyFilter()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText =
                """{"fromVersion":"12.10.0","toVersion":"12.10.1","changes":"Robot OTA","subsystem":"robot","length":400}"""
        });

        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText =
                """{"fromVersion":"12.10.0","toVersion":"12.10.1","changes":"Avatar OTA","subsystem":"avatar","length":400}"""
        });

        var start = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/ota-update",
            BodyText = """{"filter":"robot"}"""
        });

        Assert.Equal(200, start.StatusCode);

        await Task.Delay(1000);

        var updates = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "GET",
            Path = "/update"
        });

        using var updatesPayload = JsonDocument.Parse(updates.BodyText);
        Assert.Contains(updatesPayload.RootElement.GetProperty("updates").EnumerateArray(),
            item => item.GetProperty("subsystem").GetString() == "robot" &&
                    item.GetProperty("downloaded").GetBoolean());
        Assert.Contains(updatesPayload.RootElement.GetProperty("updates").EnumerateArray(),
            item => item.GetProperty("subsystem").GetString() == "avatar" &&
                    !item.GetProperty("downloaded").GetBoolean());
    }

    [Fact]
    public async Task SchedulerApplyUpdate_AdvancesFirmwareAndClearsPendingUpdate()
    {
        var create = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText =
                """{"fromVersion":"12.10.0","toVersion":"12.10.2","changes":"Controlled OTA apply","subsystem":"robot","length":400}"""
        });

        using var createPayload = JsonDocument.Parse(create.BodyText);
        var updateId = createPayload.RootElement.GetProperty("_id").GetString();

        var apply = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/apply-update",
            BodyText = "{\"id\":\"" + updateId + "\"}"
        });

        using var applyPayload = JsonDocument.Parse(apply.BodyText);
        Assert.Equal(200, apply.StatusCode);
        Assert.Equal("OK", applyPayload.RootElement.GetProperty("status").GetString());
        Assert.Equal("12.10.2", applyPayload.RootElement.GetProperty("firmwareVersion").GetString());
        Assert.True(applyPayload.RootElement.GetProperty("rebootRequired").GetBoolean());

        var checkUpdates = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/check-updates",
            BodyText = """{"filter":"robot"}"""
        });

        using var checkPayload = JsonDocument.Parse(checkUpdates.BodyText);
        Assert.Empty(checkPayload.RootElement.GetProperty("data").EnumerateArray());

        var robot = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Robot_20160715",
            Operation = "GetRobot",
            BodyText = "{}"
        });

        using var robotPayload = JsonDocument.Parse(robot.BodyText);
        Assert.Equal("12.10.2", robotPayload.RootElement.GetProperty("payload").GetProperty("platform").GetString());
    }

    [Fact]
    public async Task SchedulerCheckUpdates_IgnoresSameVersionNoopUpdates()
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText =
                """{"fromVersion":"12.10.0","toVersion":"12.10.0","changes":"No update available","subsystem":"robot","length":0}"""
        });

        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/check-updates",
            BodyText = """{"filter":"robot"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("OK", payload.RootElement.GetProperty("status").GetString());
        Assert.True(payload.RootElement.TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Empty(data.EnumerateArray());
    }

    [Fact]
    public async Task SchedulerBackupEndpoints_AreDisabledWhenConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:EnableBackupRestore"] = "false"
            })
            .Build();

        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(), null, configuration);

        var backupStatus = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/backup-status"
        });

        using var backupPayload = JsonDocument.Parse(backupStatus.BodyText);
        Assert.Equal(200, backupStatus.StatusCode);
        Assert.Equal("OK", backupPayload.RootElement.GetProperty("status").GetString());
        Assert.False(backupPayload.RootElement.GetProperty("data").GetBoolean());

        var checkUpdates = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/check-updates",
            BodyText = """{"filter":"robot"}"""
        });

        using var checkUpdatesPayload = JsonDocument.Parse(checkUpdates.BodyText);
        Assert.Equal(200, checkUpdates.StatusCode);
        Assert.Equal("OK", checkUpdatesPayload.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Array, checkUpdatesPayload.RootElement.GetProperty("data").ValueKind);

        var backupRobot = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/backup-robot"
        });

        using var backupRobotPayload = JsonDocument.Parse(backupRobot.BodyText);
        Assert.Equal(200, backupRobot.StatusCode);
        Assert.Equal("OK", backupRobotPayload.RootElement.GetProperty("status").GetString());

        var otaUpdate = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/ota-update"
        });

        using var otaUpdatePayload = JsonDocument.Parse(otaUpdate.BodyText);
        Assert.Equal(200, otaUpdate.StatusCode);
        Assert.Equal("OK", otaUpdatePayload.RootElement.GetProperty("status").GetString());

        var downloadStatus = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "localhost",
            Method = "POST",
            Path = "/download-status"
        });

        using var downloadPayload = JsonDocument.Parse(downloadStatus.BodyText);
        Assert.Equal(200, downloadStatus.StatusCode);
        Assert.Equal("OK", downloadPayload.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, downloadPayload.RootElement.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task BackupList_WithoutBackups_ReturnsEmptyList()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Backup_20170222",
            Operation = "List",
            BodyText = """{"loopId":"loop-123"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(JsonValueKind.Array, payload.RootElement.ValueKind);
        Assert.Empty(payload.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task BackupNew_ReturnsUploadUrl_AndListIncludesBackup()
    {
        var create = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Backup_20170222",
            Operation = "New",
            BodyText = """{"loopId":"loop-123"}"""
        });

        using var createPayload = JsonDocument.Parse(create.BodyText);
        Assert.Equal(200, create.StatusCode);
        var uploadUrl = createPayload.RootElement.GetProperty("uploadUrl").GetString();
        Assert.NotNull(uploadUrl);
        Assert.Contains("/upload/backup/", uploadUrl);

        var list = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Backup_20170222",
            Operation = "List",
            BodyText = """{"loopId":"loop-123"}"""
        });

        using var listPayload = JsonDocument.Parse(list.BodyText);
        Assert.Equal(200, list.StatusCode);
        Assert.NotEmpty(listPayload.RootElement.EnumerateArray());
        var item = listPayload.RootElement.EnumerateArray().First();
        Assert.True(item.TryGetProperty("location", out var location));
        Assert.Contains("/backup/", location.GetProperty("url").GetString());
        Assert.False(string.IsNullOrWhiteSpace(location.GetProperty("expires").GetString()));
    }

    [Theory]
    [InlineData("locationString")]
    [InlineData("locationObject")]
    public async Task BackupRestore_AcceptsMappedLocationShapes(string locationShape)
    {
        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText =
                """{"fromVersion":"12.10.0","toVersion":"12.10.1","changes":"Location restore baseline","subsystem":"robot"}"""
        });

        var backup = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Backup_20170222",
            Operation = "Create",
            BodyText = """{"name":"location-restore-point"}"""
        });

        using var backupPayload = JsonDocument.Parse(backup.BodyText);
        var locationUrl = backupPayload.RootElement.GetProperty("location").GetProperty("url").GetString();
        Assert.False(string.IsNullOrWhiteSpace(locationUrl));

        await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "CreateUpdate",
            BodyText =
                """{"fromVersion":"12.10.1","toVersion":"12.10.2","changes":"Location restore stray update","subsystem":"robot"}"""
        });

        var restoreBody = locationShape == "locationObject"
            ? JsonSerializer.Serialize(new { location = new { url = locationUrl } })
            : JsonSerializer.Serialize(new { location = locationUrl });
        var restore = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Backup_20170222",
            Operation = "Restore",
            BodyText = restoreBody
        });

        using var restorePayload = JsonDocument.Parse(restore.BodyText);
        Assert.Equal(200, restore.StatusCode);
        Assert.Equal("ok", restorePayload.RootElement.GetProperty("result").GetString());
        Assert.True(restorePayload.RootElement.GetProperty("rebootRequired").GetBoolean());

        var updates = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "ListUpdates",
            BodyText = """{"subsystem":"robot"}"""
        });

        using var updatesPayload = JsonDocument.Parse(updates.BodyText);
        Assert.Contains(updatesPayload.RootElement.EnumerateArray(),
            item => item.GetProperty("changes").GetString() == "Location restore baseline");
        Assert.DoesNotContain(updatesPayload.RootElement.EnumerateArray(),
            item => item.GetProperty("changes").GetString() == "Location restore stray update");
    }

    [Fact]
    public async Task PutEventsAsync_ReturnsUploadUrl()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Log_20160715",
            Operation = "PutEventsAsync",
            BodyText = "{}"
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal("gzip", payload.RootElement.GetProperty("contentEncoding").GetString());
        Assert.Contains("/upload/log-events", payload.RootElement.GetProperty("uploadUrl").GetString());
    }

    [Fact]
    public async Task LegacyLogBinaryPost_IsCapturedAsBinaryArtifact()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Media.Tests", Guid.NewGuid().ToString("N"));
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(), new FileMediaContentStore(directoryPath));
        var binaryPayload = new byte[] { 0x00, 0xFF, 0x4F, 0x67, 0x67 };

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            Path = "/log/binary/legacy-upload",
            DeviceId = "Royal-Current-Sage-Canvas",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/octet-stream"
            },
            BodyBytes = binaryPayload
        });

        Assert.Equal(200, result.StatusCode);
        var manifestPath = Path.Combine(directoryPath, "logs", "binary", "legacy-upload.json");
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(binaryPayload, (await new FileMediaContentStore(directoryPath)
            .LoadAsync("logs/binary/legacy-upload"))!.Content);
    }

    [Fact]
    public async Task PersonListHolidays_DoesNotThrow_WhenLoopStateIsEmpty()
    {
        var persistencePath = Path.Combine(Path.GetTempPath(), $"openjibo-empty-holidays-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(persistencePath, """
                                                          {
                                                            "SchemaVersion": "1",
                                                            "Revision": 0,
                                                            "Loops": [],
                                                            "Holidays": []
                                                          }
                                                          """);

            var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));
            var result = await service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Person_20160715",
                Operation = "ListHolidays",
                BodyText = "{}"
            });

            using var payload = JsonDocument.Parse(result.BodyText);
            Assert.Equal(200, result.StatusCode);
            Assert.Equal(JsonValueKind.Array, payload.RootElement.ValueKind);
            Assert.NotEmpty(payload.RootElement.EnumerateArray());
        }
        finally
        {
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
        }
    }

    [Fact]
    public async Task PersonListHolidays_MergesPersistedLoopHolidayOverrides()
    {
        var persistencePath = Path.Combine(Path.GetTempPath(), $"openjibo-loop-holidays-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(persistencePath, """
                                                          {
                                                            "SchemaVersion": "1",
                                                            "Revision": 0,
                                                            "Loops": [],
                                                            "Holidays": [
                                                              {
                                                                "Id": "birthday-1",
                                                                "EventId": "birthday-1",
                                                                "Name": "Jake's Birthday",
                                                                "Category": "birthday",
                                                                "LoopId": "loop-123",
                                                                "MemberId": "person-123",
                                                                "IsEnabled": true,
                                                                "Date": "2026-05-19",
                                                                "Source": "manual",
                                                                "CountryCode": "US",
                                                                "Created": "2026-05-19T00:00:00Z"
                                                              }
                                                            ]
                                                          }
                                                          """);

            var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));
            var result = await service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Person_20160715",
                Operation = "ListHolidays",
                BodyText = """{"loopId":"loop-123"}"""
            });

            using var payload = JsonDocument.Parse(result.BodyText);
            Assert.Equal(200, result.StatusCode);
            Assert.Contains(payload.RootElement.EnumerateArray(),
                item => item.GetProperty("name").GetString() == "Jake's Birthday");
        }
        finally
        {
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
        }
    }

    [Fact]
    public async Task PersonUpsertCommute_ThenListCommute_ReturnsPersistedLoopProfile()
    {
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore());

        var upsert = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Person_20160715",
            Operation = "UpsertCommute",
            BodyText =
                """{"loopId":"loop-123","mode":"walking","workHour":8,"workMinute":15,"typicalDurationMinutes":22}"""
        });

        using var upsertPayload = JsonDocument.Parse(upsert.BodyText);
        Assert.Equal(200, upsert.StatusCode);
        Assert.Equal("loop-123", upsertPayload.RootElement.GetProperty("loopId").GetString());
        Assert.Equal("walking", upsertPayload.RootElement.GetProperty("mode").GetString());

        var listed = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Person_20160715",
            Operation = "ListCommute",
            BodyText = """{"loopId":"loop-123"}"""
        });

        using var listedPayload = JsonDocument.Parse(listed.BodyText);
        Assert.Equal(200, listed.StatusCode);
        Assert.Contains(listedPayload.RootElement.EnumerateArray(),
            item => item.GetProperty("loopId").GetString() == "loop-123" &&
                    item.GetProperty("mode").GetString() == "walking" &&
                    item.GetProperty("workHour").GetInt32() == 8);
    }

    [Fact]
    public async Task MediaCreateAndGet_ReturnsCreatedItem()
    {
        var created = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Media_20160725",
            Operation = "Create",
            BodyText = """{"path":"/media/test-item","type":"image","reference":"demo"}"""
        });

        using var createdPayload = JsonDocument.Parse(created.BodyText);
        Assert.Equal("/media/test-item", createdPayload.RootElement.GetProperty("path").GetString());

        var fetched = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Media_20160725",
            Operation = "Get",
            BodyText = """{"paths":["/media/test-item"]}"""
        });

        using var fetchedPayload = JsonDocument.Parse(fetched.BodyText);
        Assert.Single(fetchedPayload.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task MediaCreate_PersistsAcrossStoreRecreation_WhenPersistencePathIsConfigured()
    {
        var persistencePath = Path.Combine(Path.GetTempPath(), $"openjibo-state-{Guid.NewGuid():N}.json");
        try
        {
            var firstService = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));
            await firstService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Media_20160725",
                Operation = "Create",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = "image/jpeg"
                },
                BodyText = """{"path":"persisted-photo","type":"image","reference":"photo"}"""
            });

            var secondService = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));
            var listed = await secondService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Media_20160725",
                Operation = "List",
                BodyText = "{}"
            });

            using var listedPayload = JsonDocument.Parse(listed.BodyText);
            Assert.Single(listedPayload.RootElement.EnumerateArray());
            Assert.Equal("persisted-photo", listedPayload.RootElement[0].GetProperty("path").GetString());
        }
        finally
        {
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
        }
    }

    [Fact]
    public async Task UpdateAndBackupPersistAcrossStoreRecreation_WhenPersistencePathIsConfigured()
    {
        var persistencePath = Path.Combine(Path.GetTempPath(), $"openjibo-update-backup-{Guid.NewGuid():N}.json");
        try
        {
            var firstService = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));

            await firstService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Update_20160715",
                Operation = "CreateUpdate",
                BodyText =
                    """{"fromVersion":"12.10.0","toVersion":"12.10.1","changes":"Restore proof","subsystem":"robot"}"""
            });

            await firstService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Backup_20160715",
                Operation = "Create",
                BodyText = """{"name":"manual-backup"}"""
            });

            var firstStore = new InMemoryCloudStateStore(persistencePath);
            var firstInfo = firstStore.GetPersistenceStateInfo();

            var secondService = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));
            var secondStore = new InMemoryCloudStateStore(persistencePath);
            var secondInfo = secondStore.GetPersistenceStateInfo();

            var updates = await secondService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Update_20160715",
                Operation = "ListUpdates",
                BodyText = """{"subsystem":"robot"}"""
            });

            var backups = await secondService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Backup_20160715",
                Operation = "List",
                BodyText = "{}"
            });

            var schedulerUpdates = await secondService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "127.0.0.1",
                Method = "GET",
                Path = "/update/robot"
            });

            using var updatesPayload = JsonDocument.Parse(updates.BodyText);
            using var backupsPayload = JsonDocument.Parse(backups.BodyText);
            using var schedulerPayload = JsonDocument.Parse(schedulerUpdates.BodyText);

            Assert.Equal(firstInfo.Revision, secondInfo.Revision);
            Assert.Equal("1", firstInfo.SchemaVersion);
            Assert.Equal("1", secondInfo.SchemaVersion);
            Assert.NotNull(secondInfo.LastLoadedUtc);
            Assert.NotNull(secondInfo.LastSavedUtc);
            Assert.NotEmpty(updatesPayload.RootElement.EnumerateArray());
            Assert.Contains(updatesPayload.RootElement.EnumerateArray(),
                item => item.GetProperty("changes").GetString() == "Restore proof");
            Assert.NotEmpty(backupsPayload.RootElement.EnumerateArray());
            Assert.Contains(backupsPayload.RootElement.EnumerateArray(),
                item => item.GetProperty("location").GetProperty("url").GetString()!.Contains("/backup/",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(schedulerPayload.RootElement.GetProperty("updates").EnumerateArray(),
                item => item.GetProperty("subsystem").GetString() == "robot" &&
                        item.GetProperty("changes").GetString() == "Restore proof");
        }
        finally
        {
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
        }
    }

    [Fact]
    public async Task BackupRestore_RehydratesPersistedStateAcrossStoreRecreation()
    {
        var persistencePath = Path.Combine(Path.GetTempPath(), $"openjibo-restore-{Guid.NewGuid():N}.json");
        try
        {
            var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));

            await service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Update_20160715",
                Operation = "CreateUpdate",
                BodyText =
                    """{"fromVersion":"12.10.0","toVersion":"12.10.1","changes":"Restore baseline","subsystem":"robot"}"""
            });

            var backup = await service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Backup_20170222",
                Operation = "Create",
                BodyText = """{"name":"restore-point"}"""
            });

            using var backupPayload = JsonDocument.Parse(backup.BodyText);
            var backupId = backupPayload.RootElement.GetProperty("etag").GetString();
            Assert.False(string.IsNullOrWhiteSpace(backupId));

            await service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Update_20160715",
                Operation = "CreateUpdate",
                BodyText =
                    """{"fromVersion":"12.10.1","toVersion":"12.10.2","changes":"After backup","subsystem":"robot"}"""
            });

            var restore = await service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Backup_20170222",
                Operation = "Restore",
                BodyText = $$"""{"backupId":"{{backupId}}"}"""
            });

            using var restorePayload = JsonDocument.Parse(restore.BodyText);
            Assert.Equal(200, restore.StatusCode);
            Assert.Equal("ok", restorePayload.RootElement.GetProperty("result").GetString());
            Assert.True(restorePayload.RootElement.GetProperty("rebootRequired").GetBoolean());
            Assert.Equal(backupId, restorePayload.RootElement.GetProperty("backupId").GetString());

            var secondService = new JiboCloudProtocolService(new InMemoryCloudStateStore(persistencePath));
            var updates = await secondService.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "api.jibo.com",
                Method = "POST",
                ServicePrefix = "Update_20160715",
                Operation = "ListUpdates",
                BodyText = """{"subsystem":"robot"}"""
            });

            using var updatesPayload = JsonDocument.Parse(updates.BodyText);
            Assert.Contains(updatesPayload.RootElement.EnumerateArray(),
                item => item.GetProperty("changes").GetString() == "Restore baseline");
            Assert.DoesNotContain(updatesPayload.RootElement.EnumerateArray(),
                item => item.GetProperty("changes").GetString() == "After backup");
        }
        finally
        {
            if (File.Exists(persistencePath)) File.Delete(persistencePath);
        }
    }

    [Fact]
    public async Task MediaCreate_StoresBodyAndServesMediaUrl()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Media_20160725",
            Operation = "Create",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "image/jpeg",
                ["x-path"] = "photo-blob-1",
                ["x-type"] = "image"
            },
            BodyText = "binary-photo-placeholder"
        });

        using var createdPayload = JsonDocument.Parse(result.BodyText);
        Assert.Equal("https://api.jibo.com/media/photo-blob-1",
            createdPayload.RootElement.GetProperty("url").GetString());

        var mediaGet = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "GET",
            Path = "/media/photo-blob-1"
        });

        Assert.Equal(200, mediaGet.StatusCode);
        Assert.Equal("image/jpeg", mediaGet.ContentType);
        Assert.Equal("binary-photo-placeholder", mediaGet.BodyText);
    }

    [Fact]
    public async Task MediaCreate_PersistsBinaryContentThroughFileMediaStore()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Media.Tests", Guid.NewGuid().ToString("N"));
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(),
            new FileMediaContentStore(directoryPath));

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Media_20160725",
            Operation = "Create",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "image/jpeg",
                ["x-path"] = "photo-blob-2",
                ["x-type"] = "image"
            },
            BodyText = "binary-photo-placeholder"
        });

        using var createdPayload = JsonDocument.Parse(result.BodyText);
        Assert.Equal("https://api.jibo.com/media/photo-blob-2",
            createdPayload.RootElement.GetProperty("url").GetString());

        var storedFile = Path.Combine(directoryPath, "photo-blob-2.bin");
        Assert.True(File.Exists(storedFile));

        var mediaGet = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "GET",
            Path = "/media/photo-blob-2"
        });

        Assert.Equal(200, mediaGet.StatusCode);
        Assert.Equal("image/jpeg", mediaGet.ContentType);
        Assert.Equal("binary-photo-placeholder", mediaGet.BodyText);
    }

    [Fact]
    public async Task LogUploads_ArePersistedWithTheirGeneratedUploadId()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Log.Tests", Guid.NewGuid().ToString("N"));
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(),
            new FileMediaContentStore(directoryPath));

        var uploadRequest = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Log_20160715",
            Operation = "PutEventsAsync",
            DeviceId = "robot-log-1"
        });

        using var uploadPayload = JsonDocument.Parse(uploadRequest.BodyText);
        var uploadUrl = uploadPayload.RootElement.GetProperty("uploadUrl").GetString()!;
        var uploadPath = new Uri(uploadUrl).AbsolutePath;
        var upload = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "PUT",
            Path = uploadPath,
            DeviceId = "robot-log-1",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/gzip"
            },
            BodyText = "compressed-event-payload"
        });

        Assert.Equal(200, upload.StatusCode);
        var artifactId = uploadPath.Split('/').Last();
        var contentPath = Path.Combine(directoryPath, "logs", "events", $"{artifactId}.bin");
        var manifestPath = Path.Combine(directoryPath, "logs", "events", $"{artifactId}.json");
        Assert.True(File.Exists(contentPath));
        Assert.Equal("compressed-event-payload", await File.ReadAllTextAsync(contentPath));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Equal("robot-log", manifest.RootElement.GetProperty("meta").GetProperty("artifactType").GetString());
        Assert.Equal("robot-log-1", manifest.RootElement.GetProperty("meta").GetProperty("deviceId").GetString());
        Assert.Equal("application/gzip", manifest.RootElement.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task LogUpload_UnassignedHealthHeaderWithUniqueVerifiedSerial_IsAssigned()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Log.Identity.Tests", Guid.NewGuid().ToString("N"));
        var stateStore = new InMemoryCloudStateStore();
        var device = stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5a41326368dfd00019692602",
            RobotId = "robot-5a41326368dfd00019692602",
            FriendlyName = "OpenJibo Registered Robot",
            VerifiedSerialNumber = "BOJB-1000-0017-0630-0018"
        });
        var suggestions = new RobotIdentitySuggestionStore(stateStore);
        var service = new JiboCloudProtocolService(stateStore, new FileMediaContentStore(directoryPath),
            identitySuggestionStore: suggestions);

        var upload = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com", Method = "PUT", Path = "/upload/log-events/identity-by-serial",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json"
            },
            BodyText = "{\"name\":\"Black-Byte-Cookie-Crinkle\",\"serial_number\":\"BOJB-1000-0017-0630-0018\",\"health\":[]}"
        });

        Assert.Equal(200, upload.StatusCode);
        Assert.Equal("Black-Byte-Cookie-Crinkle", suggestions.GetSuggestion(device.DeviceId)?.ProposedRobotId);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directoryPath, "logs", "events", "identity-by-serial.json")));
        var meta = manifest.RootElement.GetProperty("meta");
        Assert.Equal(device.DeviceId, meta.GetProperty("deviceId").GetString());
        Assert.Equal("log-header-verified-serial", meta.GetProperty("identitySource").GetString());
        Assert.Equal("BOJB-1000-0017-0630-0018", meta.GetProperty("observedSerialNumber").GetString());
        Assert.Equal("Black-Byte-Cookie-Crinkle", meta.GetProperty("observedRobotName").GetString());
    }

    [Fact]
    public async Task LogUpload_AttributedGeneratedDevice_RecordsFriendlyNameSuggestion()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Log.Identity.Tests", Guid.NewGuid().ToString("N"));
        var stateStore = new InMemoryCloudStateStore();
        var device = stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5a41326368dfd00019692602", RobotId = "robot-5a41326368dfd00019692602",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var suggestions = new RobotIdentitySuggestionStore(stateStore);
        var service = new JiboCloudProtocolService(stateStore, new FileMediaContentStore(directoryPath),
            identitySuggestionStore: suggestions);

        await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com", Method = "PUT", Path = "/upload/log-events/identity-suggestion",
            DeviceId = device.DeviceId, Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json"
            },
            BodyText = "{\"name\":\"Black-Byte-Cookie-Crinkle\",\"serial_number\":\"BOJB-1000-0017-0630-0018\",\"health\":[]}"
        });

        Assert.Equal("Black-Byte-Cookie-Crinkle", suggestions.GetSuggestion(device.DeviceId)?.ProposedRobotId);
    }

    [Fact]
    public async Task LogUpload_ConflictingSerialPreservesAttributionAndDoesNotSuggestName()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Log.Identity.Tests", Guid.NewGuid().ToString("N"));
        var stateStore = new InMemoryCloudStateStore();
        var device = stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5a41326368dfd00019692602", RobotId = "robot-5a41326368dfd00019692602",
            FriendlyName = "OpenJibo Registered Robot", VerifiedSerialNumber = "BOJB-1000-0017-0000-0001"
        });
        var suggestions = new RobotIdentitySuggestionStore(stateStore);
        var service = new JiboCloudProtocolService(stateStore, new FileMediaContentStore(directoryPath),
            identitySuggestionStore: suggestions);

        await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com", Method = "PUT", Path = "/upload/log-events/identity-conflict",
            DeviceId = device.DeviceId, Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json"
            },
            BodyText = "{\"name\":\"Black-Byte-Cookie-Crinkle\",\"serial_number\":\"BOJB-1000-0017-0630-0018\",\"health\":[]}"
        });

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directoryPath, "logs", "events", "identity-conflict.json")));
        var meta = manifest.RootElement.GetProperty("meta");
        Assert.Equal(device.DeviceId, meta.GetProperty("deviceId").GetString());
        Assert.True(meta.GetProperty("identityEvidenceConflict").GetBoolean());
        Assert.Null(suggestions.GetSuggestion(device.DeviceId));
    }
    [Fact]
    public async Task PutEvents_PersistsInlineLogPayload()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Log.Tests", Guid.NewGuid().ToString("N"));
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(),
            new FileMediaContentStore(directoryPath));

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Log_20160715",
            Operation = "PutEvents",
            DeviceId = "robot-log-inline",
            BodyText = "[{\"event\":\"boot\"}]"
        });

        Assert.Equal(200, result.StatusCode);
        var file = Directory.EnumerateFiles(Path.Combine(directoryPath, "logs", "events-request"), "*.bin").Single();
        Assert.Equal("[{\"event\":\"boot\"}]", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task LogUploadNegotiation_UsesConfiguredCanonicalApiHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:CanonicalApiBaseUrl"] = "https://api.openjibo.com"
            })
            .Build();
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(), configuration: configuration);

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.openjibo.com",
            Method = "POST",
            ServicePrefix = "Log_20150309",
            Operation = "PutEventsAsync"
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.StartsWith("https://api.openjibo.com/upload/log-events/",
            payload.RootElement.GetProperty("uploadUrl").GetString());
    }

    [Fact]
    public async Task BearerTokenIdentity_IsPersistedForLogAndMediaArtifacts()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Identity.Tests", Guid.NewGuid().ToString("N"));
        var stateStore = new InMemoryCloudStateStore();
        stateStore.GetOrCreateDevice("robot-auth-1", null, null);
        var token = stateStore.IssueRobotToken("robot-auth-1");
        var service = new JiboCloudProtocolService(stateStore, new FileMediaContentStore(directoryPath));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {token}",
            ["Content-Type"] = "application/json"
        };

        await service.DispatchAsync(new ProtocolEnvelope
        {
            Method = "POST",
            ServicePrefix = "Log_20150309",
            Operation = "PutEvents",
            Headers = headers,
            BodyText = "[{\"event\":\"boot\"}]"
        });
        await service.DispatchAsync(new ProtocolEnvelope
        {
            Method = "POST",
            ServicePrefix = "Media_20160725",
            Operation = "Create",
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
            {
                ["x-path"] = "identity-media"
            },
            BodyText = "media-body"
        });

        var logManifest = Directory.EnumerateFiles(Path.Combine(directoryPath, "logs", "events-request"), "*.json").Single();
        using var logDocument = JsonDocument.Parse(await File.ReadAllTextAsync(logManifest));
        using var mediaDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directoryPath, "identity-media.json")));
        Assert.Equal("robot-auth-1", logDocument.RootElement.GetProperty("meta").GetProperty("deviceId").GetString());
        Assert.Equal("bearer-token", logDocument.RootElement.GetProperty("meta").GetProperty("identitySource").GetString());
        Assert.Equal("robot-auth-1", mediaDocument.RootElement.GetProperty("meta").GetProperty("deviceId").GetString());
        Assert.Equal("bearer-token", mediaDocument.RootElement.GetProperty("meta").GetProperty("identitySource").GetString());
    }

    [Fact]
    public async Task HttpTraffic_RecordsIdentitySuggestionWithoutArtifactHistoryScan()
    {
        var stateStore = new InMemoryCloudStateStore();
        stateStore.GetOrCreateDevice("observed-device-001", null, null);
        var token = stateStore.IssueRobotToken("observed-device-001");
        var suggestions = new RobotIdentitySuggestionStore(stateStore);
        var service = new JiboCloudProtocolService(stateStore, identitySuggestionStore: suggestions);

        await service.DispatchAsync(new ProtocolEnvelope
        {
            Method = "POST",
            ServicePrefix = "Loop_20160413",
            Operation = "List",
            DeviceId = "Alpha-Beta-Dodger-Quirk",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {token}"
            },
            BodyText = "{}"
        });

        var suggestion = suggestions.GetSuggestion("observed-device-001");
        Assert.NotNull(suggestion);
        Assert.Equal("Alpha-Beta-Dodger-Quirk", suggestion.ProposedRobotId);
        Assert.Contains(suggestion.Evidence, evidence => evidence.Field == "X-Jibo-RobotId");
    }

    [Fact]
    public void AwsSigV4Identity_IsObservedButDoesNotClaimARobot()
    {
        var resolver = new ProtocolRobotIdentityResolver(new InMemoryCloudStateStore());
        var identity = resolver.Resolve(new ProtocolEnvelope
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "AWS4-HMAC-SHA256 Credential=AKIAEXAMPLEKEY/20260804/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-date, Signature=secret-signature",
                ["X-Amz-Date"] = "20260804T190000Z"
            },
            QueryParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Amz-Security-Token"] = "secret-session-token"
            }
        });

        Assert.False(identity.IsResolved);
        Assert.Equal("unresolved", identity.Source);
        Assert.True(identity.Aws.IsSigV4);
        Assert.Equal("aws4-hmac-sha256", identity.Aws.AuthScheme);
        Assert.True(identity.Aws.DatePresent);
        Assert.True(identity.Aws.SecurityTokenPresent);
        Assert.False(string.IsNullOrWhiteSpace(identity.Aws.AccessKeyFingerprint));
        Assert.DoesNotContain("AKIAEXAMPLEKEY", identity.Aws.AccessKeyFingerprint);
    }

    [Fact]
    public void AwsSigV3Identity_UsesAccessKeyFingerprintWithoutClaimingARobot()
    {
        var resolver = new ProtocolRobotIdentityResolver(new InMemoryCloudStateStore());
        var identity = resolver.Resolve(new ProtocolEnvelope
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "AWS3 AWSAccessKeyId=robot-credential-key,Algorithm=HmacSHA256,SignedHeaders=host;x-jibo-transid,Signature=secret-signature"
            }
        });

        Assert.False(identity.IsResolved);
        Assert.True(identity.Aws.IsSigV3);
        Assert.False(identity.Aws.IsSigV4);
        Assert.Equal("aws3", identity.Aws.AuthScheme);
        Assert.False(string.IsNullOrWhiteSpace(identity.Aws.AccessKeyFingerprint));
        Assert.DoesNotContain("robot-credential-key", identity.Aws.AccessKeyFingerprint);
        Assert.True(identity.Aws.SignaturePresent);
        Assert.True(identity.Aws.SignedHeadersPresent);
        Assert.False(identity.Aws.SignsRobotHeader);
        Assert.True(identity.Aws.SignsTransactionHeader);
    }

    [Fact]
    public void AwsSigV3HttpsIdentity_IsObservedWithoutClaimingARobot()
    {
        var resolver = new ProtocolRobotIdentityResolver(new InMemoryCloudStateStore());
        var identity = resolver.Resolve(new ProtocolEnvelope
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "AWS3-HTTPS AWSAccessKeyId=robot-credential-key,Algorithm=HmacSHA256,Signature=secret-signature"
            }
        });

        Assert.False(identity.IsResolved);
        Assert.True(identity.Aws.IsSigV3);
        Assert.Equal("aws3-https", identity.Aws.AuthScheme);
        Assert.True(identity.Aws.SignaturePresent);
        Assert.False(identity.Aws.SignedHeadersPresent);
    }

    [Fact]
    public void ClaimedAwsCredentialFingerprint_ResolvesTheClaimedRobot()
    {
        var store = new InMemoryCloudStateStore();
        var robot = store.GetOrCreateDevice("Royal-Current-Sage-Canvas", null, null,
            RobotRegistrationSources.Physical);
        var resolver = new ProtocolRobotIdentityResolver(store);
        var envelope = new ProtocolEnvelope
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "AWS4-HMAC-SHA256 Credential=test-access-key/20260804/us-east-1/log/aws4_request, SignedHeaders=host;x-amz-date, Signature=test"
            }
        };
        var observed = resolver.Resolve(envelope);
        store.BindAwsCredentialFingerprint(robot.DeviceId, observed.Aws.AccessKeyFingerprint!, "test-claim");
        var identity = resolver.Resolve(envelope);

        Assert.True(identity.IsResolved);
        Assert.Equal(robot.DeviceId, identity.DeviceId);
        Assert.Equal("aws-credential-binding", identity.Source);
    }

    [Fact]
    public void RobotMerge_MigratesSessionsAndCredentialBindingsAndArchivesSource()
    {
        var store = new InMemoryCloudStateStore();
        var source = store.GetOrCreateDevice("duplicate-robot", null, null, RobotRegistrationSources.Physical);
        var target = store.GetOrCreateDevice("Royal-Current-Sage-Canvas", null, null, RobotRegistrationSources.Physical);
        var token = store.IssueRobotToken(source.DeviceId);
        store.BindAwsCredentialFingerprint(source.DeviceId, "8de2920e0b2874b4", "test-claim");

        var result = store.MergeRobotRecords(source.DeviceId, target.DeviceId);

        Assert.Equal(target.DeviceId, store.FindSessionByToken(token)!.DeviceId);
        Assert.Equal(target.DeviceId, store.FindDeviceByAwsCredentialFingerprint("8de2920e0b2874b4")!.DeviceId);
        Assert.True(store.GetDevices().Single(device => device.DeviceId == source.DeviceId).IsHidden);
        Assert.False(store.GetDevices().Single(device => device.DeviceId == source.DeviceId).IsActive);
        Assert.Equal(1, result.MigratedSessions);
        Assert.Equal(1, result.MigratedCredentialBindings);
    }

    [Fact]
    public void RobotMerge_RejectsActiveRobotAsSource()
    {
        var store = new InMemoryCloudStateStore();
        var active = store.GetRobot();
        var target = store.GetOrCreateDevice("Royal-Current-Sage-Canvas", null, null,
            RobotRegistrationSources.Physical);

        var exception = Assert.Throws<ArgumentException>(() =>
            store.MergeRobotRecords(active.DeviceId, target.DeviceId));

        Assert.Equal("The active robot record must be the canonical target, not the merge source.", exception.Message);
        Assert.Equal(active.DeviceId, store.GetRobot().DeviceId);
    }

    [Fact]
    public async Task MediaCreate_WritesBinaryManifestMetadataForSync()
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Media.Tests", Guid.NewGuid().ToString("N"));
        var service = new JiboCloudProtocolService(new InMemoryCloudStateStore(),
            new FileMediaContentStore(directoryPath));
        const string bodyText = "binary-photo-placeholder";

        var result = await service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Media_20160725",
            Operation = "Create",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "image/jpeg",
                ["x-path"] = "photo-blob-manifest",
                ["x-type"] = "image"
            },
            BodyText = bodyText
        });

        using var createdPayload = JsonDocument.Parse(result.BodyText);
        var meta = createdPayload.RootElement.GetProperty("meta");
        Assert.Equal(bodyText.Length, meta.GetProperty("contentLength").GetInt32());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyText))).ToLowerInvariant(),
            meta.GetProperty("contentSha256").GetString());

        var metaPath = Path.Combine(directoryPath, "photo-blob-manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(metaPath));
        var manifestMeta = manifest.RootElement.GetProperty("meta");
        Assert.Equal(bodyText.Length, manifestMeta.GetProperty("contentLength").GetInt32());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyText))).ToLowerInvariant(),
            manifestMeta.GetProperty("contentSha256").GetString());
        Assert.True(manifestMeta.TryGetProperty("storedUtc", out _));
    }

    [Fact]
    public async Task KeyCreateSymmetricKey_ReturnsKeyPayload()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Key_20160715",
            Operation = "CreateSymmetricKey",
            BodyText = """{"loopId":"openjibo-default-loop"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal("openjibo-default-loop", payload.RootElement.GetProperty("loopId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.RootElement.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task PersonListHolidays_ReturnsHoliday()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Person_20160715",
            Operation = "ListHolidays",
            BodyText = "{}"
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.NotEmpty(payload.RootElement.EnumerateArray());
    }

    [Fact]
    public void InMemoryCloudStateStore_SeedsPeopleForTheDefaultAccountLoop()
    {
        var store = new InMemoryCloudStateStore();

        var people = store.GetPeople();

        Assert.NotEmpty(people);
        Assert.Contains(people, person => person.IsPrimary);
        Assert.Contains(people,
            person => string.Equals(person.AccountId, store.GetAccount().AccountId,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(people,
            person => string.Equals(person.LoopId, store.GetLoops()[0].LoopId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonElement> WaitForSchedulerDownloadDataKindAsync(JsonValueKind expectedKind)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            var result = await _service.DispatchAsync(new ProtocolEnvelope
            {
                HostName = "localhost",
                Method = "POST",
                Path = "/download-status"
            });

            using var payload = JsonDocument.Parse(result.BodyText);
            var root = payload.RootElement.Clone();
            if (root.GetProperty("data").ValueKind == expectedKind || timeout.IsCancellationRequested)
                return root;

            await Task.Delay(100);
        }
    }
}
