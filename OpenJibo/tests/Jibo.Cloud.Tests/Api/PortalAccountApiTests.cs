using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;

namespace Jibo.Cloud.Tests.Api;

public sealed class PortalAccountApiTests
{
    [Fact]
    public async Task CodeOnlySession_IsInvalidatedByFirstClaimAndLaterTransfer()
    {
        await using var factory = CreateFactory();
        async Task<HttpClient> CodeLogin()
        {
            var code = factory.Services.GetRequiredService<JiboVerificationService>()
                .IssueCodeForDevice("isolation-jibo", "device-isolation");
            var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/portal/jibo-verification/confirm", new { code });
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                payload.GetProperty("portalSessionToken").GetString());
            return client;
        }

        var unclaimedProof = await CodeLogin();
        Assert.Equal(HttpStatusCode.OK, (await unclaimedProof.GetAsync("/api/portal/dashboard")).StatusCode);
        var first = await RegisterAndAuthenticateAsync(factory, "first@example.invalid");
        await PairAndSelectAsync(factory, first);
        Assert.Equal(HttpStatusCode.Unauthorized, (await unclaimedProof.GetAsync("/api/portal/dashboard")).StatusCode);
        var firstOwnersProof = await CodeLogin();
        Assert.Equal(HttpStatusCode.OK, (await firstOwnersProof.GetAsync("/api/portal/dashboard")).StatusCode);

