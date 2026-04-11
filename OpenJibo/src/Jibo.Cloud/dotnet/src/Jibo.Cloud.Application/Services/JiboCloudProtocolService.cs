using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboCloudProtocolService(ICloudStateStore stateStore)
{
    private static readonly string[] AcceptedHosts =
    [
        "api.jibo.com",
        "openjibo.com",
        "openjibo.ai",
        "localhost"
    ];

    public Task<ProtocolDispatchResult> DispatchAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path == "/" &&
            string.IsNullOrWhiteSpace(envelope.ServicePrefix))
        {
            return Task.FromResult(ProtocolDispatchResult.NoContent());
        }

        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ProtocolDispatchResult.Ok(new { ok = true, host = envelope.HostName }));
        }

        if (envelope.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) &&
            (envelope.Path.Equals("/upload/asr-binary", StringComparison.OrdinalIgnoreCase) ||
             envelope.Path.Equals("/upload/log-events", StringComparison.OrdinalIgnoreCase) ||
             envelope.Path.Equals("/upload/log-binary", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(ProtocolDispatchResult.Raw(200, string.Empty));
        }

        if (!AcceptedHosts.Contains(envelope.HostName, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(ProtocolDispatchResult.Ok(new
            {
                ok = true,
                accepted = false,
                host = envelope.HostName
            }));
        }

        var servicePrefix = envelope.ServicePrefix ?? string.Empty;
        var operation = envelope.Operation ?? string.Empty;

        if (servicePrefix.StartsWith("Account_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleAccount(operation));
        }

        if (servicePrefix.StartsWith("Notification_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleNotification(operation, envelope));
        }

        if (servicePrefix.StartsWith("Loop_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleLoop(operation));
        }

        if (servicePrefix.StartsWith("Robot_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleRobot(operation, envelope));
        }

        if (servicePrefix.StartsWith("Update_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleUpdate(operation, envelope));
        }

        return Task.FromResult(ProtocolDispatchResult.Ok(new
        {
            ok = true,
            service = servicePrefix,
            operation
        }));
    }

    private ProtocolDispatchResult HandleAccount(string operation)
    {
        var account = stateStore.GetAccount();

        return operation switch
        {
            "CreateHubToken" => ProtocolDispatchResult.Ok(new
            {
                token = stateStore.IssueHubToken(),
                expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            }),
            "CreateAccessToken" => ProtocolDispatchResult.Ok(new
            {
                token = $"access-{account.AccountId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            }),
            "Get" => ProtocolDispatchResult.Ok(new[]
            {
                new
                {
                    id = account.AccountId,
                    email = account.Email,
                    firstName = account.FirstName,
                    lastName = account.LastName,
                    accessKeyId = account.AccessKeyId,
                    secretAccessKey = account.SecretAccessKey
                }
            }),
            _ => ProtocolDispatchResult.Ok(new
            {
                id = account.AccountId,
                email = account.Email,
                firstName = account.FirstName,
                lastName = account.LastName
            })
        };
    }

    private ProtocolDispatchResult HandleNotification(string operation, ProtocolEnvelope envelope)
    {
        if (!operation.Equals("NewRobotToken", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new { ok = true, operation });
        }

        var body = envelope.TryParseBody();
        var deviceId = envelope.DeviceId
            ?? ReadString(body, "deviceId")
            ?? ReadString(body, "serialNumber")
            ?? "unknown-device";

        stateStore.GetOrCreateDevice(deviceId, envelope.FirmwareVersion, envelope.ApplicationVersion);

        return ProtocolDispatchResult.Ok(new
        {
            token = stateStore.IssueRobotToken(deviceId)
        });
    }

    private ProtocolDispatchResult HandleLoop(string operation)
    {
        if (operation is not ("List" or "ListLoops"))
        {
            return ProtocolDispatchResult.Ok(Array.Empty<object>());
        }

        var robot = stateStore.GetRobot();
        var account = stateStore.GetAccount();

        return ProtocolDispatchResult.Ok(new[]
        {
            new
            {
                id = "openjibo-default-loop",
                name = "OpenJibo Default Loop",
                owner = account.AccountId,
                robot = robot.RobotId,
                robotFriendlyId = robot.DeviceId,
                members = Array.Empty<object>(),
                isSuspended = false,
                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        });
    }

    private ProtocolDispatchResult HandleRobot(string operation, ProtocolEnvelope envelope)
    {
        var robot = stateStore.GetRobot();

        if (operation.Equals("UpdateRobot", StringComparison.OrdinalIgnoreCase))
        {
            var updated = new DeviceRegistration
            {
                DeviceId = robot.DeviceId,
                RobotId = robot.RobotId,
                FriendlyName = robot.FriendlyName,
                FirmwareVersion = envelope.FirmwareVersion ?? robot.FirmwareVersion,
                ApplicationVersion = envelope.ApplicationVersion ?? robot.ApplicationVersion,
                HostMappings = robot.HostMappings
            };

            stateStore.UpdateRobot(updated);
            robot = updated;
        }

        return ProtocolDispatchResult.Ok(new
        {
            id = robot.RobotId,
            friendlyId = robot.DeviceId,
            name = robot.FriendlyName,
            firmwareVersion = robot.FirmwareVersion,
            applicationVersion = robot.ApplicationVersion
        });
    }

    private ProtocolDispatchResult HandleUpdate(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();
        var subsystem = ReadString(body, "subsystem");
        var filter = ReadString(body, "filter");
        var fromVersion = ReadString(body, "fromVersion");

        return operation switch
        {
            "ListUpdates" => ProtocolDispatchResult.Ok(stateStore.ListUpdates(subsystem, filter).Select(MapUpdate).ToArray()),
            "ListUpdatesFrom" => ProtocolDispatchResult.Ok(stateStore.ListUpdates(subsystem, filter).Select(MapUpdate).ToArray()),
            "GetUpdateFrom" => ProtocolDispatchResult.Ok(MapUpdate(stateStore.GetUpdateFrom(subsystem, fromVersion, filter))),
            _ => ProtocolDispatchResult.Ok(Array.Empty<object>())
        };
    }

    private static object MapUpdate(UpdateManifest update)
    {
        return new
        {
            _id = update.UpdateId,
            created = update.CreatedUtc.ToUnixTimeMilliseconds(),
            fromVersion = update.FromVersion,
            toVersion = update.ToVersion,
            changes = update.Changes,
            url = update.Url,
            shaHash = update.ShaHash,
            length = update.Length,
            subsystem = update.Subsystem,
            filter = update.Filter
        };
    }

    private static string? ReadString(JsonElement? element, string propertyName)
    {
        if (element is null)
        {
            return null;
        }

        if (!element.Value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }
}
