using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class OobeProvisioningRetryTests
{
    private static Task<ProtocolDispatchResult> Call(JiboCloudProtocolService service, string operation, object body,
        string? firmware = null) => service.DispatchAsync(new ProtocolEnvelope
    {
        ServicePrefix = "OOBE_20161026", Operation = operation, HostName = "api.openjibo.com",
        BodyText = JsonSerializer.Serialize(body), FirmwareVersion = firmware
    });

    private static JsonElement Payload(ProtocolDispatchResult result)
    {
        using var document = JsonDocument.Parse(result.BodyText);
        return document.RootElement.Clone();
    }

    private static async Task<string> Prepare(JiboCloudProtocolService service, string id) =>
        Payload(await Call(service, "PrepareRobot", new { deviceId = id, rollbackSnapshotId = "synthetic-recovery" }))
            .GetProperty("token").GetString()!;

    [Fact]
    public async Task UnknownExplicitTokenCannotPrepareRegisterOrVerifyThePrimaryRobot()
    {
        var store = new InMemoryCloudStateStore(); var service = new JiboCloudProtocolService(store);
        var before = JsonSerializer.Serialize(store.GetDevices()); var loops = JsonSerializer.Serialize(store.GetLoops());
        var result = await Call(service, "SetupRobot", new { token = "not-issued", id = "incoming-a", rollbackSnapshotId = "synthetic" });
        Assert.Equal(409, result.StatusCode);
        Assert.False(Payload(await Call(service, "GetStatus", new { token = "not-issued" })).GetProperty("prepared").GetBoolean());
        var proof = Payload(await Call(service, "VerifyConnection", new { token = "not-issued" }));
        Assert.False(proof.GetProperty("connected").GetBoolean());
        Assert.Equal(before, JsonSerializer.Serialize(store.GetDevices()));
        Assert.Equal(loops, JsonSerializer.Serialize(store.GetLoops()));
    }

    [Fact]
    public async Task TokenlessCompatibilityDoesNotCreateAReusablePreparedToken()
    {
        var store = new InMemoryCloudStateStore(); var service = new JiboCloudProtocolService(store);
        Assert.Equal(200, (await Call(service, "SetupRobot", new { id = "legacy-a" })).StatusCode);
        Assert.Equal(409, (await Call(service, "ReconnectRobot", new { token = "oobe-implicit-legacy-a", id = "legacy-a", rollbackSnapshotId = "synthetic" })).StatusCode);
        Assert.False(Payload(await Call(service, "GetStatus", new { token = "oobe-implicit-legacy-a" })).GetProperty("prepared").GetBoolean());
    }

    [Fact]
    public async Task SerialConflictDoesNotChangeRegistrationLoopsOrTokenCompletion()
    {
        var store = new InMemoryCloudStateStore(); var service = new JiboCloudProtocolService(store);
        store.UpsertDevice(new DeviceRegistration { DeviceId = "incoming-a", RobotId = "robot-a", FirmwareVersion = "original",
            VerifiedSerialNumber = "BOJW-1000-0017-1114-0008" });
        var token = Payload(await Call(service, "PrepareRobot", new { deviceId = "incoming-a", rollbackSnapshotId = "synthetic",
            serialNumber = "BOJW-1000-0017-1114-0009", serialVerified = true, serialVerificationMethod = "physical-label" })).GetProperty("token").GetString();
        var before = JsonSerializer.Serialize(store.GetDevices()); var loops = JsonSerializer.Serialize(store.GetLoops());
        Assert.Equal(409, (await Call(service, "SetupRobot", new { token, id = "incoming-a" }, "changed")).StatusCode);
        var status = Payload(await Call(service, "GetStatus", new { token }));
        Assert.False(status.GetProperty("complete").GetBoolean());
        Assert.Equal(before, JsonSerializer.Serialize(store.GetDevices()));
        Assert.Equal(loops, JsonSerializer.Serialize(store.GetLoops()));
        // The failed claim remains retryable with corrected explicit evidence.
        Assert.Equal(200, (await Call(service, "SetupRobot", new { token, id = "incoming-a",
            serialNumber = "BOJW-1000-0017-1114-0008", serialVerified = true, serialVerificationMethod = "physical-label" })).StatusCode);
    }

    [Fact]
    public async Task CompletedTokenCannotBeReboundToAnotherRobot()
    {
        var store = new InMemoryCloudStateStore(); var service = new JiboCloudProtocolService(store);
        var token = await Prepare(service, "planning-alias");
        // Preserve the legacy first-setup alias behavior, then bind the completed identity.
        Assert.Equal(200, (await Call(service, "SetupRobot", new { token, id = "incoming-a" })).StatusCode);
        Assert.Equal(409, (await Call(service, "ReconnectRobot", new { token, id = "incoming-b" })).StatusCode);
        Assert.Null(store.FindDeviceByFriendlyId("incoming-b"));
        Assert.Equal("incoming-a", Payload(await Call(service, "GetStatus", new { token })).GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task FreshAndConcurrentRetryTokensKeepOneLoopAndPreserveTheCurrentRobot()
    {
        var store = new InMemoryCloudStateStore();
        store.UpdateRobot(new DeviceRegistration { DeviceId = "current-control", RobotId = "current-robot", RegistrationSource = RobotRegistrationSources.Physical });
        var primary = JsonSerializer.Serialize(store.GetRobot()); var profile = JsonSerializer.Serialize(store.GetRobotProfile());
        var people = JsonSerializer.Serialize(store.GetPeople());
        var service = new JiboCloudProtocolService(store);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Prepare(service, "incoming-a")));
        var replies = await Task.WhenAll(tokens.Select(token => Task.Run(() => Call(service, "SetupRobot", new { token, id = "incoming-a" }))));
        Assert.All(replies, reply => Assert.Equal(200, reply.StatusCode));
        var loop = Assert.Single(store.GetLoops(), loop => loop.RobotFriendlyId == "incoming-a");
        Assert.All(replies, reply => Assert.Equal(loop.LoopId, Payload(reply).GetProperty("loopId").GetString()));
        var second = await Prepare(service, "incoming-b");
        Assert.Equal(200, (await Call(service, "SetupRobot", new { token = second, id = "incoming-b" })).StatusCode);
        Assert.Single(store.GetLoops(), item => item.RobotFriendlyId == "incoming-b");
        Assert.Equal(primary, JsonSerializer.Serialize(store.GetRobot()));
        Assert.Equal(profile, JsonSerializer.Serialize(store.GetRobotProfile()));
        Assert.Equal(people, JsonSerializer.Serialize(store.GetPeople()));
        foreach (var token in tokens.Append(second))
            Assert.True(Payload(await Call(service, "VerifyConnection", new { token })).GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task MissingPreparedDeviceCannotBorrowPrimaryOrFriendlyNameProof()
    {
        var store = new InMemoryCloudStateStore();
        store.UpdateRobot(new DeviceRegistration { DeviceId = "primary", RobotId = "primary-robot", FriendlyName = "incoming-a",
            HostMappings = new Dictionary<string, string> { ["api.jibo.com"] = "api.openjibo.com" } });
        var service = new JiboCloudProtocolService(store); var token = await Prepare(service, "incoming-a");
        var proof = Payload(await Call(service, "VerifyConnection", new { token }));
        Assert.False(proof.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, proof.GetProperty("robotId").ValueKind);
        Assert.Empty(proof.GetProperty("storedHostMappings").EnumerateObject());
        Assert.Contains(proof.GetProperty("connectionBlockers").EnumerateArray(), item => item.GetString() == "prepared-device-not-registered");
    }

    [Fact]
    public async Task RegistrationFailureLeavesTokenIncompleteAndRetryReusesTheCreatedLoop()
    {
        var store = new InMemoryCloudStateStore();
        store.UpdateRobot(new DeviceRegistration { DeviceId = "current-control", RobotId = "current-robot" });
        var proxy = DispatchProxy.Create<ICloudStateStore, StoreProxy>(); var control = (StoreProxy)proxy;
        control.Store = store; control.FailNextUpsert = true;
        var service = new JiboCloudProtocolService(proxy); var token = await Prepare(service, "incoming-a");
        await Assert.ThrowsAsync<IOException>(() => Call(service, "SetupRobot", new { token, id = "incoming-a" }));
        Assert.False(Payload(await Call(service, "GetStatus", new { token })).GetProperty("complete").GetBoolean());
        var loop = Assert.Single(store.GetLoops(), item => item.RobotFriendlyId == "incoming-a");
        var retry = await Call(service, "SetupRobot", new { token, id = "incoming-a" });
        Assert.Equal(200, retry.StatusCode); Assert.Equal(loop.LoopId, Payload(retry).GetProperty("loopId").GetString());
        Assert.Single(store.GetLoops(), item => item.RobotFriendlyId == "incoming-a");
        // Even a completed token cannot claim connection through a missing registration.
        control.HideDevice = "incoming-a";
        Assert.False(Payload(await Call(service, "VerifyConnection", new { token })).GetProperty("connected").GetBoolean());
    }

    public class StoreProxy : DispatchProxy
    {
        public ICloudStateStore Store { get; set; } = null!;
        public bool FailNextUpsert { get; set; }
        public string? HideDevice { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ICloudStateStore.UpsertDevice) && FailNextUpsert)
            {
                FailNextUpsert = false; throw new IOException("Synthetic registration failure");
            }
            if (targetMethod.Name == nameof(ICloudStateStore.GetDevices) && HideDevice is not null)
                return Store.GetDevices().Where(item => item.DeviceId != HideDevice).ToArray();
            try { return targetMethod.Invoke(Store, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw;
            }
        }
    }
}