        var second = await RegisterAndAuthenticateAsync(factory, "second@example.invalid");
        var secondRobot = await PairAndSelectAsync(factory, second);
        Assert.Equal(HttpStatusCode.Unauthorized, (await firstOwnersProof.GetAsync("/api/portal/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondRobot.GetAsync("/api/portal/dashboard")).StatusCode);
    }

    [Fact]
    public async Task RobotNamedPortalAdmin_DoesNotGrantAdministratorAccess()
    {
        await using var factory = CreateFactory();
        var verification = factory.Services.GetRequiredService<JiboVerificationService>();
        var code = verification.IssueCodeForDevice("ordinary-robot", "portal-admin");
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/portal/jibo-verification/confirm", new { code });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            payload.GetProperty("portalSessionToken").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/portal/status/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/portal/dashboard")).StatusCode);
    }

    [Theory]
    [InlineData("/api/portal/dashboard")]
    [InlineData("/api/portal/calendar-feeds")]
    [InlineData("/api/portal/loop-members")]
    [InlineData("/api/portal/home-assistant/links")]
    [InlineData("/api/portal/admin/summary")]
    public async Task AccountOnlySession_CannotReadRobotResourcesOrCreateTopology(string path)
    {
        await using var factory = CreateFactory();
        var client = await RegisterAndAuthenticateAsync(factory, "unpaired@example.invalid");
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var loops = store.GetLoops().Select(loop => loop.LoopId).ToArray();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(loops, store.GetLoops().Select(loop => loop.LoopId));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/portal/account")).StatusCode);
    }

    [Theory]
    [InlineData("/api/portal/dashboard")]
    [InlineData("/api/portal/calendar-feeds")]
    [InlineData("/api/portal/loop-members")]
    [InlineData("/api/portal/home-assistant/links")]
    [InlineData("/api/portal/admin/summary")]
    public async Task TransferredRobot_RejectsPreviousOwnersSelectedSession(string path)
    {
        await using var factory = CreateFactory();
        var firstAccount = await RegisterAndAuthenticateAsync(factory, "first@example.invalid");
        var firstRobot = await PairAndSelectAsync(factory, firstAccount);
        Assert.Equal(HttpStatusCode.OK, (await firstRobot.GetAsync(path)).StatusCode);

        var secondAccount = await RegisterAndAuthenticateAsync(factory, "second@example.invalid");
        var secondRobot = await PairAndSelectAsync(factory, secondAccount);

        Assert.Equal(HttpStatusCode.Unauthorized, (await firstRobot.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await secondRobot.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await firstAccount.PostAsJsonAsync(
            "/api/portal/robots/select", new { deviceId = "device-isolation" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await firstAccount.GetAsync("/api/portal/account")).StatusCode);
    }

    [Fact]
    public async Task TransferredRobot_PreviousOwnerCannotDeleteNewOwnersIntegration()
    {
        await using var factory = CreateFactory();
        var firstAccount = await RegisterAndAuthenticateAsync(factory, "first@example.invalid");
        var firstRobot = await PairAndSelectAsync(factory, firstAccount);
        var integrations = factory.Services.GetRequiredService<IUserIntegrationStore>();
        var link = integrations.AddHomeAssistantLink("isolation-jibo", "isolation-jibo", "synthetic-instance");
        var secondAccount = await RegisterAndAuthenticateAsync(factory, "second@example.invalid");
        var secondRobot = await PairAndSelectAsync(factory, secondAccount);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await firstRobot.DeleteAsync("/api/portal/home-assistant/link")).StatusCode);
        Assert.Equal(link.LinkId, integrations.FindLinkForJibo("isolation-jibo", "isolation-jibo")?.LinkId);
        Assert.Equal(HttpStatusCode.OK,
            (await secondRobot.DeleteAsync("/api/portal/home-assistant/link")).StatusCode);
    }

    private static async Task<HttpClient> PairAndSelectAsync(WebApplicationFactory<Program> factory,
        HttpClient account)
    {
        var verification = factory.Services.GetRequiredService<JiboVerificationService>();
        var code = verification.IssueCodeForDevice("isolation-jibo", "device-isolation");
        var response = await account.PostAsJsonAsync("/api/portal/robots/pair", new { code });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            payload.GetProperty("portalSessionToken").GetString());
        return client;
    }

    [Fact]
    public async Task Register_RejectsPasswordsOutsideEightToThirtyTwoCharacters()
    {
        await using var factory = CreateFactory();
        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/portal/account/register",
            new { email = "user@example.com", password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterLoginAndAccount_ReturnAccountSessionAndNoRobots()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var register = await client.PostAsJsonAsync(
            "/api/portal/account/register",
            new { email = "user@example.com", password = "password123" });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var registered = await register.Content.ReadFromJsonAsync<JsonElement>();
        var token = registered.GetProperty("portalSessionToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var account = await client.GetFromJsonAsync<JsonElement>("/api/portal/account");
        Assert.Equal("user@example.com", account.GetProperty("email").GetString());
        Assert.Empty(account.GetProperty("robots").EnumerateArray());

        var login = await factory.CreateClient().PostAsJsonAsync(
            "/api/portal/account/login",
            new { email = "user@example.com", password = "password123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var invalidLogin = await factory.CreateClient().PostAsJsonAsync(
            "/api/portal/account/login",
            new { email = "user@example.com", password = "wrong-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
    }

    [Fact]
    public async Task PairingLinksRobotAndNewAccountCanTransferIt()
    {
        await using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var verification = factory.Services.GetRequiredService<JiboVerificationService>();

        var firstClient = await RegisterAndAuthenticateAsync(factory, "first@example.com");
        var firstCode = verification.IssueCodeForDevice("Kitchen Jibo", "device-kitchen");
        var firstPair = await firstClient.PostAsJsonAsync(
            "/api/portal/robots/pair", new { code = firstCode });
        Assert.Equal(HttpStatusCode.OK, firstPair.StatusCode);

        var firstAccount = await firstClient.GetFromJsonAsync<JsonElement>("/api/portal/account");
        Assert.Single(firstAccount.GetProperty("robots").EnumerateArray());
        Assert.NotNull(store.GetUserIdForDevice("device-kitchen"));

        var secondClient = await RegisterAndAuthenticateAsync(factory, "second@example.com");
        var secondCode = verification.IssueCodeForDevice("Kitchen Jibo", "device-kitchen");
        var secondPair = await secondClient.PostAsJsonAsync(
            "/api/portal/robots/pair", new { code = secondCode });
        Assert.Equal(HttpStatusCode.OK, secondPair.StatusCode);

        var firstAfterTransfer = await firstClient.GetAsync("/api/portal/account");
        Assert.Equal(HttpStatusCode.OK, firstAfterTransfer.StatusCode);
        var firstAccountAfterTransfer =
            await firstAfterTransfer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(firstAccountAfterTransfer.GetProperty("robots").EnumerateArray());

        var secondAccount = await secondClient.GetFromJsonAsync<JsonElement>("/api/portal/account");
        var robot = secondAccount.GetProperty("robots").EnumerateArray().Single();
        Assert.Equal("device-kitchen", robot.GetProperty("deviceId").GetString());

        var rename = await secondClient.PutAsJsonAsync(
            "/api/portal/robots/device-kitchen/name", new { name = "Kitchen Jibo" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        var renamedAccount = await secondClient.GetFromJsonAsync<JsonElement>("/api/portal/account");
        Assert.Equal("Kitchen Jibo",
            renamedAccount.GetProperty("robots").EnumerateArray().Single().GetProperty("friendlyName").GetString());

        var select = await secondClient.PostAsJsonAsync(
            "/api/portal/robots/select", new { deviceId = "device-kitchen" });
        Assert.Equal(HttpStatusCode.OK, select.StatusCode);
        var selected = await select.Content.ReadFromJsonAsync<JsonElement>();
        secondClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", selected.GetProperty("portalSessionToken").GetString());
        var dashboardResponse = await secondClient.GetAsync("/api/portal/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
        var dashboard = await dashboardResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Kitchen Jibo", dashboard.GetProperty("jiboName").GetString());
    }

    [Fact]
    public async Task LegacyCodeLoginRemainsRobotScopedAndNotAnAccountSession()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var verification = factory.Services.GetRequiredService<JiboVerificationService>();
        store.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "device-legacy",
            RobotId = "legacy-jibo",
            FriendlyName = "Legacy Jibo"
        });
        var code = verification.IssueCodeForDevice("legacy-jibo", "device-legacy");

        var response = await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm", new { code });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", payload.GetProperty("portalSessionToken").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/portal/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/portal/account")).StatusCode);
    }

    private static async Task<HttpClient> RegisterAndAuthenticateAsync(
        WebApplicationFactory<Program> factory, string email)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/portal/account/register", new { email, password = "password123" });
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", payload.GetProperty("portalSessionToken").GetString());
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openjibo-account-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMediaContentStore>();
                    services.AddSingleton<IMediaContentStore>(new FileMediaContentStore(Path.Combine(root, "media")));
                });
                builder.UseSetting("OpenJibo:Telemetry:DirectoryPath", Path.Combine(root, "websocket"));
                builder.UseSetting("OpenJibo:ProtocolTelemetry:DirectoryPath", Path.Combine(root, "http"));
                builder.UseSetting("OpenJibo:TurnTelemetry:DirectoryPath", Path.Combine(root, "turn"));
                builder.UseSetting("OpenJibo:Logging:DirectoryPath", Path.Combine(root, "logs"));
                builder.UseSetting("OpenJibo:UserIntegrations:PersistencePath",
                    Path.Combine(root, "user-integrations.json"));
                builder.UseSetting("OpenJibo:State:Backend", "File");
                builder.UseSetting("OpenJibo:PersonalMemory:Backend", "File");
                builder.UseSetting("OpenJibo:State:PersistencePath", Path.Combine(root, "cloud-state.json"));
                builder.UseSetting("OpenJibo:PersonalMemory:PersistencePath",
                    Path.Combine(root, "personal-memory.json"));
                builder.UseSetting("OpenJibo:Stt:EnableLocalWhisperCpp", "false");
                builder.UseSetting("OpenJibo:Stt:EnableWhisperServer", "false");
                builder.UseSetting("OpenJibo:Portal:StatusPassword", "test-admin-password");
                builder.UseSetting("OpenJibo:FleetNetwork:PeerSyncEnabled", "false");
                builder.UseSetting("OpenJibo:FleetNetwork:AllowedPeerHosts", "fleet.example.openjibo.com");
                builder.UseSetting("OpenJibo:FleetNetwork:PeerSyncSharedKey", "test-peer-key");
            });
    }
}
