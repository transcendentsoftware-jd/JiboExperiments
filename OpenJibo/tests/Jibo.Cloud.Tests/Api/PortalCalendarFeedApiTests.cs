using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Tests.Api;

public sealed class PortalCalendarFeedApiTests
{
    [Fact]
    public async Task CalendarFeeds_SaveListAndClear_NeverEchoFullUrl()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        await AuthorizeAsync(client, factory);
        var loop = store.AddLoop(null, null, "Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        var member = store.AddLoopMember(
            loop.LoopId,
            null,
            "zane@example.test",
            "Zane",
            "Tester",
            "unknown",
            null,
            false,
            "member");

        const string secretUrl = "https://calendar.example.com/ical/zane/private-token/basic.ics";
        var putResponse = await client.PutAsJsonAsync(
            $"/api/portal/calendar-feeds/{member.Id}",
            new { icalUrl = secretUrl, isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var putBody = await putResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-token", putBody, StringComparison.Ordinal);
        var putPayload = JsonDocument.Parse(putBody).RootElement;
        Assert.True(putPayload.GetProperty("configured").GetBoolean());
        Assert.Equal("calendar.example.com", putPayload.GetProperty("host").GetString());

        var listResponse = await client.GetAsync("/api/portal/calendar-feeds");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-token", listBody, StringComparison.Ordinal);
        Assert.DoesNotContain(secretUrl, listBody, StringComparison.Ordinal);
        Assert.Contains("calendar.example.com", listBody, StringComparison.Ordinal);

        var dashboardResponse = await client.GetAsync("/api/portal/dashboard");
        var dashboardBody = await dashboardResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-token", dashboardBody, StringComparison.Ordinal);

        var deleteResponse = await client.DeleteAsync($"/api/portal/calendar-feeds/{member.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        var integrationStore = factory.Services.GetRequiredService<IUserIntegrationStore>();
        Assert.Null(integrationStore.FindMemberCalendarFeed(loop.LoopId, member.Id));
    }

    [Fact]
    public async Task CalendarFeeds_ListsPeopleFromLoopRoster()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        await AuthorizeAsync(client, factory);
        var loop = store.AddLoop(null, null, "Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        store.SyncPeopleFromLoopUsers(
            loop.LoopId,
            "Ghost-Instance-Onion-Silk",
            [
                new LoopUserSnapshot("looper-zane", "Zane", "Tester", "acct-zane", Type: "owner"),
                new LoopUserSnapshot("looper-jon", "Jon", "Tester", "acct-jon", Type: "member")
            ]);

        var listResponse = await client.GetAsync("/api/portal/calendar-feeds");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsStringAsync();
        Assert.Contains("Zane", listBody, StringComparison.Ordinal);
        Assert.Contains("Jon", listBody, StringComparison.Ordinal);
        Assert.Contains("looper-zane", listBody, StringComparison.Ordinal);
        Assert.Contains("looper-jon", listBody, StringComparison.Ordinal);
        Assert.DoesNotContain("person-openjibo-household-member", listBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CalendarFeeds_DoesNotMergePeopleFromAnotherRobot()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();

        await AuthorizeAsync(client, factory);
        var thisLoop = store.AddLoop(null, null, "Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        var otherLoop = store.AddLoop(null, null, "Other-Jibo-Friendly", "OTHER-DEVICE-0001");

        store.SyncPeopleFromLoopUsers(
            thisLoop.LoopId,
            "Ghost-Instance-Onion-Silk",
            [
                new LoopUserSnapshot("looper-zane", "Zane", "Tester", Type: "owner"),
                new LoopUserSnapshot("looper-dad", "My Dad", "Tester", Type: "member")
            ]);
        store.SyncPeopleFromLoopUsers(
            otherLoop.LoopId,
            "Other-Jibo-Friendly",
            [
                new LoopUserSnapshot("looper-guy", "Guy from Jibo 2", "X", Type: "member"),
                new LoopUserSnapshot("looper-gal", "Gal from Jibo 2", "Y", Type: "member")
            ]);

        var listBody = await (await client.GetAsync("/api/portal/calendar-feeds")).Content.ReadAsStringAsync();
        Assert.Contains("Zane", listBody, StringComparison.Ordinal);
        Assert.Contains("My Dad", listBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Guy from Jibo 2", listBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Gal from Jibo 2", listBody, StringComparison.Ordinal);
        Assert.DoesNotContain("looper-guy", listBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_IsUniqueToVerifiedJibo()
    {
        await using var factory = CreateFactory();
        var firstClient = factory.CreateClient();
        var secondClient = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var integrations = factory.Services.GetRequiredService<IUserIntegrationStore>();

        var firstLoop = store.AddLoop(null, null, "Jibo-One", "DEVICE-ONE");
        var secondLoop = store.AddLoop(null, null, "Jibo-Two", "DEVICE-TWO");
        store.SyncPeopleFromLoopUsers(
            firstLoop.LoopId,
            "Jibo-One",
            [new LoopUserSnapshot("person-one", "First Household", Type: "owner")]);
        store.SyncPeopleFromLoopUsers(
            secondLoop.LoopId,
            "Jibo-Two",
            [new LoopUserSnapshot("person-two", "Second Household", Type: "owner")]);

        await AuthorizeAsync(firstClient, factory, "Jibo-One", "DEVICE-ONE");
        await AuthorizeAsync(secondClient, factory, "Jibo-Two", "DEVICE-TWO");

        integrations.AddHomeAssistantLink("DEVICE-ONE", "Jibo-One", "ha-one");
        integrations.AddHomeAssistantLink("DEVICE-TWO", "Jibo-Two", "ha-two");

        var firstDashboard =
            await (await firstClient.GetAsync("/api/portal/dashboard")).Content.ReadFromJsonAsync<JsonElement>();
        var secondDashboard =
            await (await secondClient.GetAsync("/api/portal/dashboard")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Jibo-One", firstDashboard.GetProperty("jiboFriendlyId").GetString());
        Assert.Contains("First Household", firstDashboard.ToString(), StringComparison.Ordinal);
        Assert.Contains("ha-one", firstDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Second Household", firstDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ha-two", firstDashboard.ToString(), StringComparison.Ordinal);

        Assert.Equal("Jibo-Two", secondDashboard.GetProperty("jiboFriendlyId").GetString());
        Assert.Contains("Second Household", secondDashboard.ToString(), StringComparison.Ordinal);
        Assert.Contains("ha-two", secondDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("First Household", secondDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ha-one", secondDashboard.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Portal_IsolatesRobots_ThroughIdentityResolver_WithSharedSingletonDeviceId()
    {
        await using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var integrations = factory.Services.GetRequiredService<IUserIntegrationStore>();

        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = "SHARED-SINGLETON-DEVICE",
            RobotId = "Bootstrap-Robot",
            FriendlyName = "Bootstrap"
        });

        // Real bug path: unregistered hyphenated friendlyIds must not inherit GetRobot().DeviceId.
        var (deviceOne, friendlyOne) = JiboIdentityResolver.Resolve(
            new Jibo.Runtime.Abstractions.TurnContext { DeviceId = "Jibo-One" }, store);
        var (deviceTwo, friendlyTwo) = JiboIdentityResolver.Resolve(
            new Jibo.Runtime.Abstractions.TurnContext { DeviceId = "Jibo-Two" }, store);
        Assert.Equal("Jibo-One", deviceOne);
        Assert.Equal("Jibo-One", friendlyOne);
        Assert.Equal("Jibo-Two", deviceTwo);
        Assert.Equal("Jibo-Two", friendlyTwo);
        Assert.NotEqual("SHARED-SINGLETON-DEVICE", deviceOne);
        Assert.NotEqual("SHARED-SINGLETON-DEVICE", deviceTwo);

        var firstLoop = store.AddLoop(null, null, friendlyOne, deviceOne);
        var secondLoop = store.AddLoop(null, null, friendlyTwo, deviceTwo);
        Assert.NotEqual(firstLoop.LoopId, secondLoop.LoopId);

        // Shared second arg must not merge loops once matching is friendlyId-primary.
        var mergedProbe = store.AddLoop(null, null, "Jibo-Two", "SHARED-SINGLETON-DEVICE");
        Assert.Equal(secondLoop.LoopId, mergedProbe.LoopId);
        Assert.NotEqual(firstLoop.LoopId, mergedProbe.LoopId);

        store.SyncPeopleFromLoopUsers(
            firstLoop.LoopId,
            friendlyOne,
            [new LoopUserSnapshot("person-one", "First Household", Type: "owner")]);
        store.SyncPeopleFromLoopUsers(
            secondLoop.LoopId,
            friendlyTwo,
            [new LoopUserSnapshot("person-two", "Second Household", Type: "owner")]);
        integrations.UpsertMemberCalendarFeed(
            firstLoop.LoopId,
            "person-one",
            "https://calendar.example.com/one.ics",
            true);
        integrations.UpsertMemberCalendarFeed(
            secondLoop.LoopId,
            "person-two",
            "https://calendar.example.com/two.ics",
            true);

        var firstClient = factory.CreateClient();
        var secondClient = factory.CreateClient();
        Assert.False(string.IsNullOrWhiteSpace(friendlyOne));
        Assert.False(string.IsNullOrWhiteSpace(deviceOne));
        Assert.False(string.IsNullOrWhiteSpace(friendlyTwo));
        Assert.False(string.IsNullOrWhiteSpace(deviceTwo));
        await AuthorizeViaResolverAsync(firstClient, verificationService, friendlyOne!, deviceOne!);
        await AuthorizeViaResolverAsync(secondClient, verificationService, friendlyTwo!, deviceTwo!);

        var firstFeeds = await (await firstClient.GetAsync("/api/portal/calendar-feeds")).Content.ReadAsStringAsync();
        var secondFeeds = await (await secondClient.GetAsync("/api/portal/calendar-feeds")).Content.ReadAsStringAsync();
        Assert.Contains("First Household", firstFeeds, StringComparison.Ordinal);
        Assert.DoesNotContain("Second Household", firstFeeds, StringComparison.Ordinal);
        Assert.Contains("Second Household", secondFeeds, StringComparison.Ordinal);
        Assert.DoesNotContain("First Household", secondFeeds, StringComparison.Ordinal);

        var firstDashboard =
            await (await firstClient.GetAsync("/api/portal/dashboard")).Content.ReadFromJsonAsync<JsonElement>();
        var secondDashboard =
            await (await secondClient.GetAsync("/api/portal/dashboard")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Jibo-One", firstDashboard.GetProperty("jiboFriendlyId").GetString());
        Assert.Equal("Jibo-Two", secondDashboard.GetProperty("jiboFriendlyId").GetString());
        Assert.Contains("First Household", firstDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Second Household", firstDashboard.ToString(), StringComparison.Ordinal);
        Assert.Contains("Second Household", secondDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("First Household", secondDashboard.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Portal_IsolatesRobots_ThroughRealContextRuntimeLoopJiboId()
    {
        await using var factory = CreateFactory();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        var integrations = factory.Services.GetRequiredService<IUserIntegrationStore>();

        store.UpdateRobot(new DeviceRegistration
        {
            DeviceId = "SHARED-SINGLETON-DEVICE",
            RobotId = "Bootstrap-Robot",
            FriendlyName = "Bootstrap"
        });

        // Real wire shape: general has only "release"; identity is runtime.loop.jibo.id.
        var (deviceOne, friendlyOne) = JiboIdentityResolver.Resolve(
            new Jibo.Runtime.Abstractions.TurnContext
            {
                DeviceId = "SHARED-SINGLETON-DEVICE",
                Attributes = new Dictionary<string, object?>
                {
                    ["context"] =
                        """{"runtime":{"loop":{"loopId":"loop-household-one","jibo":{"id":"jibo-unit-one"},"users":[]}},"general":{"release":"1.9.2"}}"""
                }
            },
            store);
        var (deviceTwo, friendlyTwo) = JiboIdentityResolver.Resolve(
            new Jibo.Runtime.Abstractions.TurnContext
            {
                DeviceId = "SHARED-SINGLETON-DEVICE",
                Attributes = new Dictionary<string, object?>
                {
                    ["context"] =
                        """{"runtime":{"loop":{"loopId":"loop-household-two","jibo":{"id":"jibo-unit-two"},"users":[]}},"general":{"release":"1.9.2"}}"""
                }
            },
            store);

        Assert.Equal("jibo-unit-one", deviceOne);
        Assert.Equal("jibo-unit-one", friendlyOne);
        Assert.Equal("jibo-unit-two", deviceTwo);
        Assert.Equal("jibo-unit-two", friendlyTwo);
        Assert.NotEqual(deviceOne, deviceTwo);

        var firstLoop = store.AddLoop(null, null, friendlyOne, friendlyOne);
        var secondLoop = store.AddLoop(null, null, friendlyTwo, friendlyTwo);
        Assert.NotEqual(firstLoop.LoopId, secondLoop.LoopId);

        store.SyncPeopleFromLoopUsers(
            firstLoop.LoopId,
            friendlyOne,
            [new LoopUserSnapshot("person-one", "First Household", Type: "owner")]);
        store.SyncPeopleFromLoopUsers(
            secondLoop.LoopId,
            friendlyTwo,
            [new LoopUserSnapshot("person-two", "Second Household", Type: "owner")]);
        integrations.UpsertMemberCalendarFeed(
            firstLoop.LoopId,
            "person-one",
            "https://calendar.example.com/one.ics",
            true);
        integrations.UpsertMemberCalendarFeed(
            secondLoop.LoopId,
            "person-two",
            "https://calendar.example.com/two.ics",
            true);

        var firstClient = factory.CreateClient();
        var secondClient = factory.CreateClient();
        await AuthorizeViaResolverAsync(firstClient, verificationService, friendlyOne!, deviceOne!);
        await AuthorizeViaResolverAsync(secondClient, verificationService, friendlyTwo!, deviceTwo!);

        var firstFeeds = await (await firstClient.GetAsync("/api/portal/calendar-feeds")).Content.ReadAsStringAsync();
        var secondFeeds = await (await secondClient.GetAsync("/api/portal/calendar-feeds")).Content.ReadAsStringAsync();
        Assert.Contains("First Household", firstFeeds, StringComparison.Ordinal);
        Assert.DoesNotContain("Second Household", firstFeeds, StringComparison.Ordinal);
        Assert.Contains("Second Household", secondFeeds, StringComparison.Ordinal);
        Assert.DoesNotContain("First Household", secondFeeds, StringComparison.Ordinal);

        var firstDashboard =
            await (await firstClient.GetAsync("/api/portal/dashboard")).Content.ReadFromJsonAsync<JsonElement>();
        var secondDashboard =
            await (await secondClient.GetAsync("/api/portal/dashboard")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("jibo-unit-one", firstDashboard.GetProperty("jiboFriendlyId").GetString());
        Assert.Equal("jibo-unit-two", secondDashboard.GetProperty("jiboFriendlyId").GetString());
        Assert.Contains("First Household", firstDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Second Household", firstDashboard.ToString(), StringComparison.Ordinal);
        Assert.Contains("Second Household", secondDashboard.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("First Household", secondDashboard.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CalendarFeeds_RequiresPortalSession()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/portal/calendar-feeds");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CalendarFeeds_RejectsNonHttpsUrl()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<ICloudStateStore>();
        await AuthorizeAsync(client, factory);
        var loop = store.AddLoop(null, null, "Ghost-Instance-Onion-Silk", "BOJW-1000-0017-0820-0020");
        var member = store.AddLoopMember(
            loop.LoopId,
            null,
            "jon@example.test",
            "Jon",
            "Tester",
            "unknown",
            null,
            false,
            "member");

        var response = await client.PutAsJsonAsync(
            $"/api/portal/calendar-feeds/{member.Id}",
            new { icalUrl = "http://calendar.example.com/basic.ics" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task AuthorizeAsync(
        HttpClient client,
        WebApplicationFactory<Program> factory,
        string friendlyId = "Ghost-Instance-Onion-Silk",
        string deviceId = "BOJW-1000-0017-0820-0020")
    {
        var verificationService = factory.Services.GetRequiredService<JiboVerificationService>();
        await AuthorizeViaResolverAsync(client, verificationService, friendlyId, deviceId);
    }

    private static async Task AuthorizeViaResolverAsync(
        HttpClient client,
        JiboVerificationService verificationService,
        string friendlyId,
        string deviceId)
    {
        var spokenCode = verificationService.IssueCodeForDevice(friendlyId, deviceId);
        var confirmPayload = await (await client.PostAsJsonAsync(
            "/api/portal/jibo-verification/confirm",
            new { code = spokenCode })).Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", confirmPayload.GetProperty("portalSessionToken").GetString());
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openjibo-portal-cal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
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
                builder.UseSetting("OpenJibo:Stt:EnableLocalWhisperCpp", "false");
                builder.UseSetting("OpenJibo:Stt:EnableWhisperServer", "false");
            });
    }
}
