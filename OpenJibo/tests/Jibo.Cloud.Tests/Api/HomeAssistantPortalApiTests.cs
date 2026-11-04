using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Media;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jibo.Cloud.Tests.Api;

public sealed class HomeAssistantPortalApiTests
{
    [Fact]
    public async Task StatusSummaryAndMutations_RequireAdminSession()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/portal/status/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/portal/status/robots/any-device/archive", new { hidden = true })).StatusCode);
    }

    [Fact]
    public async Task Links_RequiresPortalSession()
    {
        await using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync("/api/portal/home-assistant/links");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LinkFlow_ConnectsHomeAssistantAndLinksJibo()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var robot = store.GetRobot();
        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = "BOJW-1000-0017-0820-0020",
            RobotId = "Ghost-Instance-Onion-Silk",
            FriendlyName = robot.FriendlyName,
            FirmwareVersion = robot.FirmwareVersion,
            ApplicationVersion = robot.ApplicationVersion,
            HostMappings = new Dictionary<string, string>(robot.HostMappings, StringComparer.OrdinalIgnoreCase)
        });

        var wsClient = factory.Server.CreateWebSocketClient();
        using var haSocket = await wsClient.ConnectAsync(
            new Uri("ws://localhost/v1/homeassistant/ws"),
            CancellationToken.None);

        var registerBytes = Encoding.UTF8.GetBytes("""{"type":"register","instanceId":"ha-test-instance"}""");
        await haSocket.SendAsync(registerBytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var codePayload = await ReadJsonFrameAsync(haSocket);
        Assert.Equal("verification_code", codePayload.GetProperty("type").GetString());

        var haCode = codePayload.GetProperty("code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(haCode));

        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmResponse = await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode });
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        var confirmPayload = await confirmResponse.Content.ReadFromJsonAsync<JsonElement>();
        var portalSessionToken = confirmPayload.GetProperty("portalSessionToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(portalSessionToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", portalSessionToken);

        var dashboardResponse = await client.GetAsync("/api/portal/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
        var dashboardPayload = await dashboardResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Ghost-Instance-Onion-Silk", dashboardPayload.GetProperty("jiboFriendlyId").GetString());
        Assert.False(dashboardPayload.GetProperty("homeAssistant").GetProperty("linked").GetBoolean());

        var linkResponse = await client.PostAsJsonAsync(
            "/api/portal/home-assistant/link",
            new { haCode });
        Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);

        var pairedPayload = await ReadJsonFrameAsync(haSocket);
        Assert.Equal("paired", pairedPayload.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(pairedPayload.GetProperty("commandSecret").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(pairedPayload.GetProperty("linkId").GetString()));

        var linksResponse = await client.GetAsync("/api/portal/home-assistant/links");
        var linksPayload = await linksResponse.Content.ReadFromJsonAsync<JsonElement>();
        var links = linksPayload.GetProperty("links");
        Assert.Equal(1, links.GetArrayLength());
        Assert.True(links[0].GetProperty("connected").GetBoolean());

        await TryCloseAsync(haSocket);
    }

    [Fact]
    public async Task Unlink_RemovesHomeAssistantLink()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var integrationStore = factory.Services.GetRequiredService<IUserIntegrationStore>();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());

        integrationStore.AddHomeAssistantLink(
            "BOJW-1000-0017-0820-0020",
            "Ghost-Instance-Onion-Silk",
            "ha-instance-1");

        var unlinkResponse = await client.DeleteAsync("/api/portal/home-assistant/link");
        Assert.Equal(HttpStatusCode.OK, unlinkResponse.StatusCode);

        Assert.Null(integrationStore.FindLinkForJibo("BOJW-1000-0017-0820-0020", "Ghost-Instance-Onion-Silk"));
    }

    [Fact]
    public async Task Register_ReconnectsAsPaired_WhenLinkAlreadyExists()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var integrationStore = factory.Services.GetRequiredService<IUserIntegrationStore>();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var robot = store.GetRobot();
        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = "BOJW-1000-0017-0820-0020",
            RobotId = "Ghost-Instance-Onion-Silk",
            FriendlyName = robot.FriendlyName,
            FirmwareVersion = robot.FirmwareVersion,
            ApplicationVersion = robot.ApplicationVersion,
            HostMappings = new Dictionary<string, string>(robot.HostMappings, StringComparer.OrdinalIgnoreCase)
        });

        var wsClient = factory.Server.CreateWebSocketClient();
        using var haSocket = await wsClient.ConnectAsync(
            new Uri("ws://localhost/v1/homeassistant/ws"),
            CancellationToken.None);

        await haSocket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"register","instanceId":"ha-test-instance"}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        var codePayload = await ReadJsonFrameAsync(haSocket);
        var haCode = codePayload.GetProperty("code").GetString();

        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());

        var linkResponse = await client.PostAsJsonAsync(
            "/api/portal/home-assistant/link",
            new { haCode });
        var linkPayload = await linkResponse.Content.ReadFromJsonAsync<JsonElement>();
        var linkId = linkPayload.GetProperty("linkId").GetString();

        var pairedPayload = await ReadJsonFrameAsync(haSocket);
        Assert.Equal("paired", pairedPayload.GetProperty("type").GetString());

        await TryCloseAsync(haSocket);

        using var reconnectedSocket = await wsClient.ConnectAsync(
            new Uri("ws://localhost/v1/homeassistant/ws"),
            CancellationToken.None);
        await reconnectedSocket.SendAsync(
            Encoding.UTF8.GetBytes(
                $$"""{"type":"register","instanceId":"ha-test-instance","linkId":"{{linkId}}"}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        var reconnectPayload = await ReadJsonFrameAsync(reconnectedSocket);
        Assert.Equal("paired", reconnectPayload.GetProperty("type").GetString());
        Assert.Equal(linkId, reconnectPayload.GetProperty("linkId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(reconnectPayload.GetProperty("commandSecret").GetString()));

        var link = integrationStore.FindLinkByLinkId(linkId!);
        Assert.NotNull(link);
        Assert.False(string.IsNullOrWhiteSpace(link.CommandSecret));
        Assert.Equal(link.CommandSecret, reconnectPayload.GetProperty("commandSecret").GetString());
        Assert.True(link.LastSeenUtc > link.PairedAtUtc);
    }

    [Fact]
    public async Task Register_RequiresFreshPairing_WhenOnlyInstanceIdIsKnown()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var robot = store.GetRobot();
        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = "BOJW-1000-0017-0820-0020",
            RobotId = "Ghost-Instance-Onion-Silk",
            FriendlyName = robot.FriendlyName,
            FirmwareVersion = robot.FirmwareVersion,
            ApplicationVersion = robot.ApplicationVersion,
            HostMappings = new Dictionary<string, string>(robot.HostMappings, StringComparer.OrdinalIgnoreCase)
        });

        var wsClient = factory.Server.CreateWebSocketClient();
        using var haSocket = await wsClient.ConnectAsync(
            new Uri("ws://localhost/v1/homeassistant/ws"),
            CancellationToken.None);

        await haSocket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"register","instanceId":"ha-known-instance"}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        var codePayload = await ReadJsonFrameAsync(haSocket);
        var haCode = codePayload.GetProperty("code").GetString();

        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());

        await client.PostAsJsonAsync(
            "/api/portal/home-assistant/link",
            new { haCode });

        await ReadJsonFrameAsync(haSocket);
        await TryCloseAsync(haSocket);

        using var reconnectedSocket = await wsClient.ConnectAsync(
            new Uri("ws://localhost/v1/homeassistant/ws"),
            CancellationToken.None);
        await reconnectedSocket.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"register","instanceId":"ha-known-instance"}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);

        var reconnectPayload = await ReadJsonFrameAsync(reconnectedSocket);
        Assert.Equal("verification_code", reconnectPayload.GetProperty("type").GetString());
        Assert.False(reconnectPayload.TryGetProperty("commandSecret", out _));
    }


    [Fact]
    public async Task AdminSummary_ReportsRequiredLegacyHostMappingProof()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var robot = store.GetRobot();
        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = robot.DeviceId,
            RobotId = robot.RobotId,
            FriendlyName = robot.FriendlyName,
            FirmwareVersion = robot.FirmwareVersion,
            ApplicationVersion = robot.ApplicationVersion,
            IsActive = true,
            HostMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api.jibo.com"] = "openjibo.example.test",
                ["api-socket.jibo.com"] = "openjibo.example.test"
            }
        });

        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());

        var response = await client.GetAsync("/api/portal/admin/summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        var conversion = summary.GetProperty("conversion");
        Assert.Equal(3, conversion.GetProperty("requiredHostMappings").GetArrayLength());
        Assert.Contains(conversion.GetProperty("requiredHostMappings").EnumerateArray(), item =>
            item.GetString() == "neo-hub.jibo.com");
        Assert.Contains(conversion.GetProperty("missingHostMappings").EnumerateArray(), item =>
            item.GetString() == "neo-hub.jibo.com");
        Assert.Contains(conversion.GetProperty("blockers").EnumerateArray(), item =>
            item.GetString() == "missing-host-mapping:neo-hub.jibo.com");
    }

    [Fact]
    public async Task StatusLogin_UnlocksPasswordProtectedFleetSummary()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "physical-status-robot",
            RobotId = "physical-status-robot",
            FriendlyName = "Living Room Jibo",
            VerifiedSerialNumber = "BOJW-1000-0017-1114-0008",
            SerialEvidenceSource = "oobe-verified:physical-label",
            SerialEvidenceVerifiedUtc = DateTimeOffset.UtcNow,
            RegistrationSource = RobotRegistrationSources.Physical
        });
        var authHandler = factory.Services.GetRequiredService<ICloudAuthProtocolHandler>();
        authHandler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"live-hub-jibo"}"""
        });
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "archived-live-jibo",
            RobotId = "archived-live-jibo",
            FriendlyName = "Archived Living Room Jibo",
            RegistrationSource = RobotRegistrationSources.Physical,
            IsHidden = true,
            ArchivedUtc = DateTimeOffset.UtcNow
        });
        authHandler.HandleAccount("CreateHubToken", new ProtocolEnvelope
        {
            BodyText = """{"deviceId":"archived-live-jibo"}"""
        });

        var loginResponse = await client.PostAsJsonAsync(
            "/api/portal/status/login",
            new { password = "test-admin-password" });

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginPayload = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loginPayload.GetProperty("portalSessionToken").GetString());

        var summaryResponse = await client.GetAsync("/api/portal/status/summary");

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var summaryText = await summaryResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PasswordHash", summaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PasswordSalt", summaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretAccessKey", summaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"token\":", summaryText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-admin-password", summaryText, StringComparison.Ordinal);
        Assert.True(summary.GetProperty("fleet").GetProperty("registeredRobots").GetInt32() >= 1);
        Assert.Equal(0, summary.GetProperty("fleet").GetProperty("totalSessions").GetInt32());
        Assert.Equal(0, summary.GetProperty("fleet").GetProperty("liveSessions").GetInt32());
        Assert.Empty(summary.GetProperty("recentSessions").EnumerateArray());
        Assert.Equal(2, summary.GetProperty("fleet").GetProperty("hiddenRobots").GetInt32());
        Assert.True(summary.GetProperty("service").GetProperty("uptimeSeconds").GetInt64() >= 0);
        var deployment = summary.GetProperty("service").GetProperty("deployment");
        Assert.Equal("self-hosted-isolated", deployment.GetProperty("mode").GetString());
        Assert.Equal("local", deployment.GetProperty("revision").GetString());
        Assert.StartsWith("local/", deployment.GetProperty("instanceId").GetString());
        Assert.Equal("test.openjibo.local", deployment.GetProperty("canonicalApiHostname").GetString());
        Assert.Equal("File", deployment.GetProperty("stateBackend").GetString());
        Assert.Equal("File", deployment.GetProperty("personalMemoryBackend").GetString());
        Assert.False(deployment.GetProperty("localWhisperEnabled").GetBoolean());
        Assert.True(deployment.GetProperty("managedConfigurationCompatible").GetBoolean());
        Assert.False(deployment.GetProperty("releaseSmokeEnabled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(deployment.GetProperty("persistenceSchemaVersion").GetString()));
        Assert.Contains(summary.GetProperty("robots").EnumerateArray(), robot =>
            robot.GetProperty("deviceId").GetString() == "physical-status-robot" &&
            robot.GetProperty("presence").GetString() == "never-connected" &&
            robot.GetProperty("verifiedSerialNumber").GetString() == "BOJW-1000-0017-1114-0008");
        Assert.DoesNotContain(summary.GetProperty("robots").EnumerateArray(), robot =>
            robot.GetProperty("deviceId").GetString() == "live-hub-jibo");
        Assert.DoesNotContain(summary.GetProperty("robots").EnumerateArray(), robot =>
            robot.GetProperty("deviceId").GetString() == "archived-live-jibo");

        var archiveResponse = await client.PostAsJsonAsync(
            "/api/portal/status/robots/physical-status-robot/archive",
            new { hidden = true });
        Assert.Equal(HttpStatusCode.OK, archiveResponse.StatusCode);

        var archivedSummary = await (await client.GetAsync("/api/portal/status/summary?includeHidden=true"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(archivedSummary.GetProperty("robots").EnumerateArray(), robot =>
            robot.GetProperty("deviceId").GetString() == "physical-status-robot" &&
            robot.GetProperty("isHidden").GetBoolean());

        var remoteServer = store.UpsertTrustedServer(new TrustedServerRecord
        {
            CanonicalHost = "fleet.example.openjibo.com",
            DisplayName = "Fleet server",
            ServerKind = "managed",
            IsActive = true,
            ParticipatesInCloudSync = true
        });
        var reportResponse = await client.PostAsJsonAsync("/api/portal/status/network/reports", new
        {
            serverId = remoteServer.ServerId,
            canonicalHost = remoteServer.CanonicalHost,
            instanceId = "fleet-server-1",
            connectedRobotIds = new[] { "remote-jibo-001", "remote-jibo-002" },
            connectionCount = 3
        });
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);

        var networkSummary = await (await client.GetAsync("/api/portal/status/summary"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, networkSummary.GetProperty("serverFleet").GetProperty("network")
            .GetProperty("connectedRobots").GetInt32());
        Assert.Contains(networkSummary.GetProperty("serverFleet").GetProperty("servers").EnumerateArray(), server =>
            server.GetProperty("canonicalHost").GetString() == remoteServer.CanonicalHost);

        var peerPayload = new FleetPeerPresencePayload(
            remoteServer.ServerId,
            remoteServer.CanonicalHost,
            "fleet-server-2",
            new[] { "remote-jibo-003" },
            1,
            DateTimeOffset.UtcNow);
        var peerPayloadBytes = JsonSerializer.SerializeToUtf8Bytes(peerPayload);
        var peerPayloadHash = Convert.ToHexString(SHA256.HashData(peerPayloadBytes)).ToLowerInvariant();
        var peerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var peerRequest = new HttpRequestMessage(HttpMethod.Post, "/api/network/fleet-presence")
        {
            Content = new ByteArrayContent(peerPayloadBytes)
        };
        peerRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        peerRequest.Headers.Add(FleetPeerSyncAuthentication.ServerIdHeader, remoteServer.ServerId);
        peerRequest.Headers.Add(FleetPeerSyncAuthentication.TimestampHeader, peerTimestamp);
        peerRequest.Headers.Add(FleetPeerSyncAuthentication.PayloadHashHeader, peerPayloadHash);
        peerRequest.Headers.Add(FleetPeerSyncAuthentication.SignatureHeader,
            FleetPeerSyncAuthentication.Sign(remoteServer.ServerId, peerTimestamp, peerPayloadHash, "test-peer-key"));
        var peerResponse = await client.SendAsync(peerRequest);
        Assert.Equal(HttpStatusCode.OK, peerResponse.StatusCode);
    }

    [Fact]
    public async Task StatusSummary_FlagsManagedConfigurationThatEnablesLocalWhisper()
    {
        await using var factory = CreateFactory(
            deploymentMode: "managed",
            localWhisperEnabled: true);
        var client = factory.CreateClient();
        await AuthenticateAdminAsync(client);

        var summary = await (await client.GetAsync("/api/portal/status/summary"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var deployment = summary.GetProperty("service").GetProperty("deployment");

        Assert.Equal("managed", deployment.GetProperty("mode").GetString());
        Assert.True(deployment.GetProperty("localWhisperEnabled").GetBoolean());
        Assert.False(deployment.GetProperty("managedConfigurationCompatible").GetBoolean());
    }

    [Fact]
    public async Task StatusSummary_ShowsOnlyObservedIdentitySuggestionAndApplyClearsIt()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var suggestions = factory.Services.GetRequiredService<RobotIdentitySuggestionStore>();
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "observed-device-001",
            RobotId = "robot-observed-device-001",
            FriendlyName = "OpenJibo Registered Robot"
        });
        suggestions.Observe("observed-device-001", "Alpha-Beta-Dodger-Quirk",
            "websocket-context", "data.runtime.loop.jibo.id");
        await AuthenticateAdminAsync(client);

        var summary = await (await client.GetAsync("/api/portal/status/summary"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var robot = Assert.Single(summary.GetProperty("robots").EnumerateArray(), item =>
            item.GetProperty("deviceId").GetString() == "observed-device-001");
        Assert.Equal("Alpha-Beta-Dodger-Quirk",
            robot.GetProperty("identitySuggestion").GetProperty("proposedRobotId").GetString());

        var apply = await client.PostAsJsonAsync(
            "/api/portal/status/robots/observed-device-001/identity-suggestion/apply",
            new
            {
                proposedRobotId = "Alpha-Beta-Dodger-Quirk",
                expectedAction = "rename",
                expectedTargetDeviceId = (string?)null
            });

        apply.EnsureSuccessStatusCode();
        Assert.Equal("Alpha-Beta-Dodger-Quirk",
            stateStore.GetDevices().Single(item => item.DeviceId == "observed-device-001").RobotId);
        Assert.Null(suggestions.GetSuggestion("observed-device-001"));
    }

    [Fact]
    public async Task IdentitySuggestion_ExistingNameMergesInsteadOfCreatingDuplicate()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var suggestions = factory.Services.GetRequiredService<RobotIdentitySuggestionStore>();
        const string sourceDeviceId = "observed-device-001";
        const string targetDeviceId = "canonical-device-001";
        const string proposedRobotId = "Alpha-Beta-Dodger-Quirk";
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = sourceDeviceId,
            RobotId = "robot-observed-device-001",
            FriendlyName = "OpenJibo Registered Robot",
            RegistrationSource = RobotRegistrationSources.Physical
        });
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = targetDeviceId,
            RobotId = proposedRobotId,
            FriendlyName = proposedRobotId,
            RegistrationSource = RobotRegistrationSources.Physical
        });
        suggestions.Observe(sourceDeviceId, proposedRobotId,
            "websocket-context", "data.runtime.loop.jibo.id");
        await AuthenticateAdminAsync(client);

        var suggestion = await (await client.GetAsync(
                $"/api/portal/status/robots/{sourceDeviceId}/identity-suggestion"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("merge", suggestion.GetProperty("action").GetString());
        Assert.Equal(targetDeviceId, suggestion.GetProperty("targetDeviceId").GetString());

        var apply = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/identity-suggestion/apply",
            new { proposedRobotId, expectedAction = "merge", expectedTargetDeviceId = targetDeviceId });

        apply.EnsureSuccessStatusCode();
        var result = await apply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("merge", result.GetProperty("action").GetString());
        var devices = stateStore.GetDevices();
        var source = devices.Single(device => device.DeviceId == sourceDeviceId);
        Assert.True(source.IsHidden);
        Assert.NotNull(source.ArchivedUtc);
        var activeNamedRobot = Assert.Single(devices, device =>
            !device.IsHidden && device.ArchivedUtc is null &&
            proposedRobotId.Equals(device.RobotId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(targetDeviceId, activeNamedRobot.DeviceId);
        Assert.Null(suggestions.GetSuggestion(sourceDeviceId));
    }

    [Fact]
    public async Task IdentitySuggestion_AmbiguousTargetsCannotBeAppliedAutomatically()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var suggestions = factory.Services.GetRequiredService<RobotIdentitySuggestionStore>();
        const string sourceDeviceId = "observed-device-001";
        const string proposedRobotId = "Alpha-Beta-Dodger-Quirk";
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = sourceDeviceId,
            RobotId = "robot-observed-device-001",
            FriendlyName = "OpenJibo Registered Robot",
            RegistrationSource = RobotRegistrationSources.Physical
        });
        foreach (var targetDeviceId in new[] { "canonical-device-b", "canonical-device-a" })
        {
            stateStore.UpsertDevice(new DeviceRegistration
            {
                DeviceId = targetDeviceId,
                RobotId = proposedRobotId,
                FriendlyName = proposedRobotId,
                RegistrationSource = RobotRegistrationSources.Physical
            });
        }
        suggestions.Observe(sourceDeviceId, proposedRobotId,
            "websocket-context", "data.runtime.loop.jibo.id");
        await AuthenticateAdminAsync(client);

        var suggestion = await (await client.GetAsync(
                $"/api/portal/status/robots/{sourceDeviceId}/identity-suggestion"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ambiguous", suggestion.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, suggestion.GetProperty("targetDeviceId").ValueKind);
        Assert.Equal(
            ["canonical-device-a", "canonical-device-b"],
            suggestion.GetProperty("candidateTargetDeviceIds").EnumerateArray()
                .Select(item => item.GetString()));

        var before = stateStore.GetDevicesForAdministration()
            .Select(device => (device.DeviceId, device.RobotId, device.IsHidden, device.ArchivedUtc))
            .OrderBy(device => device.DeviceId)
            .ToArray();
        var apply = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/identity-suggestion/apply",
            new { proposedRobotId, expectedAction = "ambiguous", expectedTargetDeviceId = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
        Assert.Equal(before, stateStore.GetDevicesForAdministration()
            .Select(device => (device.DeviceId, device.RobotId, device.IsHidden, device.ArchivedUtc))
            .OrderBy(device => device.DeviceId)
            .ToArray());
        Assert.Equal("ambiguous", suggestions.GetSuggestion(sourceDeviceId)!.Action);
    }

    [Fact]
    public async Task IdentitySuggestion_ApplyRejectsTargetChangedAfterReview()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var suggestions = factory.Services.GetRequiredService<RobotIdentitySuggestionStore>();
        const string sourceDeviceId = "observed-device-001";
        const string reviewedTargetDeviceId = "canonical-device-a";
        const string replacementTargetDeviceId = "canonical-device-b";
        const string proposedRobotId = "Alpha-Beta-Dodger-Quirk";
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = sourceDeviceId,
            RobotId = "robot-observed-device-001",
            FriendlyName = "OpenJibo Registered Robot",
            RegistrationSource = RobotRegistrationSources.Physical
        });
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = reviewedTargetDeviceId,
            RobotId = proposedRobotId,
            FriendlyName = proposedRobotId,
            RegistrationSource = RobotRegistrationSources.Physical
        });
        suggestions.Observe(sourceDeviceId, proposedRobotId,
            "websocket-context", "data.runtime.loop.jibo.id");
        await AuthenticateAdminAsync(client);

        var suggestion = await (await client.GetAsync(
                $"/api/portal/status/robots/{sourceDeviceId}/identity-suggestion"))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reviewedTargetDeviceId, suggestion.GetProperty("targetDeviceId").GetString());

        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = reviewedTargetDeviceId,
            RobotId = proposedRobotId,
            FriendlyName = proposedRobotId,
            RegistrationSource = RobotRegistrationSources.Physical,
            IsHidden = true,
            ArchivedUtc = DateTimeOffset.UtcNow
        });
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = replacementTargetDeviceId,
            RobotId = proposedRobotId,
            FriendlyName = proposedRobotId,
            RegistrationSource = RobotRegistrationSources.Physical
        });

        var apply = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/identity-suggestion/apply",
            new
            {
                proposedRobotId,
                expectedAction = "merge",
                expectedTargetDeviceId = reviewedTargetDeviceId
            });

        Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
        var devices = stateStore.GetDevicesForAdministration();
        Assert.False(devices.Single(device => device.DeviceId == sourceDeviceId).IsHidden);
        Assert.False(devices.Single(device => device.DeviceId == replacementTargetDeviceId).IsHidden);
        Assert.NotNull(suggestions.GetSuggestion(sourceDeviceId));
    }

    [Fact]
    public async Task CredentialClaim_BackfillsMatchingUnassignedArtifacts()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var mediaStore = factory.Services.GetRequiredService<IMediaContentStore>();
        const string deviceId = "Royal-Current-Sage-Canvas";
        const string fingerprint = "8de2920e0b2874b4";
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = deviceId,
            RobotId = deviceId,
            FriendlyName = deviceId,
            RegistrationSource = RobotRegistrationSources.Physical
        });
        await mediaStore.StoreAsync("logs/unassigned-sigv4-request.txt", "text/plain", Encoding.UTF8.GetBytes("capture"),
            new Dictionary<string, object?>
            {
                ["awsAccessKeyFingerprint"] = fingerprint,
                ["identitySource"] = "unresolved"
            });

        await AuthenticateAdminAsync(client);
        var response = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{deviceId}/credential-bindings",
            new { accessKeyFingerprint = fingerprint });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("ok").GetBoolean());
        Assert.Equal(1, payload.GetProperty("backfilledArtifacts").GetInt32());
        Assert.Equal(deviceId, stateStore.FindDeviceByAwsCredentialFingerprint(fingerprint)!.DeviceId);

        var artifact = await mediaStore.LoadAsync("logs/unassigned-sigv4-request.txt");
        Assert.NotNull(artifact);
        Assert.Equal(deviceId, artifact.Meta["deviceId"]?.ToString());
        Assert.Equal("aws-credential-binding-backfill", artifact.Meta["identitySource"]?.ToString());
    }

    [Fact]
    public async Task CredentialBindingSwap_SwapsOnlyExplicitClaimsAndBackfillArtifacts()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var mediaStore = factory.Services.GetRequiredService<IMediaContentStore>();
        const string firstRobot = "Royal-Current-Sage-Canvas";
        const string secondRobot = "duplicate-robot";
        const string firstFingerprint = "8de2920e0b2874b4";
        const string secondFingerprint = "0123456789abcdef";
        stateStore.UpsertDevice(new DeviceRegistration { DeviceId = firstRobot, RobotId = firstRobot, FriendlyName = firstRobot });
        stateStore.UpsertDevice(new DeviceRegistration { DeviceId = secondRobot, RobotId = secondRobot, FriendlyName = secondRobot });
        stateStore.BindAwsCredentialFingerprint(firstRobot, firstFingerprint, "test-claim");
        stateStore.BindAwsCredentialFingerprint(secondRobot, secondFingerprint, "test-claim");
        await mediaStore.StoreAsync("logs/first-credential.txt", "text/plain", Encoding.UTF8.GetBytes("first"),
            new Dictionary<string, object?>
            {
                ["deviceId"] = firstRobot, ["awsAccessKeyFingerprint"] = firstFingerprint,
                ["identitySource"] = "aws-credential-binding-backfill"
            });
        await mediaStore.StoreAsync("logs/second-credential.txt", "text/plain", Encoding.UTF8.GetBytes("second"),
            new Dictionary<string, object?>
            {
                ["deviceId"] = secondRobot, ["awsAccessKeyFingerprint"] = secondFingerprint,
                ["identitySource"] = "aws-credential-binding-backfill"
            });

        await AuthenticateAdminAsync(client);
        var response = await client.PostAsJsonAsync("/api/portal/status/credential-bindings/swap", new
        {
            firstAccessKeyFingerprint = firstFingerprint,
            secondAccessKeyFingerprint = secondFingerprint,
            confirmed = true
        });

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, payload.GetProperty("reassignedArtifacts").GetInt32());
        Assert.Equal(secondRobot, stateStore.FindDeviceByAwsCredentialFingerprint(firstFingerprint)!.DeviceId);
        Assert.Equal(firstRobot, stateStore.FindDeviceByAwsCredentialFingerprint(secondFingerprint)!.DeviceId);
        Assert.Equal(secondRobot, (await mediaStore.LoadAsync("logs/first-credential.txt"))!.Meta["deviceId"]?.ToString());
        Assert.Equal(firstRobot, (await mediaStore.LoadAsync("logs/second-credential.txt"))!.Meta["deviceId"]?.ToString());
    }

    [Fact]
    public async Task RobotMerge_RequiresAdminPreviewAndMigratesOnlyIdentityArtifacts()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var mediaStore = factory.Services.GetRequiredService<IMediaContentStore>();
        const string sourceDeviceId = "duplicate-robot";
        const string targetDeviceId = "Royal-Current-Sage-Canvas";
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = sourceDeviceId,
            RobotId = sourceDeviceId,
            FriendlyName = "Duplicate robot",
            RegistrationSource = RobotRegistrationSources.Physical
        });
        stateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = targetDeviceId,
            RobotId = targetDeviceId,
            FriendlyName = targetDeviceId,
            RegistrationSource = RobotRegistrationSources.Physical
        });
        var sourceToken = stateStore.IssueRobotToken(sourceDeviceId);
        stateStore.BindAwsCredentialFingerprint(sourceDeviceId, "8de2920e0b2874b4", "test-claim");
        await mediaStore.StoreAsync("logs/duplicate-request.txt", "text/plain", Encoding.UTF8.GetBytes("capture"),
            new Dictionary<string, object?> { ["deviceId"] = sourceDeviceId });
        var loopIdsBefore = stateStore.GetLoops().Select(loop => loop.LoopId).ToArray();
        var peopleBefore = stateStore.GetPeople().Select(person => person.PersonId).ToArray();

        await AuthenticateAdminAsync(client);
        var previewResponse = await client.GetAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/merge-preview?targetDeviceId={targetDeviceId}");
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, preview.GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, preview.GetProperty("credentialBindingCount").GetInt32());
        Assert.Equal(1, preview.GetProperty("artifactCount").GetInt32());
        var previewToken = preview.GetProperty("previewToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(previewToken));

        var mergeResponse = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/merge", new { targetDeviceId, previewToken });
        mergeResponse.EnsureSuccessStatusCode();
        var merge = await mergeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(merge.GetProperty("ok").GetBoolean());
        Assert.Equal(1, merge.GetProperty("migratedArtifacts").GetInt32());
        Assert.Equal(1, merge.GetProperty("result").GetProperty("migratedSessions").GetInt32());
        Assert.Equal(1, merge.GetProperty("result").GetProperty("migratedCredentialBindings").GetInt32());

        Assert.Equal(targetDeviceId, stateStore.FindSessionByToken(sourceToken)!.DeviceId);
        Assert.Equal(targetDeviceId, stateStore.FindDeviceByAwsCredentialFingerprint("8de2920e0b2874b4")!.DeviceId);
        Assert.True(stateStore.GetDevices().Single(device => device.DeviceId == sourceDeviceId).IsHidden);
        var artifact = await mediaStore.LoadAsync("logs/duplicate-request.txt");
        Assert.Equal(targetDeviceId, artifact!.Meta["deviceId"]?.ToString());
        Assert.Equal("robot-merge", artifact.Meta["identitySource"]?.ToString());
        Assert.Equal(sourceDeviceId, artifact.Meta["mergedFromDeviceId"]?.ToString());
        Assert.Equal(loopIdsBefore, stateStore.GetLoops().Select(loop => loop.LoopId));
        Assert.Equal(peopleBefore, stateStore.GetPeople().Select(person => person.PersonId));
    }

    [Fact]
    public async Task RobotMerge_RejectsMissingOrCrossTargetPreviewWithoutMutation()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        const string sourceDeviceId = "duplicate-robot";
        const string reviewedTargetDeviceId = "canonical-a";
        const string otherTargetDeviceId = "canonical-b";
        foreach (var deviceId in new[] { sourceDeviceId, reviewedTargetDeviceId, otherTargetDeviceId })
        {
            stateStore.UpsertDevice(new DeviceRegistration
            {
                DeviceId = deviceId,
                RobotId = deviceId,
                FriendlyName = deviceId,
                RegistrationSource = RobotRegistrationSources.Physical
            });
        }
        await AuthenticateAdminAsync(client);

        var missingPreview = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/merge",
            new { targetDeviceId = reviewedTargetDeviceId });
        Assert.Equal(HttpStatusCode.Conflict, missingPreview.StatusCode);

        var preview = await (await client.GetAsync(
                $"/api/portal/status/robots/{sourceDeviceId}/merge-preview?targetDeviceId={reviewedTargetDeviceId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var crossTarget = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/merge",
            new
            {
                targetDeviceId = otherTargetDeviceId,
                previewToken = preview.GetProperty("previewToken").GetString()
            });

        Assert.Equal(HttpStatusCode.Conflict, crossTarget.StatusCode);
        Assert.False(stateStore.GetDevicesForAdministration()
            .Single(device => device.DeviceId == sourceDeviceId).IsHidden);
    }

    [Fact]
    public async Task RobotMerge_RejectsArtifactChangedAfterPreviewWithoutMutation()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var stateStore = factory.Services.GetRequiredService<ICloudStateStore>();
        var mediaStore = factory.Services.GetRequiredService<IMediaContentStore>();
        const string sourceDeviceId = "duplicate-robot";
        const string targetDeviceId = "canonical-robot";
        foreach (var deviceId in new[] { sourceDeviceId, targetDeviceId })
        {
            stateStore.UpsertDevice(new DeviceRegistration
            {
                DeviceId = deviceId,
                RobotId = deviceId,
                FriendlyName = deviceId,
                RegistrationSource = RobotRegistrationSources.Physical
            });
        }
        await mediaStore.StoreAsync("logs/merge-race.txt", "text/plain", Encoding.UTF8.GetBytes("reviewed"),
            new Dictionary<string, object?> { ["deviceId"] = sourceDeviceId });
        await AuthenticateAdminAsync(client);

        var preview = await (await client.GetAsync(
                $"/api/portal/status/robots/{sourceDeviceId}/merge-preview?targetDeviceId={targetDeviceId}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        await mediaStore.StoreAsync("logs/merge-race.txt", "text/plain", Encoding.UTF8.GetBytes("changed"),
            new Dictionary<string, object?> { ["deviceId"] = sourceDeviceId });
        var merge = await client.PostAsJsonAsync(
            $"/api/portal/status/robots/{sourceDeviceId}/merge",
            new
            {
                targetDeviceId,
                previewToken = preview.GetProperty("previewToken").GetString()
            });

        Assert.Equal(HttpStatusCode.Conflict, merge.StatusCode);
        Assert.False(stateStore.GetDevicesForAdministration()
            .Single(device => device.DeviceId == sourceDeviceId).IsHidden);
        Assert.Equal(sourceDeviceId,
            (await mediaStore.LoadAsync("logs/merge-race.txt"))!.Meta["deviceId"]?.ToString());
    }

    [Fact]
    public async Task IdentityGraphEndpoint_ReturnsSignedEvidencePayloadForPortalSession()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());

        var graphResponse = await client.GetAsync("/api/portal/identity-graph");

        Assert.Equal(HttpStatusCode.OK, graphResponse.StatusCode);
        var graph = await graphResponse.Content.ReadFromJsonAsync<JsonElement>();
        var contentHash = graph.GetProperty("contentHash").GetString();
        Assert.Matches("^[a-f0-9]{64}$", contentHash);
        Assert.Equal("HMAC-SHA256", graph.GetProperty("signatureAlgorithm").GetString());
        Assert.Equal("open-jibo-local-snapshot-v1", graph.GetProperty("signatureKeyId").GetString());
        Assert.Equal(
            $"1|{graph.GetProperty("accountId").GetString()}|{graph.GetProperty("loopId").GetString()}|{contentHash}",
            graph.GetProperty("signaturePayload").GetString());
        Assert.Matches("^[a-f0-9]{64}$", graph.GetProperty("signature").GetString());
        var admissionAssessment = graph.GetProperty("admissionAssessment");
        Assert.Equal("deny-by-evidence-v1", admissionAssessment.GetProperty("policyVersion").GetString());
        Assert.Equal("quarantine", admissionAssessment.GetProperty("recommendation").GetString());
        var evidenceBundle = graph.GetProperty("evidenceBundle");
        Assert.Equal("identity-graph-evidence-bundle-v1", evidenceBundle.GetProperty("bundleVersion").GetString());
        Assert.Equal(contentHash, evidenceBundle.GetProperty("snapshotContentHash").GetString());
        Assert.Equal(admissionAssessment.GetProperty("decisionHash").GetString(),
            evidenceBundle.GetProperty("admissionDecisionHash").GetString());
        Assert.Equal("quarantine", evidenceBundle.GetProperty("admissionRecommendation").GetString());
        Assert.Contains($"snapshot-content-hash|{contentHash}", evidenceBundle.GetProperty("payload").GetString());
        Assert.Matches("^[a-f0-9]{64}$", evidenceBundle.GetProperty("bundleHash").GetString());
        Assert.Matches("^[a-f0-9]{64}$", evidenceBundle.GetProperty("signature").GetString());
        Assert.Contains("envelope-version|identity-graph-evidence-envelope-v1",
            evidenceBundle.GetProperty("envelope").GetString());
        Assert.Contains($"bundle-signature|{evidenceBundle.GetProperty("signature").GetString()}",
            evidenceBundle.GetProperty("envelope").GetString());
        Assert.Contains(admissionAssessment.GetProperty("satisfiedEvidence").EnumerateArray(), item =>
            item.GetString() == "device-id");
        Assert.Contains(admissionAssessment.GetProperty("blockingEvidence").EnumerateArray(), item =>
            item.GetString() == "required:application-version");
        Assert.Contains(admissionAssessment.GetProperty("recommendedActions").EnumerateArray(), item =>
            item.GetString() == "capture-current-open-jibo-application-version");
        Assert.NotEmpty(graph.GetProperty("relationships").EnumerateArray());
        Assert.Contains(graph.GetProperty("evidenceSignals").EnumerateArray(), signal =>
            signal.GetProperty("signalKind").GetString() == "device-id" &&
            signal.GetProperty("value").GetString() == "BOJW-1000-0017-0820-0020");
    }

    [Fact]
    public async Task IdentityGraphRevocationEndpoint_QuarantinesSignedPeerAdmissionPayload()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());

        var revokeResponse = await client.PostAsJsonAsync(
            "/api/portal/identity-graph/revocations",
            new { anchor = "device-id:BOJW-1000-0017-0820-0020=BOJW-1000-0017-0820-0020" });

        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        var revoked = await revokeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(revoked.GetProperty("revoked").GetBoolean());
        Assert.Equal("device-id:BOJW-1000-0017-0820-0020=BOJW-1000-0017-0820-0020",
            revoked.GetProperty("anchor").GetString());
        var admissionAssessment = revoked.GetProperty("admissionAssessment");
        Assert.Equal("quarantine", admissionAssessment.GetProperty("recommendation").GetString());
        Assert.Contains(admissionAssessment.GetProperty("revocationChecks").EnumerateArray(), item =>
            item.GetString() == "local-revocation-match:device-id:BOJW-1000-0017-0820-0020=BOJW-1000-0017-0820-0020");
        Assert.Contains(admissionAssessment.GetProperty("blockingEvidence").EnumerateArray(), item =>
            item.GetString() == "revoked:device-id:BOJW-1000-0017-0820-0020=BOJW-1000-0017-0820-0020");
        Assert.Contains(admissionAssessment.GetProperty("recommendedActions").EnumerateArray(), item =>
            item.GetString() == "keep-revoked-identity-anchor-quarantined");
        var evidenceBundle = revoked.GetProperty("evidenceBundle");
        Assert.Equal(admissionAssessment.GetProperty("decisionHash").GetString(),
            evidenceBundle.GetProperty("admissionDecisionHash").GetString());
        Assert.Contains(
            "admission-revocation-checks|local-revocation-match:device-id:BOJW-1000-0017-0820-0020=BOJW-1000-0017-0820-0020",
            evidenceBundle.GetProperty("payload").GetString());
    }

    [Fact]
    public async Task IdentityGraphEvidenceBundleEndpoint_ReturnsDownloadablePeerAdmissionPayload()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());

        var bundleResponse = await client.GetAsync("/api/portal/identity-graph/evidence-bundle");

        Assert.Equal(HttpStatusCode.OK, bundleResponse.StatusCode);
        Assert.Equal("text/plain", bundleResponse.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("openjibo-identity-evidence-BOJW-1000-0017-0820-0020-",
            bundleResponse.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var payload = await bundleResponse.Content.ReadAsStringAsync();
        Assert.Contains("envelope-version|identity-graph-evidence-envelope-v1", payload);
        Assert.Contains("bundle-signature|", payload);
        Assert.Contains("payload-begin", payload);
        Assert.Contains("bundle-version|identity-graph-evidence-bundle-v1", payload);
        Assert.Contains("device|BOJW-1000-0017-0820-0020", payload);
        Assert.Contains("admission-recommendation|quarantine", payload);
        Assert.Contains("admission-decision-hash|", payload);
        Assert.Contains("payload-end", payload);
    }


    [Fact]
    public async Task IdentityGraphEvidenceBundleVerifyEndpoint_ReturnsOfflineAdmissionReview()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = "BOJW-1000-0017-0820-0020",
            RobotId = "Ghost-Instance-Onion-Silk",
            ApplicationVersion = "1.0.20",
            HostMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["neo-hub.jibo.com"] = "openjibo.local"
            }
        });
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
        var graph = store.GetIdentityGraph();

        var verifyResponse = await client.PostAsJsonAsync(
            "/api/portal/identity-graph/evidence-bundle/verify",
            new
            {
                envelope = graph.EvidenceBundle.Envelope,
                localRevokedAnchors = new[]
                {
                    "device-id:BOJW-1000-0017-0820-0020=BOJW-1000-0017-0820-0020"
                }
            });

        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verification = await verifyResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(verification.GetProperty("isValid").GetBoolean());
        Assert.False(verification.GetProperty("isLocallyAdmissible").GetBoolean());
        Assert.Equal("admit", verification.GetProperty("admissionRecommendation").GetString());
        Assert.Equal("quarantine", verification.GetProperty("effectiveAdmissionRecommendation").GetString());
        Assert.Equal("peer-admission-retention", verification.GetProperty("trustPurpose").GetString());
        Assert.Equal("not-enabled", verification.GetProperty("peerTransportStatus").GetString());
        Assert.Equal("snapshot-retention-only", verification.GetProperty("syncDirection").GetString());
        Assert.False(verification.GetProperty("directPeerTransportAllowed").GetBoolean());
        Assert.Equal("Ghost-Instance-Onion-Silk", verification.GetProperty("robotId").GetString());
        Assert.Contains(verification.GetProperty("localRevocationMatches").EnumerateArray(), item =>
            item.GetString() == "device-id:BOJW-1000-0017-0820-0020=BOJW-1000-0017-0820-0020");
        Assert.True(verification.GetProperty("admissionDecisionSignatureValid").GetBoolean());
        Assert.True(verification.GetProperty("snapshotSignatureValid").GetBoolean());
        Assert.Empty(verification.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task TrustedServerDirectoryEndpoint_ReturnsRegistryBackedHostedOptions()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await AuthenticateAdminAsync(client);

        var response = await client.GetAsync("/api/onboarding/trusted-servers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var directory = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(directory.GetProperty("allowCustomEntry").GetBoolean());
        Assert.True(directory.GetProperty("hostedHttpsRequired").GetBoolean());
        Assert.Equal("api.openjibo.com", directory.GetProperty("trustedRootHost").GetString());

        var servers = directory.GetProperty("servers");
        Assert.Contains(servers.EnumerateArray(), server =>
            server.GetProperty("canonicalHost").GetString() == "api.openjibo.com" &&
            server.GetProperty("isTrustRoot").GetBoolean() &&
            server.GetProperty("requiresHttps").GetBoolean());
    }

    [Fact]
    public async Task TrustedServerRegistryPersistsNewHostedServers()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
        await AuthenticateAdminAsync(client);

        var registerResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers",
            new
            {
                canonicalHost = "api.example.openjibo.com",
                displayName = "Example Open Jibo Server",
                serverKind = "managed",
                requiresHttps = true,
                isActive = true,
                description = "Operator-added hosted server."
            });

        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        Assert.NotNull(store.FindTrustedServer("api.example.openjibo.com"));

        var directoryResponse = await client.GetAsync("/api/onboarding/trusted-servers");
        var directory = await directoryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(directory.GetProperty("servers").EnumerateArray(), server =>
            server.GetProperty("canonicalHost").GetString() == "api.example.openjibo.com" &&
            server.GetProperty("displayName").GetString() == "Example Open Jibo Server" &&
            server.GetProperty("serverKind").GetString() == "managed");
    }

    [Fact]
    public async Task TrustedServerRegistryAllowsHybridServersButRejectsSelfHosted()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
        await AuthenticateAdminAsync(client);

        var hybridResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers",
            new
            {
                canonicalHost = "hybrid.example.openjibo.com",
                displayName = "Hybrid Open Jibo Server",
                serverKind = "hybrid",
                requiresHttps = true,
                isListed = true,
                acceptsPublicConnections = false,
                participatesInCloudSync = true,
                isActive = true,
                description = "Private cloud-synced hosted server."
            });

        Assert.Equal(HttpStatusCode.OK, hybridResponse.StatusCode);

        var hybridPayload = await hybridResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hybrid", hybridPayload.GetProperty("trustedServer").GetProperty("serverKind").GetString());
        Assert.False(hybridPayload.GetProperty("trustedServer").GetProperty("acceptsPublicConnections").GetBoolean());

        var selfHostedResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers",
            new
            {
                canonicalHost = "selfhosted.example.local",
                displayName = "Local Self-Hosted",
                serverKind = "self-hosted",
                requiresHttps = false,
                isActive = true,
                description = "Should not enter the trusted registry."
            });

        Assert.Equal(HttpStatusCode.BadRequest, selfHostedResponse.StatusCode);
    }

    [Fact]
    public async Task TrustedServerLifecycleEndpoint_RevokeAndReactivateManagedServer()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
        await AuthenticateAdminAsync(client);

        await client.PostAsJsonAsync(
            "/api/portal/trusted-servers",
            new
            {
                canonicalHost = "managed.example.openjibo.com",
                displayName = "Managed Example",
                serverKind = "managed",
                requiresHttps = true,
                isActive = true,
                description = "Managed hosted server."
            });

        var revokeResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers/lifecycle",
            new
            {
                canonicalHost = "managed.example.openjibo.com",
                action = "revoke"
            });

        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);
        var revokedPayload = await revokeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(revokedPayload.GetProperty("trustedServer").GetProperty("isActive").GetBoolean());
        Assert.False(revokedPayload.GetProperty("trustedServer").GetProperty("isListed").GetBoolean());

        var storedRevoked = store.FindTrustedServer("managed.example.openjibo.com");
        Assert.NotNull(storedRevoked);
        Assert.False(storedRevoked!.IsActive);

        var reactivateResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers/lifecycle",
            new
            {
                canonicalHost = "managed.example.openjibo.com",
                action = "reactivate"
            });

        Assert.Equal(HttpStatusCode.OK, reactivateResponse.StatusCode);
        var reactivatedPayload = await reactivateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(reactivatedPayload.GetProperty("trustedServer").GetProperty("isActive").GetBoolean());
        Assert.True(reactivatedPayload.GetProperty("trustedServer").GetProperty("isListed").GetBoolean());
    }

    [Fact]
    public async Task TrustedServerLifecycleEndpoint_RejectsTrustRootRevocation()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
        await AuthenticateAdminAsync(client);

        var revokeResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers/lifecycle",
            new
            {
                canonicalHost = "api.openjibo.com",
                action = "revoke"
            });

        Assert.Equal(HttpStatusCode.BadRequest, revokeResponse.StatusCode);
    }

    [Fact]
    public async Task TrustedServerAdmissionCreatesSignedAuditRecord()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
        await AuthenticateAdminAsync(client);

        var registerResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers",
            new
            {
                canonicalHost = "audit.example.openjibo.com",
                displayName = "Audit Example",
                serverKind = "managed",
                reason = "Operator-approved hosted server.",
                requiresHttps = true,
                isActive = true,
                description = "Signed admission audit test."
            });

        Assert.Equal(HttpStatusCode.OK, registerResponse.StatusCode);
        var payload = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var admission = payload.GetProperty("admissionRecord");
        Assert.Equal("admit", admission.GetProperty("action").GetString());
        Assert.Equal("audit.example.openjibo.com", admission.GetProperty("canonicalHost").GetString());
        Assert.Equal("HMAC-SHA256", admission.GetProperty("signatureAlgorithm").GetString());
        Assert.Equal("open-jibo-local-trusted-server-admission-v1", admission.GetProperty("signatureKeyId").GetString());
        Assert.Matches("^[a-f0-9]{64}$", admission.GetProperty("signature").GetString());
        Assert.Contains("action|admit", admission.GetProperty("payload").GetString());
    }

    [Fact]
    public async Task SelfHostedValidationEndpoint_ReturnsModeSpecificGuidance()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        await AuthenticateAdminAsync(client);

        var localResponse = await client.PostAsJsonAsync(
            "/api/onboarding/self-hosted/validate",
            new
            {
                serverMode = "self-hosted",
                serverHost = "localhost:8080"
            });

        Assert.Equal(HttpStatusCode.OK, localResponse.StatusCode);
        var localPayload = await localResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(localPayload.GetProperty("allowsHttp").GetBoolean());
        Assert.False(localPayload.GetProperty("requiresHttps").GetBoolean());
        Assert.Equal("self-hosted", localPayload.GetProperty("serverMode").GetString());

        var publicIpResponse = await client.PostAsJsonAsync(
            "/api/onboarding/self-hosted/validate",
            new
            {
                serverMode = "self-hosted",
                serverHost = "203.0.113.10"
            });

        Assert.Equal(HttpStatusCode.OK, publicIpResponse.StatusCode);
        var publicIpPayload = await publicIpResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(publicIpPayload.GetProperty("isLocalTarget").GetBoolean());
        Assert.False(publicIpPayload.GetProperty("allowsHttp").GetBoolean());

        var hybridResponse = await client.PostAsJsonAsync(
            "/api/onboarding/self-hosted/validate",
            new
            {
                serverMode = "self-hosted-hybrid",
                serverHost = "hybrid.example.openjibo.com"
            });

        Assert.Equal(HttpStatusCode.OK, hybridResponse.StatusCode);
        var hybridPayload = await hybridResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(hybridPayload.GetProperty("allowsHttp").GetBoolean());
        Assert.True(hybridPayload.GetProperty("requiresHttps").GetBoolean());
        Assert.Equal("self-hosted-hybrid", hybridPayload.GetProperty("serverMode").GetString());
    }

    [Fact]
    public async Task TrustedServerAdmissionsExportEndpoint_ReturnsDownloadableAuditFile()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var spokenCode =
            verificationService.IssueCodeForDevice("Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");

        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
        await AuthenticateAdminAsync(client);

        var admissionResponse = await client.PostAsJsonAsync(
            "/api/portal/trusted-servers",
            new
            {
                canonicalHost = "export.example.openjibo.com",
                displayName = "Export Example",
                serverKind = "managed",
                reason = "Signed export test.",
                requiresHttps = true,
                isActive = true,
                description = "Exportable audit record."
            });
        Assert.Equal(HttpStatusCode.OK, admissionResponse.StatusCode);
        var admissionBody = await admissionResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"admissionRecord\"", admissionBody);
        Assert.Contains("\"action\":\"admit\"", admissionBody);

        var exportResponse = await client.GetAsync("/api/portal/trusted-servers/admissions/export");

        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        Assert.Equal("application/json", exportResponse.Content.Headers.ContentType?.MediaType);
        var exportBody = await exportResponse.Content.ReadAsStringAsync();
        Assert.Contains("\"exportedBy\": \"Portal Admin\"", exportBody);
        Assert.Contains("\"CanonicalHost\": \"export.example.openjibo.com\"", exportBody);
        Assert.Contains("\"Action\": \"admit\"", exportBody);
        Assert.Contains("\"SignatureKeyId\": \"open-jibo-local-trusted-server-admission-v1\"", exportBody);
    }

    [Fact]
    public async Task StatusSummary_LeavesUnclaimedDuplicateRecordSeparateFromExplicitlyLinkedRobot()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        var placeholder = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5c0b221fdf9d450019c5e254",
            RobotId = "robot-5c0b221fdf9d450019c5e254",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var verified = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "robot-Royal-Current-Sage-Canvas",
            FriendlyName = "Royal-Current-Sage-Canvas"
        });

        var session = store.OpenSession("api-socket", placeholder.DeviceId, "token-test", "api-socket", "/token-test");
        session.Metadata["registeredDeviceId"] = verified.DeviceId;
        session.Metadata["registeredRobotId"] = verified.RobotId;
        session.LastSeenUtc = DateTimeOffset.UtcNow.AddSeconds(-5);

        await AuthenticateAdminAsync(client);

        var summaryResponse = await client.GetAsync("/api/portal/status/summary");

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rows = summary.GetProperty("robots")
            .EnumerateArray()
            .Where(robot =>
                robot.GetProperty("deviceId").GetString() is "5c0b221fdf9d450019c5e254" or "Royal-Current-Sage-Canvas" ||
                robot.GetProperty("robotId").GetString() is "robot-5c0b221fdf9d450019c5e254" or "robot-Royal-Current-Sage-Canvas")
            .ToArray();

        Assert.Equal(2, rows.Length);
        var linkedRobot = Assert.Single(rows, robot =>
            robot.GetProperty("deviceId").GetString() == "Royal-Current-Sage-Canvas");
        Assert.Equal("robot-Royal-Current-Sage-Canvas", linkedRobot.GetProperty("robotId").GetString());
        Assert.Equal("online", linkedRobot.GetProperty("presence").GetString());
        Assert.True(linkedRobot.GetProperty("connected").GetBoolean());
        var unclaimedDuplicate = Assert.Single(rows, robot =>
            robot.GetProperty("deviceId").GetString() == "5c0b221fdf9d450019c5e254");
        Assert.Equal("never-connected", unclaimedDuplicate.GetProperty("presence").GetString());
    }

    [Fact]
    public async Task StatusSummary_ListsAndUnlinksStaleExplicitSessionBinding()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var robot = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "robot-Royal-Current-Sage-Canvas",
            FriendlyName = "Royal-Current-Sage-Canvas"
        });
        var session = store.OpenSession("neo-hub-listen", "wrong-runtime-token", "hub-stale", "neo-hub-listen", "/v1/listen");
        Assert.True(store.BindSessionToDevice(session.SessionId, robot.DeviceId));
        session.LastSeenUtc = DateTimeOffset.UtcNow.AddHours(-2);

        await AuthenticateAdminAsync(client);
        var summaryResponse = await client.GetAsync("/api/portal/status/summary");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var binding = Assert.Single(summary.GetProperty("explicitSessionBindings").EnumerateArray(), item =>
            item.GetProperty("sessionId").GetString() == session.SessionId);
        Assert.True(binding.GetProperty("isStale").GetBoolean());
        Assert.Equal("wrong-runtime-token", binding.GetProperty("deviceId").GetString());

        var unlinkResponse = await client.DeleteAsync($"/api/portal/status/sessions/{session.SessionId}/link");
        Assert.Equal(HttpStatusCode.OK, unlinkResponse.StatusCode);
        Assert.False(session.Metadata.ContainsKey("registeredDeviceId"));
        Assert.Equal("wrong-runtime-token", session.DeviceId);
    }

    [Fact]
    public async Task LinkLiveSession_UsesObservedIdentityWhenSessionLivesOnAnotherReplica()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "cross-replica-observed", RobotId = "robot-cross-replica-observed",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var target = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "cross-replica-target", RobotId = "robot-cross-replica-target",
            FriendlyName = "Cross Replica Target"
        });
        var remoteSessionId = Guid.NewGuid().ToString("N");
        var lastSeenUtc = DateTimeOffset.UtcNow;

        await AuthenticateAdminAsync(client);
        var response = await client.PostAsJsonAsync(
            $"/api/portal/status/sessions/{remoteSessionId}/link",
            new
            {
                deviceId = target.DeviceId,
                observedDeviceId = "cross-replica-observed",
                lastSeenUtc
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reconnect = store.OpenSession("neo-hub-proactive", "cross-replica-observed",
            "proactive", "neo-hub", "/v1/proactive");
        Assert.Equal(target.DeviceId, reconnect.Metadata["registeredDeviceId"]?.ToString());
    }

    [Fact]
    public async Task FleetPresence_WhenPeerSyncIsDisabled_FailsClosed()
    {
        await using var factory = CreateFactory(peerSyncEnabled: false);
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/network/fleet-presence", new FleetPeerPresencePayload(
            "remote-server", "fleet.example.openjibo.com", "instance", [], 0, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task IdentityCleanup_PreviewsAndResetsHistoricalAssociations()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        store.UpsertDevice(new DeviceRegistration { DeviceId = "cleanup-source", RobotId = "robot-cleanup-source" });
        store.UpsertDevice(new DeviceRegistration { DeviceId = "cleanup-target", RobotId = "Cleanup-Target-Robot" });
        store.OpenSession("hub", "cleanup-source", "conn:cleanup", "neohub", "/v1/listen");
        store.MergeRobotRecords("cleanup-source", "cleanup-target");
        store.IssueRobotToken("cleanup-target");

        await AuthenticateAdminAsync(client);
        var previewResponse = await client.GetAsync("/api/portal/status/identity-cleanup");
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preview.GetProperty("mergeRelationshipCount").GetInt32() >= 1);
        Assert.True(preview.GetProperty("explicitSessionBindingCount").GetInt32() >= 1);
        Assert.True(preview.GetProperty("authenticationSessionCount").GetInt32() >= 1);

        var resetResponse = await client.PostAsJsonAsync(
            "/api/portal/status/identity-cleanup/reset", new { confirmed = true });
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        var restored = store.GetDevices().Single(device => device.DeviceId == "cleanup-source");
        Assert.False(restored.IsHidden);
        Assert.Null(restored.ArchivedUtc);
        Assert.DoesNotContain(store.GetSessions(), session =>
            !string.IsNullOrWhiteSpace(session.Token) && session.Token.StartsWith("token-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestoreIdentityFromDeviceId_RepairsMismatchedNamedRecordWithoutMerging()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "Coral-Watt-Serrano-Woven",
            FriendlyName = "Coral-Watt-Serrano-Woven"
        });

        await AuthenticateAdminAsync(client);
        var response = await client.PostAsJsonAsync(
            "/api/portal/status/robots/Royal-Current-Sage-Canvas/identity/restore-from-device-id",
            new { confirmed = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var repaired = store.GetDevices().Single(device => device.DeviceId == "Royal-Current-Sage-Canvas");
        Assert.Equal("Royal-Current-Sage-Canvas", repaired.RobotId);
        Assert.Equal("Royal-Current-Sage-Canvas", repaired.FriendlyName);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, "BOJB-1000-0017-0630-0018", true)]
    [InlineData(true, "BOJB-1000-0000-0000-0000", false)]
    [InlineData(true, null, true)]
    public async Task IdentitySuggestion_RecognizesHealthHeaderWithoutGuessingUnassignedOwnership(
        bool attributed, string? verifiedSerial, bool expectedSuggestion)
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var device = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5a41326368dfd00019692602",
            RobotId = "robot-5a41326368dfd00019692602",
            FriendlyName = "OpenJibo Registered Robot",
            VerifiedSerialNumber = verifiedSerial
        });
        var media = factory.Services.GetRequiredService<IMediaContentStore>();
        await media.StoreAsync("logs/events/health-header", "application/json",
            Encoding.UTF8.GetBytes("{\"system_clock\":1788905276226939674,\"name\":\"Black-Byte-Cookie-Crinkle\",\"serial_number\":\"BOJB-1000-0017-0630-0018\",\"health\":[{"),
            new Dictionary<string, object?> { ["deviceId"] = attributed ? device.DeviceId : null });
        await AuthenticateAdminAsync(client);

        var response = await client.GetAsync($"/api/portal/status/robots/{device.DeviceId}/identity-suggestion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedSuggestion, payload.GetProperty("suggested").GetBoolean());
        if (expectedSuggestion)
            Assert.Equal("Black-Byte-Cookie-Crinkle", payload.GetProperty("proposedRobotId").GetString());
        if (!attributed)
        {
            var evidence = payload.GetProperty("unassignedEvidence").EnumerateArray().ToArray();
            Assert.Contains(evidence, item => item.GetProperty("value").GetString() == "Black-Byte-Cookie-Crinkle");
            Assert.Contains(evidence, item => item.GetProperty("value").GetString() == "BOJB-1000-0017-0630-0018");
            // A read-time suggestion must not silently claim an upload or shared credential.
            Assert.Null((await media.LoadAsync("logs/events/health-header"))!.Meta["deviceId"]);
        }
    }
    [Fact]
    public async Task IdentitySuggestion_InspectsArtifactPayloadWhenManifestHasNoRobotName()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var placeholder = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "runtime-with-generic-name",
            RobotId = "robot-runtime-with-generic-name",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var mediaStore = factory.Services.GetRequiredService<IMediaContentStore>();
        await mediaStore.StoreAsync(
            "logs/events/identity-evidence.json",
            "application/json",
            Encoding.UTF8.GetBytes("{\"metadata\":{\"robot_name\":\"Royal-Current-Sage-Canvas\"}}"),
            new Dictionary<string, object?> { ["deviceId"] = placeholder.DeviceId });

        await AuthenticateAdminAsync(client);
        var response = await client.GetAsync($"/api/portal/status/robots/{placeholder.DeviceId}/identity-suggestion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("suggested").GetBoolean());
        Assert.Equal("Royal-Current-Sage-Canvas", payload.GetProperty("proposedRobotId").GetString());
        Assert.Contains(payload.GetProperty("evidence").EnumerateArray(), item =>
            item.GetProperty("source").GetString()?.StartsWith("artifact-content:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task IdentitySuggestion_InspectsUnlinkedSessionAssignedForIdentityEvidence()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var placeholder = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5c0b221fdf9d450019c5e254",
            RobotId = "robot-5c0b221fdf9d450019c5e254",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var session = store.OpenSession("neo-hub-listen", "wire-device",
            "conn:identity-evidence", "neo-hub-listen", "/v1/listen");
        session.Metadata["identitySuggestionDeviceId"] = placeholder.DeviceId;
        session.Metadata["robotFriendlyId"] = "Royal-Current-Sage-Canvas";

        await AuthenticateAdminAsync(client);
        var response = await client.GetAsync(
            $"/api/portal/status/robots/{placeholder.DeviceId}/identity-suggestion");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("suggested").GetBoolean());
        Assert.Equal("Royal-Current-Sage-Canvas", payload.GetProperty("proposedRobotId").GetString());
        Assert.Contains(payload.GetProperty("evidence").EnumerateArray(), item =>
            item.GetProperty("source").GetString() == "stored-session");

        var summary = await (await client.GetAsync("/api/portal/status/summary"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var robot = Assert.Single(summary.GetProperty("robots").EnumerateArray(), item =>
            item.GetProperty("deviceId").GetString() == placeholder.DeviceId);
        Assert.False(robot.GetProperty("connected").GetBoolean());
        Assert.Equal("never-connected", robot.GetProperty("presence").GetString());
    }

    [Fact]
    public async Task StatusSummary_PrefersNamedIdentityOverGenericHashPlaceholder()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        var placeholder = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5c0b221fdf9d450019c5e254",
            RobotId = "robot-5c0b221fdf9d450019c5e254",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var namedIdentity = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "robot-Royal-Current-Sage-Canvas",
            FriendlyName = "OpenJibo Registered Robot"
        });

        var session = store.OpenSession("neo-hub-listen", placeholder.DeviceId, "token-test", "neo-hub-listen", "/token-test");
        session.Metadata["registeredDeviceId"] = namedIdentity.DeviceId;
        session.Metadata["registeredRobotId"] = namedIdentity.RobotId;
        session.LastSeenUtc = DateTimeOffset.UtcNow.AddSeconds(-5);

        await AuthenticateAdminAsync(client);

        var summaryResponse = await client.GetAsync("/api/portal/status/summary");

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var reconciledRow = summary.GetProperty("robots")
            .EnumerateArray()
            .Single(robot => robot.GetProperty("presence").GetString() == "online");

        Assert.Equal(namedIdentity.DeviceId, reconciledRow.GetProperty("deviceId").GetString());
        Assert.Equal(namedIdentity.RobotId, reconciledRow.GetProperty("robotId").GetString());
    }

    [Fact]
    public async Task StatusSummary_DoesNotCarryDeploymentSmokeSourceOntoNamedIdentity()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        var authorizationOptions = new ReleaseSmokeAuthorizationOptions
        {
            Enabled = true, Secret = "portal-fixture-secret", MaxConcurrentDevices = 6
        };
        Assert.True(authorizationOptions.TryAuthorize("open-jibo-smoke-staging-primary",
            "portal-fixture-secret", out var smokeAuthorization));
        var placeholder = store.GetOrCreateDeploymentSmokeDevice(smokeAuthorization!, null, null);
        var namedIdentity = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "robot-Royal-Current-Sage-Canvas",
            FriendlyName = "OpenJibo Registered Robot"
        });

        var session = store.OpenSession("hub", placeholder.DeviceId, "token-test", "hub", "/token-test");
        session.Metadata["registeredDeviceId"] = namedIdentity.DeviceId;
        session.Metadata["registeredRobotId"] = namedIdentity.RobotId;
        session.LastSeenUtc = DateTimeOffset.UtcNow.AddSeconds(-5);

        await AuthenticateAdminAsync(client);

        var summaryResponse = await client.GetAsync("/api/portal/status/summary");

        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var reconciledRow = summary.GetProperty("robots")
            .EnumerateArray()
            .Single(robot => robot.GetProperty("presence").GetString() == "online");

        Assert.Equal(namedIdentity.DeviceId, reconciledRow.GetProperty("deviceId").GetString());
        Assert.Equal(RobotRegistrationSources.Unknown, reconciledRow.GetProperty("registrationSource").GetString());
        Assert.False(reconciledRow.GetProperty("isSynthetic").GetBoolean());
    }

    [Fact]
    public void RegisterVerifiedRobotIdentity_ArchivesSupersededPlaceholderRecords()
    {
        var store = new InMemoryCloudStateStore();
        var placeholder = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "5c0b221fdf9d450019c5e254",
            RobotId = "robot-5c0b221fdf9d450019c5e254",
            FriendlyName = "OpenJibo Registered Robot"
        });
        var verified = store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "Royal-Current-Sage-Canvas",
            RobotId = "robot-Royal-Current-Sage-Canvas",
            FriendlyName = "Royal-Current-Sage-Canvas",
            RegistrationSource = RobotRegistrationSources.Portal
        });

        var archivedCount = PortalEndpoints.ArchiveSupersededRobotPlaceholders(
            store,
            verified.DeviceId,
            placeholder.DeviceId,
            verified.RobotId);

        Assert.Equal(1, archivedCount);

        var archivedPlaceholder = store.GetDevices().Single(device =>
            device.DeviceId == placeholder.DeviceId);
        Assert.NotNull(archivedPlaceholder);
        Assert.True(archivedPlaceholder.IsHidden);
        Assert.NotNull(archivedPlaceholder.ArchivedUtc);

        var verifiedDevice = store.GetDevices().Single(device =>
            device.DeviceId == verified.DeviceId);
        Assert.NotNull(verifiedDevice);
        Assert.False(verifiedDevice.IsHidden);
        Assert.Null(verifiedDevice.ArchivedUtc);
    }

    private static async Task AuthenticateAdminAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/portal/status/login", new { password = "test-admin-password" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", payload.GetProperty("portalSessionToken").GetString());
    }

    private static async Task<JsonElement> ReadJsonFrameAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        using var document = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return document.RootElement.Clone();
    }

    private static async Task TryCloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent))
            return;

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-complete", CancellationToken.None);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        bool peerSyncEnabled = true,
        string deploymentMode = "self-hosted-isolated",
        bool localWhisperEnabled = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"openjibo-portal-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var ffmpegPath = Path.Combine(root, "ffmpeg.exe");
        var whisperCliPath = Path.Combine(root, "whisper-cli.exe");
        var whisperModelPath = Path.Combine(root, "ggml-test.bin");
        if (localWhisperEnabled)
        {
            File.WriteAllText(ffmpegPath, "test");
            File.WriteAllText(whisperCliPath, "test");
            File.WriteAllText(whisperModelPath, "test");
        }

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMediaContentStore>();
                    services.AddSingleton<IMediaContentStore>(new FileMediaContentStore(Path.Combine(root, "media")));
                });
                builder.UseSetting("OpenJibo:Deployment:Mode", deploymentMode);
                builder.UseSetting("OpenJibo:CanonicalApiHostname", "test.openjibo.local");
                builder.UseSetting("OpenJibo:Telemetry:DirectoryPath", Path.Combine(root, "websocket"));
                builder.UseSetting("OpenJibo:ProtocolTelemetry:DirectoryPath", Path.Combine(root, "http"));
                builder.UseSetting("OpenJibo:TurnTelemetry:DirectoryPath", Path.Combine(root, "turn"));
                builder.UseSetting("OpenJibo:Logging:DirectoryPath", Path.Combine(root, "logs"));
                builder.UseSetting(
                    "OpenJibo:UserIntegrations:PersistencePath",
                    Path.Combine(root, "user-integrations.json"));
                builder.UseSetting("OpenJibo:State:Backend", "File");
                builder.UseSetting("OpenJibo:PersonalMemory:Backend", "File");
                builder.UseSetting("OpenJibo:State:PersistencePath", Path.Combine(root, "cloud-state.json"));
                builder.UseSetting(
                    "OpenJibo:PersonalMemory:PersistencePath",
                    Path.Combine(root, "personal-memory.json"));
                builder.UseSetting("OpenJibo:Stt:EnableLocalWhisperCpp", localWhisperEnabled.ToString());
                builder.UseSetting("OpenJibo:Stt:FfmpegPath", ffmpegPath);
                builder.UseSetting("OpenJibo:Stt:WhisperCliPath", whisperCliPath);
                builder.UseSetting("OpenJibo:Stt:WhisperModelPath", whisperModelPath);
                builder.UseSetting("OpenJibo:Stt:EnableWhisperServer", "false");
                builder.UseSetting("OpenJibo:Stt:AutoStartWhisperServer", "false");
                builder.UseSetting("OpenJibo:Testing:SkipSttDependencyValidation",
                    localWhisperEnabled.ToString());
                builder.UseSetting("OpenJibo:Portal:StatusPassword", "test-admin-password");
                builder.UseSetting("OpenJibo:FleetNetwork:PeerSyncEnabled", peerSyncEnabled.ToString());
                builder.UseSetting("OpenJibo:FleetNetwork:AllowedPeerHosts", "fleet.example.openjibo.com");
                builder.UseSetting("OpenJibo:FleetNetwork:PeerSyncSharedKey", "test-peer-key");
            });
    }
}
