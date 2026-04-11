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
    public async Task NewRobotToken_UsesBodyDeviceId()
    {
        var result = await _service.DispatchAsync(new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            ServicePrefix = "Notification_20160715",
            Operation = "NewRobotToken",
            BodyText = """{"deviceId":"robot-123"}"""
        });

        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("robot-123", payload.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public async Task GetUpdateFrom_ReturnsNoOpUpdate()
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
        Assert.Equal("robot", payload.RootElement.GetProperty("subsystem").GetString());
        Assert.True(payload.RootElement.TryGetProperty("url", out _));
    }
}
