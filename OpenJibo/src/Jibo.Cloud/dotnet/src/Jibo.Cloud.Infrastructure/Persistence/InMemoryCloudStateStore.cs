using System.Collections.Concurrent;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class InMemoryCloudStateStore : ICloudStateStore
{
    private readonly AccountProfile _account = new();
    private readonly ConcurrentDictionary<string, DeviceRegistration> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CloudSession> _sessionsByToken = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UpdateManifest> _updates;
    private DeviceRegistration _robot;

    public InMemoryCloudStateStore()
    {
        _robot = new DeviceRegistration
        {
            HostMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api.jibo.com"] = "openjibo.com",
                ["api-socket.jibo.com"] = "openjibo.com",
                ["neo-hub.jibo.com"] = "openjibo.com",
                ["neohub.jibo.com"] = "openjibo.com"
            }
        };

        _devices[_robot.DeviceId] = _robot;

        _updates =
        [
            new UpdateManifest
            {
                UpdateId = "noop-update-robot",
                FromVersion = "unknown",
                ToVersion = "unknown",
                Changes = "No update available",
                Url = "https://api.jibo.com/update/noop",
                ShaHash = "noop",
                Subsystem = "robot"
            }
        ];
    }

    public AccountProfile GetAccount() => _account;

    public DeviceRegistration GetRobot() => _robot;

    public DeviceRegistration GetOrCreateDevice(string deviceId, string? firmwareVersion, string? applicationVersion)
    {
        return _devices.AddOrUpdate(
            deviceId,
            _ => new DeviceRegistration
            {
                DeviceId = deviceId,
                RobotId = $"robot-{deviceId}",
                FriendlyName = "OpenJibo Registered Robot",
                FirmwareVersion = firmwareVersion,
                ApplicationVersion = applicationVersion
            },
            (_, current) => new DeviceRegistration
            {
                DeviceId = current.DeviceId,
                RobotId = current.RobotId,
                FriendlyName = current.FriendlyName,
                FirmwareVersion = firmwareVersion ?? current.FirmwareVersion,
                ApplicationVersion = applicationVersion ?? current.ApplicationVersion,
                HostMappings = current.HostMappings
            });
    }

    public string IssueHubToken()
    {
        var token = $"hub-{_account.AccountId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        _sessionsByToken[token] = new CloudSession
        {
            Kind = "hub",
            AccountId = _account.AccountId,
            Token = token,
            DeviceId = _robot.DeviceId
        };

        return token;
    }

    public string IssueRobotToken(string deviceId)
    {
        var token = $"token-{deviceId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        _sessionsByToken[token] = new CloudSession
        {
            Kind = "robot",
            AccountId = _account.AccountId,
            Token = token,
            DeviceId = deviceId
        };

        return token;
    }

    public CloudSession OpenSession(string kind, string? deviceId, string? token, string? hostName, string? path)
    {
        var session = new CloudSession
        {
            Kind = kind,
            AccountId = _account.AccountId,
            DeviceId = deviceId ?? _robot.DeviceId,
            Token = token,
            HostName = hostName,
            Path = path
        };

        if (!string.IsNullOrWhiteSpace(token))
        {
            _sessionsByToken[token] = session;
        }

        return session;
    }

    public CloudSession? FindSessionByToken(string token)
    {
        return _sessionsByToken.GetValueOrDefault(token);
    }

    public IReadOnlyList<UpdateManifest> ListUpdates(string? subsystem = null, string? filter = null)
    {
        return _updates
            .Where(update => subsystem is null || update.Subsystem.Equals(subsystem, StringComparison.OrdinalIgnoreCase))
            .Where(update => filter is null || string.Equals(update.Filter, filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public UpdateManifest GetUpdateFrom(string? subsystem, string? fromVersion, string? filter)
    {
        return ListUpdates(subsystem, filter).FirstOrDefault() ?? new UpdateManifest
        {
            UpdateId = $"noop-update-{subsystem ?? "robot"}-{fromVersion ?? "unknown"}",
            FromVersion = fromVersion ?? "unknown",
            ToVersion = fromVersion ?? "unknown",
            Filter = filter,
            Subsystem = subsystem ?? "robot"
        };
    }

    public void UpdateRobot(DeviceRegistration registration)
    {
        _robot = registration;
        _devices[registration.DeviceId] = registration;
    }
}
