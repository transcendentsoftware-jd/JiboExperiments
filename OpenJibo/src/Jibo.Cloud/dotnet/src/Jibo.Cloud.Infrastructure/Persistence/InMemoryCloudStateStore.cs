using System.Collections.Concurrent;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class InMemoryCloudStateStore : ICloudStateStore
{
    private readonly AccountProfile _account = new();
    private readonly ConcurrentDictionary<string, DeviceRegistration> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CloudSession> _sessionsByToken = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _symmetricKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, KeyRequestRecord> _keyRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UpdateManifest> _updates;
    private readonly List<MediaRecord> _media = [];
    private readonly List<BackupRecord> _backups = [];
    private readonly List<LoopRecord> _loops;
    private DeviceRegistration _robot;
    private RobotProfile _robotProfile;

    public InMemoryCloudStateStore()
    {
        _robot = new DeviceRegistration
        {
            HostMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api.jibo.com"] = "openjibo.com",
                ["api-socket.jibo.com"] = "openjibo.com",
                ["neo-hub.jibo.com"] = "openjibo.com"
            }
        };

        _devices[_robot.DeviceId] = _robot;
        _robotProfile = new RobotProfile
        {
            RobotId = _robot.RobotId,
            Payload = new Dictionary<string, object?>
            {
                ["SSID"] = "my-ssid",
                ["connectedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["platform"] = "12.10.0",
                ["serialNumber"] = _robot.DeviceId
            }
        };
        _loops =
        [
            new LoopRecord
            {
                OwnerAccountId = _account.AccountId,
                RobotId = _robot.RobotId,
                RobotFriendlyId = _robot.DeviceId
            }
        ];

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

    public RobotProfile GetRobotProfile() => _robotProfile;

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

    public IReadOnlyList<LoopRecord> GetLoops() => _loops.ToArray();

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

    public UpdateManifest CreateUpdate(string? fromVersion, string? toVersion, string? changes, string? shaHash, long? length, string? subsystem, string? filter, IDictionary<string, object?>? dependencies)
    {
        var update = new UpdateManifest
        {
            UpdateId = $"upd-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            FromVersion = fromVersion ?? "unknown",
            ToVersion = toVersion ?? fromVersion ?? "unknown",
            Changes = changes ?? string.Empty,
            Url = $"https://api.jibo.com/update/upd-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            ShaHash = shaHash ?? "fake-sha-hash",
            Length = length ?? 0,
            Subsystem = subsystem ?? "unknown",
            Filter = filter
        };

        _updates.Add(update);
        return update;
    }

    public UpdateManifest RemoveUpdate(string? updateId)
    {
        var existing = _updates.FirstOrDefault(update => update.UpdateId == updateId);
        if (existing is not null)
        {
            _updates.Remove(existing);
            return existing;
        }

        return new UpdateManifest
        {
            UpdateId = updateId ?? "unknown-update",
            Changes = "Update not found",
            Url = "https://api.jibo.com/update/missing",
            ShaHash = "missing",
            Subsystem = "unknown"
        };
    }

    public IReadOnlyList<MediaRecord> ListMedia(IReadOnlyList<string>? loopIds = null, long? after = null, long? before = null)
    {
        return _media
            .Where(item => loopIds is null || loopIds.Count == 0 || loopIds.Contains(item.LoopId))
            .Where(item => after is null || item.CreatedUtc.ToUnixTimeMilliseconds() > after)
            .Where(item => before is null || item.CreatedUtc.ToUnixTimeMilliseconds() < before)
            .ToArray();
    }

    public IReadOnlyList<MediaRecord> GetMedia(IReadOnlyList<string> paths)
    {
        return _media.Where(item => paths.Contains(item.Path)).ToArray();
    }

    public IReadOnlyList<MediaRecord> RemoveMedia(IReadOnlyList<string> paths)
    {
        var replacements = new List<MediaRecord>();
        for (var i = 0; i < _media.Count; i++)
        {
            if (!paths.Contains(_media[i].Path))
            {
                continue;
            }

            var updated = new MediaRecord
            {
                Path = _media[i].Path,
                CreatedUtc = _media[i].CreatedUtc,
                MediaType = _media[i].MediaType,
                Reference = _media[i].Reference,
                AccountId = _media[i].AccountId,
                LoopId = _media[i].LoopId,
                Url = _media[i].Url,
                IsEncrypted = _media[i].IsEncrypted,
                IsDeleted = true,
                Meta = _media[i].Meta
            };

            _media[i] = updated;
            replacements.Add(updated);
        }

        return replacements;
    }

    public MediaRecord CreateMedia(string loopId, string path, string type, string reference, bool isEncrypted, IDictionary<string, object?>? meta)
    {
        var item = new MediaRecord
        {
            Path = path,
            MediaType = type,
            Reference = reference,
            AccountId = _account.AccountId,
            LoopId = loopId,
            Url = $"https://api.jibo.com/media/{Uri.EscapeDataString(path)}",
            IsEncrypted = isEncrypted,
            Meta = meta ?? new Dictionary<string, object?>()
        };

        _media.Add(item);
        return item;
    }

    public IReadOnlyList<BackupRecord> GetBackups() => _backups.ToArray();

    public bool ShouldCreateSymmetricKey(string loopId) => true;

    public string GetOrCreateSymmetricKey(string loopId)
    {
        return _symmetricKeys.GetOrAdd(loopId, key => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"open-jibo-symmetric-key:{key}")));
    }

    public KeyRequestRecord CreateKeyRequest(string loopId, string publicKey)
    {
        var record = new KeyRequestRecord
        {
            RequestId = $"req-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            LoopId = loopId,
            PublicKey = publicKey
        };

        _keyRequests[record.RequestId] = record;
        return record;
    }

    public KeyRequestRecord GetKeyRequest(string loopId, string? requestId, string? publicKey)
    {
        if (!string.IsNullOrWhiteSpace(requestId) && _keyRequests.TryGetValue(requestId, out var record))
        {
            return record;
        }

        return new KeyRequestRecord
        {
            RequestId = requestId ?? "unknown-request",
            LoopId = loopId,
            PublicKey = publicKey ?? string.Empty
        };
    }

    public IReadOnlyList<KeyRequestRecord> GetIncomingKeyRequests() => [];

    public IReadOnlyList<KeyRequestRecord> GetBinaryRequests() => [];

    public IReadOnlyList<object> GetHolidays()
    {
        return
        [
            new
            {
                id = "easter-1",
                eventId = (string?)null,
                name = "Easter",
                category = "holiday",
                subcategory = (string?)null,
                loopId = _loops[0].LoopId,
                memberId = (string?)null,
                isEnabled = true,
                date = "2026-04-05",
                endDate = (string?)null,
                created = DateTimeOffset.UtcNow.ToString("O")
            }
        ];
    }

    public void UpdateRobot(DeviceRegistration registration)
    {
        _robot = registration;
        _devices[registration.DeviceId] = registration;
        _robotProfile = new RobotProfile
        {
            RobotId = registration.RobotId,
            Payload = new Dictionary<string, object?>
            {
                ["SSID"] = "my-ssid",
                ["connectedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["platform"] = registration.FirmwareVersion ?? "12.10.0",
                ["serialNumber"] = registration.DeviceId
            },
            UpdatedUtc = DateTimeOffset.UtcNow
        };
    }
}
