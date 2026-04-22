using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class JiboCloudProtocolServiceTests
{
    private readonly JiboCloudProtocolService _service = new(new InMemoryCloudStateStore());

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
    public async Task GetUpdateFrom_WithoutStagedUpdate_ReturnsEmptyPayload()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Update_20160715",
            Operation = "GetUpdateFrom",
            BodyText = """{"subsystem":"robot","fromVersion":"1.0.0"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
        Assert.Empty(payload.RootElement.EnumerateObject());
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
            if (File.Exists(persistencePath))
            {
                File.Delete(persistencePath);
            }
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
        Assert.Equal("https://api.jibo.com/media/photo-blob-1", createdPayload.RootElement.GetProperty("url").GetString());

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
        Assert.Single(payload.RootElement.EnumerateArray());
    }
}
