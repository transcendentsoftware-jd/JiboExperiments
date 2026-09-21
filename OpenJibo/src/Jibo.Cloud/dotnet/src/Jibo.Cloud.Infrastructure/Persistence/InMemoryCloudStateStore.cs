using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Holidays;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class InMemoryCloudStateStore : ICloudStateStore
{
    private const string CurrentSchemaVersion = "1";
    private const int IdentityGraphSnapshotVersion = 1;
    private const string IdentityGraphSignatureAlgorithm = "HMAC-SHA256";
    private const string IdentityGraphSignatureKeyId = "open-jibo-local-snapshot-v1";
    private const string IdentityGraphSigningKey = "open-jibo-local-identity-graph-development-key";
    private const string IdentityGraphAdmissionSignatureKeyId = "open-jibo-local-admission-v1";
    private const string IdentityGraphEvidenceBundleSignatureKeyId = "open-jibo-local-evidence-bundle-v1";
    private const string TrustedServerAdmissionSignatureKeyId = "open-jibo-local-trusted-server-admission-v1";
    private const string TrustedServerAdmissionSigningKey = "open-jibo-local-trusted-server-admission-development-key";
    private static long _nextUpdateIdSeed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static readonly JsonSerializerOptions PersistenceJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<BackupRecord> _backups = [];
    private readonly List<CalendarEventRecord> _calendarEvents = [];
    private readonly List<CommuteProfileRecord> _commuteProfiles = [];
    private readonly ConcurrentDictionary<string, DeviceRegistration> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RobotCredentialBinding> _robotCredentialBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GreetingPresenceRecord> _greetingPresences = [];

    private readonly IHolidayCalendarProvider _holidayCalendarProvider;
    private readonly List<HolidayRecord> _holidayOverrides = [];

    private readonly ConcurrentDictionary<string, KeyRequestRecord>
        _keyRequests = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<LoopMemberRecord> _loopMembers;

    private readonly List<LoopRecord> _loops;
    private readonly List<MediaRecord> _media = [];
    private readonly string? _ownerFirstName;
    private readonly string? _ownerLastName;
    private readonly List<PersonRecord> _people;
    private readonly List<RecognitionObservationRecord> _recognitionObservations = [];
    private readonly HashSet<string> _revokedIdentityGraphAnchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrustedServerAdmissionRecord> _trustedServerAdmissions = [];
    private readonly List<TrustedServerRecord> _trustedServers = [];
    private readonly List<UserDeviceLink> _userDeviceLinks = [];

    private readonly BoundedCloudSessionRegistry _sessions;

    private readonly ISnapshotStore _snapshotStore;
    private readonly ConcurrentDictionary<string, string> _symmetricKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _syncRoot = new();
    private readonly List<UpdateManifest> _updates;
    private readonly List<UserRecord> _users;

    private AccountProfile _account = new();
    private DateTimeOffset? _lastLoadedUtc;
    private DateTimeOffset? _lastSavedUtc;
    private long _revision;
    private DeviceRegistration _robot;
    private RobotProfile _robotProfile;

    public InMemoryCloudStateStore(string? persistencePath = null)
        : this(new JsonFileSnapshotStore(persistencePath, PersistenceJsonOptions))
    {
    }

    public InMemoryCloudStateStore(ISnapshotStore snapshotStore)
        : this(snapshotStore, new NagerDateHolidayCalendarProvider())
    {
    }

    public InMemoryCloudStateStore(ISnapshotStore snapshotStore, IHolidayCalendarProvider holidayCalendarProvider,
        string? ownerFirstName = null, string? ownerLastName = null, ITransportMetrics? transportMetrics = null)
    {
        _snapshotStore = snapshotStore;
        _holidayCalendarProvider = holidayCalendarProvider;
        _ownerFirstName = ownerFirstName;
        _ownerLastName = ownerLastName;
        _sessions = new BoundedCloudSessionRegistry(transportMetrics: transportMetrics);
        var bootstrapDeviceId = CreateBootstrapDeviceId();
        _robot = new DeviceRegistration
        {
            DeviceId = bootstrapDeviceId,
            RobotId = bootstrapDeviceId,
            FriendlyName = "OpenJibo Dev Robot",
            RegistrationSource = RobotRegistrationSources.Bootstrap,
            IsHidden = true,
            ArchivedUtc = DateTimeOffset.UtcNow,
            HostMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["api.jibo.com"] = "openjibo.com",
                ["api.openjibo.com"] = "openjibo.com",
                ["api-socket.jibo.com"] = "openjibo.com",
                ["open-jibo-socket.openjibo.com"] = "openjibo.com",
                ["neo-hub.jibo.com"] = "openjibo.com",
                ["neohub.openjibo.com"] = "openjibo.com"
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
        _loopMembers = [];
        _users = [];
        _people =
        [
            new PersonRecord
            {
                PersonId = "person-openjibo-owner",
                AccountId = _account.AccountId,
                LoopId = _loops[0].LoopId,
                RobotId = _robot.RobotId,
                DisplayName = $"{_account.FirstName} {_account.LastName}",
                Alias = _account.FirstName,
                IsPrimary = true
            },
            new PersonRecord
            {
                PersonId = "person-openjibo-household-member",
                AccountId = _account.AccountId,
                LoopId = _loops[0].LoopId,
                RobotId = _robot.RobotId,
                DisplayName = "OpenJibo Household Member",
                Alias = "Household Member",
                IsPrimary = false
            }
        ];

        _updates = [];
        ApplyConfiguredOwnerName();
        EnsureDefaultTrustedServers();
        EnsureDefaultTopology();
        LoadPersistedState();
    }

    public PersistenceStateInfo GetPersistenceStateInfo()
    {
        return new PersistenceStateInfo(
            CurrentSchemaVersion,
            Interlocked.Read(ref _revision),
            _lastLoadedUtc,
            _lastSavedUtc);
    }

    public void LoadPersistedState()
    {
        lock (_syncRoot)
        {
            var snapshot = _snapshotStore.Load<PersistentStateSnapshot>();
            if (snapshot is null) return;

            var cleanupApplied = ApplySnapshot(snapshot);
            if (cleanupApplied)
            {
                Interlocked.Increment(ref _revision);
                SavePersistedStateLocked(DateTimeOffset.UtcNow);
            }
        }
    }

    public void SavePersistedState()
    {
        lock (_syncRoot)
        {
            SavePersistedStateLocked(DateTimeOffset.UtcNow);
        }
    }

    public AccountProfile GetAccount()
    {
        return _account;
    }

    public DeviceRegistration GetRobot()
    {
        return _robot;
    }

    public IReadOnlyList<DeviceRegistration> GetDevices()
    {
        lock (_syncRoot)
        {
            return _devices.Values.Select(device => CloneDeviceRegistration(device)).ToArray();
        }
    }

    public IReadOnlyList<DeviceRegistration> GetDevicesForAdministration() => GetDevices();

    public IReadOnlyList<CloudSession> GetSessions()
    {
        return _sessions.Values.Select(CloneSession).ToArray();
    }

    public RobotProfile GetRobotProfile()
    {
        return _robotProfile;
    }

    public DeviceRegistration GetOrCreateDevice(string deviceId, string? firmwareVersion, string? applicationVersion,
        string? registrationSource = null)
    {
        var source = RobotRegistrationSources.Normalize(registrationSource, deviceId);
        if (source == RobotRegistrationSources.DeploymentSmoke ||
            RobotRegistrationSources.IsDeploymentSmokeNamespace(deviceId))
            throw new InvalidOperationException("Deployment-smoke devices require authenticated smoke registration.");
        return GetOrCreateDeviceCore(deviceId, firmwareVersion, applicationVersion, source);
    }

    public DeviceRegistration GetOrCreateDeploymentSmokeDevice(DeploymentSmokeRegistrationAuthorization authorization,
        string? firmwareVersion, string? applicationVersion)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!RobotRegistrationSources.IsAllowedDeploymentSmokeDeviceId(authorization.DeviceId,
                authorization.MaxConcurrentDevices))
            throw new InvalidOperationException("Deployment-smoke device ID is outside the configured bounded set.");
        return GetOrCreateDeviceCore(authorization.DeviceId.Trim(), firmwareVersion, applicationVersion,
            RobotRegistrationSources.DeploymentSmoke);
    }

    private DeviceRegistration GetOrCreateDeviceCore(string deviceId, string? firmwareVersion,
        string? applicationVersion, string source)
    {
        var device = _devices.AddOrUpdate(
            deviceId,
            _ => new DeviceRegistration
            {
                DeviceId = deviceId,
                RobotId = $"robot-{deviceId}",
                FriendlyName = "OpenJibo Registered Robot",
                FirmwareVersion = firmwareVersion,
                ApplicationVersion = applicationVersion,
                RegistrationSource = source,
                IsHidden = RobotRegistrationSources.IsSynthetic(source),
                ArchivedUtc = RobotRegistrationSources.IsSynthetic(source) ? DateTimeOffset.UtcNow : null
            },
            (_, current) => new DeviceRegistration
            {
                DeviceId = current.DeviceId,
                RobotId = current.RobotId,
                FriendlyName = current.FriendlyName,
                FirmwareVersion = firmwareVersion ?? current.FirmwareVersion,
                ApplicationVersion = applicationVersion ?? current.ApplicationVersion,
                CertificateThumbprint = current.CertificateThumbprint,
                IssuedIdentityId = current.IssuedIdentityId,
                BuildHash = current.BuildHash,
                ConfigHash = current.ConfigHash,
                VerifiedSerialNumber = current.VerifiedSerialNumber,
                SerialEvidenceSource = current.SerialEvidenceSource,
                SerialEvidenceVerifiedUtc = current.SerialEvidenceVerifiedUtc,
                RegistrationSource = current.RegistrationSource == RobotRegistrationSources.Unknown &&
                                     source != RobotRegistrationSources.Unknown
                    ? source
                    : current.RegistrationSource,
                IsHidden = current.IsHidden,
                ArchivedUtc = current.ArchivedUtc,
                HostMappings = new Dictionary<string, string>(current.HostMappings, StringComparer.OrdinalIgnoreCase)
            });

        TouchState();
        return device;
    }

    public DeviceRegistration UpsertDevice(DeviceRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (string.IsNullOrWhiteSpace(registration.DeviceId))
            throw new ArgumentException("DeviceId is required.", nameof(registration));
        if ((RobotRegistrationSources.IsDeploymentSmokeNamespace(registration.DeviceId) ||
             RobotRegistrationSources.Normalize(registration.RegistrationSource, registration.DeviceId) ==
             RobotRegistrationSources.DeploymentSmoke) &&
            !_devices.ContainsKey(registration.DeviceId.Trim()))
            throw new InvalidOperationException("Deployment-smoke devices require authenticated smoke registration.");

        _devices[registration.DeviceId.Trim()] = registration;
        TouchState();
        return registration;
    }

    public DeviceRegistration UpsertDeviceForAdministration(DeviceRegistration registration) => UpsertDevice(registration);

    public DeviceRegistration RenameDevice(string deviceId, string robotId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(robotId))
            throw new ArgumentException("Device and robot IDs are required.");
        lock (_syncRoot)
        {
            if (!_devices.TryGetValue(deviceId.Trim(), out var existing))
                throw new KeyNotFoundException("Robot record was not found.");
            if (existing.IsHidden || existing.ArchivedUtc is not null)
                throw new InvalidOperationException("Archived robot records cannot be renamed.");
            if (_devices.Values.Any(item => !item.DeviceId.Equals(existing.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                                            item.RobotId.Equals(robotId.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                            !item.IsHidden))
                throw new InvalidOperationException("That robot ID already exists; use merge instead.");

            var renamed = new DeviceRegistration
            {
                DeviceId = existing.DeviceId,
                RobotId = robotId.Trim(),
                FriendlyName = robotId.Trim(),
                FirmwareVersion = existing.FirmwareVersion,
                ApplicationVersion = existing.ApplicationVersion,
                IsActive = existing.IsActive,
                CertificateThumbprint = existing.CertificateThumbprint,
                IssuedIdentityId = existing.IssuedIdentityId,
                BuildHash = existing.BuildHash,
                ConfigHash = existing.ConfigHash,
                VerifiedSerialNumber = existing.VerifiedSerialNumber,
                SerialEvidenceSource = existing.SerialEvidenceSource,
                SerialEvidenceVerifiedUtc = existing.SerialEvidenceVerifiedUtc,
                RegistrationSource = existing.RegistrationSource,
                HostMappings = new Dictionary<string, string>(existing.HostMappings, StringComparer.OrdinalIgnoreCase)
            };
            _devices[existing.DeviceId] = renamed;
            TouchState();
            return renamed;
        }
    }

    public DeviceRegistration RenameDeviceForAdministration(string deviceId, string robotId) => RenameDevice(deviceId, robotId);

    public DeviceRegistration RenameDeviceName(string deviceId, string name)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Device ID and name are required.");

        lock (_syncRoot)
        {
            if (!_devices.TryGetValue(deviceId.Trim(), out var existing))
                throw new KeyNotFoundException("Robot record was not found.");
            if (existing.IsHidden || existing.ArchivedUtc is not null)
                throw new InvalidOperationException("Archived robot records cannot be renamed.");

            var renamed = CloneDeviceRegistration(existing, name.Trim());
            _devices[existing.DeviceId] = renamed;
            if (existing.DeviceId.Equals(_robot.DeviceId, StringComparison.OrdinalIgnoreCase))
                _robot = renamed;
            TouchState();
            return renamed;
        }
    }

    public DeviceRegistration? FindDeviceByFriendlyId(string friendlyId)
    {
        if (string.IsNullOrWhiteSpace(friendlyId)) return null;

        var trimmed = friendlyId.Trim();
        return _devices.Values.FirstOrDefault(device =>
            IdentityMatches(device.DeviceId, trimmed) ||
            IdentityMatches(device.RobotId, trimmed) ||
            IdentityMatches(device.FriendlyName, trimmed));
    }

    public IReadOnlyList<DeviceRegistration> FindVisibleIdentityCandidates(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return [];

        var normalized = identity.Trim();
        lock (_syncRoot)
        {
            var candidates = _devices.Values
                .Where(device => !device.IsHidden && device.ArchivedUtc is null)
                .Select(device => (Device: device, Priority: IdentityPriority(device, normalized)))
                .Where(candidate => candidate.Priority >= 0)
                .ToArray();
            if (candidates.Length == 0) return [];

            var minimumPriority = candidates.Min(candidate => candidate.Priority);
            return candidates
                .Where(candidate => candidate.Priority == minimumPriority)
                .OrderBy(candidate => candidate.Device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Select(candidate => CloneDeviceRegistration(candidate.Device))
                .ToArray();
        }
    }

    private static int IdentityPriority(DeviceRegistration device, string identity)
    {
        if (string.Equals(device.DeviceId, identity, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.Equals(device.VerifiedSerialNumber, identity, StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(device.RobotId, identity, StringComparison.OrdinalIgnoreCase)) return 2;
        if (string.Equals(device.FriendlyName, identity, StringComparison.OrdinalIgnoreCase)) return 3;
        return -1;
    }

    public DeviceRegistration? FindDeviceByAwsCredentialFingerprint(string accessKeyFingerprint)
    {
        if (string.IsNullOrWhiteSpace(accessKeyFingerprint)) return null;
        return _robotCredentialBindings.TryGetValue(accessKeyFingerprint.Trim(), out var binding) &&
               _devices.TryGetValue(binding.DeviceId, out var device)
            ? CloneDeviceRegistration(device)
            : null;
    }

    public IReadOnlyList<RobotCredentialBinding> GetRobotCredentialBindings() =>
        _robotCredentialBindings.Values.OrderByDescending(binding => binding.ClaimedUtc).ToArray();

    public RobotCredentialBinding BindAwsCredentialFingerprint(string deviceId, string accessKeyFingerprint,
        string claimSource)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device ID is required.", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(accessKeyFingerprint))
            throw new ArgumentException("Credential fingerprint is required.", nameof(accessKeyFingerprint));
        if (!_devices.ContainsKey(deviceId.Trim()))
            throw new KeyNotFoundException("Robot record was not found.");

        var binding = new RobotCredentialBinding(accessKeyFingerprint.Trim(), deviceId.Trim(), DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(claimSource) ? "admin-claim" : claimSource.Trim());
        _robotCredentialBindings.AddOrUpdate(binding.AccessKeyFingerprint, binding,
            (_, existing) => existing.DeviceId.Equals(binding.DeviceId, StringComparison.OrdinalIgnoreCase)
                ? binding with { ClaimedUtc = existing.ClaimedUtc, ClaimSource = existing.ClaimSource }
                : throw new InvalidOperationException("Credential fingerprint is already claimed by another robot."));
        TouchState();
        return binding;
    }

    public IReadOnlyList<RobotCredentialBinding> SwapAwsCredentialFingerprintBindings(string firstAccessKeyFingerprint,
        string secondAccessKeyFingerprint, string claimSource)
    {
        if (string.IsNullOrWhiteSpace(firstAccessKeyFingerprint) || string.IsNullOrWhiteSpace(secondAccessKeyFingerprint) ||
            firstAccessKeyFingerprint.Equals(secondAccessKeyFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose two different credential fingerprints.");

        lock (_syncRoot)
        {
            if (!_robotCredentialBindings.TryGetValue(firstAccessKeyFingerprint.Trim(), out var first) ||
                !_robotCredentialBindings.TryGetValue(secondAccessKeyFingerprint.Trim(), out var second))
                throw new KeyNotFoundException("Credential fingerprint was not found.");
            if (first.DeviceId.Equals(second.DeviceId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The selected credential fingerprints are already assigned to the same robot.");

            var now = DateTimeOffset.UtcNow;
            var source = string.IsNullOrWhiteSpace(claimSource) ? "portal-admin-swap" : claimSource.Trim();
            var swappedFirst = first with { DeviceId = second.DeviceId, ClaimedUtc = now, ClaimSource = source };
            var swappedSecond = second with { DeviceId = first.DeviceId, ClaimedUtc = now, ClaimSource = source };
            _robotCredentialBindings[swappedFirst.AccessKeyFingerprint] = swappedFirst;
            _robotCredentialBindings[swappedSecond.AccessKeyFingerprint] = swappedSecond;
            TouchState();
            return [swappedFirst, swappedSecond];
        }
    }

    public RobotMergeResult MergeRobotRecords(string sourceDeviceId, string targetDeviceId)
    {
        if (string.IsNullOrWhiteSpace(sourceDeviceId) || string.IsNullOrWhiteSpace(targetDeviceId) ||
            sourceDeviceId.Equals(targetDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose two different robot records.");
        if (sourceDeviceId.Equals(_robot.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The active robot record must be the canonical target, not the merge source.");
        if (!_devices.TryGetValue(sourceDeviceId, out var source) ||
            !_devices.TryGetValue(targetDeviceId, out var target))
            throw new KeyNotFoundException("Robot record was not found.");
        if (source.IsHidden || source.ArchivedUtc is not null || target.IsHidden || target.ArchivedUtc is not null)
            throw new InvalidOperationException("Robot merge requires two visible, unarchived records.");

        var migratedSessions = 0;
        foreach (var session in _sessions.Values.Where(session =>
                     source.DeviceId.Equals(session.DeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            // Keep connection-scoped observed identities intact. The explicit
            // inventory binding is the durable merge result; retaining these raw
            // IDs lets future reconnects inherit it instead of recreating the
            // archived record. Stable issued robot tokens can be migrated directly.
            if (!string.IsNullOrWhiteSpace(session.Token) &&
                !session.Token.StartsWith("conn:", StringComparison.OrdinalIgnoreCase))
                session.DeviceId = targetDeviceId;
            session.Metadata["registeredDeviceId"] = targetDeviceId;
            session.Metadata["registeredRobotId"] = targetDeviceId;
            migratedSessions++;
        }

        var migratedBindings = 0;
        foreach (var binding in _robotCredentialBindings.Values.Where(binding =>
                     source.DeviceId.Equals(binding.DeviceId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _robotCredentialBindings[binding.AccessKeyFingerprint] = binding with { DeviceId = targetDeviceId };
            migratedBindings++;
        }

        _devices[source.DeviceId] = new DeviceRegistration
        {
            DeviceId = source.DeviceId,
            RobotId = source.RobotId,
            FriendlyName = source.FriendlyName,
            FirmwareVersion = source.FirmwareVersion,
            ApplicationVersion = source.ApplicationVersion,
            IsActive = false,
            CertificateThumbprint = source.CertificateThumbprint,
            IssuedIdentityId = source.IssuedIdentityId,
            BuildHash = source.BuildHash,
            ConfigHash = source.ConfigHash,
            VerifiedSerialNumber = source.VerifiedSerialNumber,
            SerialEvidenceSource = source.SerialEvidenceSource,
            SerialEvidenceVerifiedUtc = source.SerialEvidenceVerifiedUtc,
            RegistrationSource = source.RegistrationSource,
            IsHidden = true,
            ArchivedUtc = DateTimeOffset.UtcNow,
            HostMappings = new Dictionary<string, string>(source.HostMappings, StringComparer.OrdinalIgnoreCase)
        };
        _devices[source.DeviceId].HostMappings["openjibo.mergedIntoDeviceId"] = targetDeviceId;
        TouchState();
        return new RobotMergeResult(source.DeviceId, targetDeviceId, migratedSessions, migratedBindings, DateTimeOffset.UtcNow);
    }

    public RobotMergeResult MergeRobotRecordsForAdministration(string sourceDeviceId, string targetDeviceId) =>
        MergeRobotRecords(sourceDeviceId, targetDeviceId);

    public RobotMergeResult MergeRobotRecordsForAdministration(string sourceDeviceId, string targetDeviceId,
        RobotMergePrecondition precondition)
    {
        ArgumentNullException.ThrowIfNull(precondition);
        ArgumentNullException.ThrowIfNull(precondition.SessionIds);
        ArgumentNullException.ThrowIfNull(precondition.CredentialFingerprints);
        if (string.IsNullOrWhiteSpace(sourceDeviceId) || string.IsNullOrWhiteSpace(targetDeviceId) ||
            sourceDeviceId.Equals(targetDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose two different robot records.");
        if (sourceDeviceId.Equals(_robot.DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The active robot record must be the canonical target, not the merge source.");

        lock (_syncRoot)
        {
            return _sessions.ExecuteExclusive(sessions =>
            {
                var source = _devices.GetValueOrDefault(sourceDeviceId.Trim()) ??
                             throw new KeyNotFoundException("Source robot record was not found.");
                var target = _devices.GetValueOrDefault(targetDeviceId.Trim()) ??
                             throw new KeyNotFoundException("Target robot record was not found.");
                if (source.IsHidden || source.ArchivedUtc is not null || target.IsHidden || target.ArchivedUtc is not null)
                    throw new InvalidOperationException("Robot merge requires two visible, unarchived records.");

                var sourceUsers = _userDeviceLinks
                    .Where(item => item.DeviceId.Equals(source.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.UserId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var targetUsers = _userDeviceLinks
                    .Where(item => item.DeviceId.Equals(target.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.UserId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!sourceUsers.SetEquals(targetUsers))
                    throw new InvalidOperationException("Admin merge requires identical account associations.");

                var sourceSessions = sessions.Where(item =>
                        source.DeviceId.Equals(item.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (!NormalizeValues(sourceSessions.Select(item => item.SessionId)).SequenceEqual(
                        NormalizeValues(precondition.SessionIds), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Robot merge state changed after preview. Review the merge again.");

                var sourceCredentials = _robotCredentialBindings.Values
                    .Where(item => source.DeviceId.Equals(item.DeviceId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.AccessKeyFingerprint);
                if (!NormalizeValues(sourceCredentials).SequenceEqual(
                        NormalizeValues(precondition.CredentialFingerprints), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Robot merge state changed after preview. Review the merge again.");

                var now = DateTimeOffset.UtcNow;
                foreach (var session in sourceSessions)
                {
                    if (!string.IsNullOrWhiteSpace(session.Token) &&
                        !session.Token.StartsWith("conn:", StringComparison.OrdinalIgnoreCase))
                        session.DeviceId = target.DeviceId;
                    session.Metadata["registeredDeviceId"] = target.DeviceId;
                    session.Metadata["registeredRobotId"] = target.DeviceId;
                }

                var migratedBindings = 0;
                foreach (var binding in _robotCredentialBindings.Values.Where(item =>
                             source.DeviceId.Equals(item.DeviceId, StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    _robotCredentialBindings[binding.AccessKeyFingerprint] = binding with { DeviceId = target.DeviceId };
                    migratedBindings++;
                }

                var mappings = new Dictionary<string, string>(source.HostMappings, StringComparer.OrdinalIgnoreCase)
                {
                    ["openjibo.mergedIntoDeviceId"] = target.DeviceId
                };
                _devices[source.DeviceId] = new DeviceRegistration
                {
                    DeviceId = source.DeviceId,
                    RobotId = source.RobotId,
                    FriendlyName = source.FriendlyName,
                    FirmwareVersion = source.FirmwareVersion,
                    ApplicationVersion = source.ApplicationVersion,
                    IsActive = false,
                    CertificateThumbprint = source.CertificateThumbprint,
                    IssuedIdentityId = source.IssuedIdentityId,
                    BuildHash = source.BuildHash,
                    ConfigHash = source.ConfigHash,
                    VerifiedSerialNumber = source.VerifiedSerialNumber,
                    SerialEvidenceSource = source.SerialEvidenceSource,
                    SerialEvidenceVerifiedUtc = source.SerialEvidenceVerifiedUtc,
                    RegistrationSource = source.RegistrationSource,
                    IsHidden = true,
                    ArchivedUtc = now,
                    HostMappings = mappings
                };
                TouchState();
                return new RobotMergeResult(source.DeviceId, target.DeviceId, sourceSessions.Length,
                    migratedBindings, now);
            });
        }
    }

    private static string[] NormalizeValues(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public RobotIdentityCleanupPreview PreviewRobotIdentityCleanup()
    {
        lock (_syncRoot)
        {
            var relationships = _devices.Values
                .Where(device => device.HostMappings.TryGetValue("openjibo.mergedIntoDeviceId", out var target) &&
                                 !string.IsNullOrWhiteSpace(target))
                .Select(device => new RobotMergeRelationship(
                    device.DeviceId,
                    device.HostMappings["openjibo.mergedIntoDeviceId"]))
                .OrderBy(item => item.SourceDeviceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var sessionBindings = _sessions.Values.Count(session =>
                !string.IsNullOrWhiteSpace(ReadSessionMetadata(session, "registeredDeviceId")));
            var authenticationSessions = _sessions.Keys.Count(IsIssuedAuthenticationToken);
            return new RobotIdentityCleanupPreview(
                relationships.Length,
                sessionBindings,
                authenticationSessions,
                _robotCredentialBindings.Count,
                relationships);
        }
    }

    public RobotIdentityCleanupResult ResetRobotIdentityAssociations()
    {
        lock (_syncRoot)
        {
            var restored = 0;
            foreach (var source in _devices.Values.Where(device =>
                         device.HostMappings.ContainsKey("openjibo.mergedIntoDeviceId")).ToArray())
            {
                var mappings = new Dictionary<string, string>(source.HostMappings, StringComparer.OrdinalIgnoreCase);
                mappings.Remove("openjibo.mergedIntoDeviceId");
                _devices[source.DeviceId] = new DeviceRegistration
                {
                    DeviceId = source.DeviceId,
                    RobotId = source.RobotId,
                    FriendlyName = source.FriendlyName,
                    FirmwareVersion = source.FirmwareVersion,
                    ApplicationVersion = source.ApplicationVersion,
                    IsActive = true,
                    CertificateThumbprint = source.CertificateThumbprint,
                    IssuedIdentityId = source.IssuedIdentityId,
                    BuildHash = source.BuildHash,
                    ConfigHash = source.ConfigHash,
                    VerifiedSerialNumber = source.VerifiedSerialNumber,
                    SerialEvidenceSource = source.SerialEvidenceSource,
                    SerialEvidenceVerifiedUtc = source.SerialEvidenceVerifiedUtc,
                    RegistrationSource = source.RegistrationSource,
                    IsHidden = false,
                    ArchivedUtc = null,
                    HostMappings = mappings
                };
                restored++;
            }

            var cleared = 0;
            foreach (var session in _sessions.Values)
            {
                var registeredDeviceId = ReadSessionMetadata(session, "registeredDeviceId");
                if (!string.IsNullOrWhiteSpace(registeredDeviceId))
                {
                    AppendSessionBindingAudit(session, "identity-cleanup-reset",
                        registeredDeviceId, null, "portal-admin");
                    session.Metadata.Remove("registeredDeviceId");
                    session.Metadata.Remove("registeredRobotId");
                    cleared++;
                }
                session.Metadata.Remove("identitySuggestionDeviceId");
            }

            var revoked = 0;
            foreach (var token in _sessions.Keys.Where(IsIssuedAuthenticationToken).ToArray())
            {
                if (_sessions.TryRemove(token, out _)) revoked++;
            }

            if (restored > 0 || cleared > 0 || revoked > 0) TouchState();
            return new RobotIdentityCleanupResult(restored, cleared, revoked, _robotCredentialBindings.Count,
                DateTimeOffset.UtcNow);
        }
    }

    private static bool IsIssuedAuthenticationToken(string token) =>
        token.StartsWith("token-", StringComparison.OrdinalIgnoreCase) ||
        token.StartsWith("hub-", StringComparison.OrdinalIgnoreCase);

    private static bool IdentityMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        if (left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return GetIdentityAliases(left).Any(alias => alias.Equals(right.Trim(), StringComparison.OrdinalIgnoreCase)) ||
               GetIdentityAliases(right).Any(alias => alias.Equals(left.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetIdentityAliases(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var trimmed = value.Trim();
        yield return trimmed;

        if (trimmed.StartsWith("robot-", StringComparison.OrdinalIgnoreCase) && trimmed.Length > "robot-".Length)
            yield return trimmed["robot-".Length..];

        if (trimmed.StartsWith("hub-", StringComparison.OrdinalIgnoreCase) && trimmed.Length > "hub-".Length)
            yield return trimmed["hub-".Length..];
    }

    public IReadOnlyList<TrustedServerRecord> GetTrustedServers()
    {
        lock (_syncRoot)
        {
            return _trustedServers
                .OrderBy(server => server.IsTrustRoot ? 0 : 1)
                .ThenBy(server => server.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(server => server.CanonicalHost, StringComparer.OrdinalIgnoreCase)
                .Select(CloneTrustedServer)
                .ToArray();
        }
    }

    public IReadOnlyList<TrustedServerAdmissionRecord> GetTrustedServerAdmissions(string? canonicalHost = null)
    {
        lock (_syncRoot)
        {
            var records = _trustedServerAdmissions.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(canonicalHost))
            {
                var normalizedHost = NormalizeTrustedServerHost(canonicalHost);
                records = records.Where(record =>
                    record.CanonicalHost.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase));
            }

            return records
                .OrderByDescending(record => record.CreatedUtc)
                .Select(CloneTrustedServerAdmission)
                .ToArray();
        }
    }

    public TrustedServerRecord? FindTrustedServer(string canonicalHost)
    {
        if (string.IsNullOrWhiteSpace(canonicalHost)) return null;

        var normalizedHost = NormalizeTrustedServerHost(canonicalHost);
        lock (_syncRoot)
        {
            var trustedServer = _trustedServers.FirstOrDefault(server =>
                server.CanonicalHost.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase));
            return trustedServer is null ? null : CloneTrustedServer(trustedServer);
        }
    }

    public TrustedServerRecord UpsertTrustedServer(TrustedServerRecord trustedServer)
    {
        ArgumentNullException.ThrowIfNull(trustedServer);

        if (string.IsNullOrWhiteSpace(trustedServer.CanonicalHost))
            throw new ArgumentException("Canonical host is required.", nameof(trustedServer));

        lock (_syncRoot)
        {
            var normalizedHost = NormalizeTrustedServerHost(trustedServer.CanonicalHost);
            var now = DateTimeOffset.UtcNow;
            var existingIndex = _trustedServers.FindIndex(server =>
                server.CanonicalHost.Equals(normalizedHost, StringComparison.OrdinalIgnoreCase));
            var current = existingIndex >= 0 ? _trustedServers[existingIndex] : null;
            var merged = new TrustedServerRecord
            {
                ServerId = current?.ServerId ?? trustedServer.ServerId,
                CanonicalHost = normalizedHost,
                DisplayName = string.IsNullOrWhiteSpace(trustedServer.DisplayName)
                    ? normalizedHost
                    : trustedServer.DisplayName.Trim(),
                ServerKind = NormalizeServerKind(trustedServer.ServerKind, current?.ServerKind),
                IsListed = trustedServer.IsListed,
                AcceptsPublicConnections = trustedServer.AcceptsPublicConnections,
                ParticipatesInCloudSync = trustedServer.ParticipatesInCloudSync,
                RequiresHttps = trustedServer.RequiresHttps,
                IsTrustRoot = current?.IsTrustRoot == true || trustedServer.IsTrustRoot,
                IsActive = trustedServer.IsActive,
                Description = trustedServer.Description?.Trim() ?? string.Empty,
                RegisteredAtUtc = current?.RegisteredAtUtc ?? now,
                UpdatedAtUtc = now,
                LastSeenAtUtc = trustedServer.LastSeenAtUtc ?? current?.LastSeenAtUtc
            };

            if (existingIndex >= 0)
                _trustedServers[existingIndex] = merged;
            else
                _trustedServers.Add(merged);
            TouchState();
            return CloneTrustedServer(merged);
        }
    }

    public TrustedServerAdmissionRecord RecordTrustedServerAdmission(TrustedServerRecord trustedServer, string action,
        string? actorDeviceId, string? actorFriendlyId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(trustedServer);

        var normalizedAction = NormalizeTrustedServerAction(action);
        var now = DateTimeOffset.UtcNow;
        var payload = BuildTrustedServerAdmissionPayload(trustedServer, normalizedAction, actorDeviceId,
            actorFriendlyId, reason, now);
        var record = new TrustedServerAdmissionRecord
        {
            ServerId = trustedServer.ServerId,
            CanonicalHost = trustedServer.CanonicalHost,
            ServerKind = trustedServer.ServerKind,
            Action = normalizedAction,
            ActorDeviceId = actorDeviceId?.Trim() ?? string.Empty,
            ActorFriendlyId = actorFriendlyId?.Trim() ?? string.Empty,
            Reason = reason?.Trim(),
            Payload = payload,
            Signature = SignTrustedServerAdmissionPayload(payload),
            CreatedUtc = now
        };

        lock (_syncRoot)
        {
            _trustedServerAdmissions.Add(record);
            TouchState();
        }

        return CloneTrustedServerAdmission(record);
    }

    public UserRecord? CreateUser(string email, string password, string? firstName, string? lastName)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return null;

        lock (_syncRoot)
        {
            if (_users.Any(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                return null;

            var salt = GenerateSalt();
            var user = new UserRecord
            {
                Email = email.Trim().ToLowerInvariant(),
                PasswordHash = HashPassword(password, salt),
                Salt = salt,
                FirstName = firstName?.Trim() ?? string.Empty,
                LastName = lastName?.Trim() ?? string.Empty
            };
            _users.Add(user);
        }

        TouchState();
        return _users.Last(u => u.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
    }

    public UserRecord? AuthenticateUser(string email, string password)
    {
        var user = _users.FirstOrDefault(u => u.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
        if (user is null) return null;

        var hash = HashPassword(password, user.Salt);
        return hash == user.PasswordHash ? user : null;
    }

    public UserRecord? GetUserById(string id)
    {
        return _users.FirstOrDefault(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public UserRecord? GetUserByEmail(string email)
    {
        return _users.FirstOrDefault(u => u.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public UserRecord UpdateUser(string id, string? firstName, string? lastName, string? gender, long? birthday)
    {
        lock (_syncRoot)
        {
            var index = _users.FindIndex(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException($"User '{id}' not found.");

            var existing = _users[index];
            _users[index] = new UserRecord
            {
                Id = existing.Id,
                Email = existing.Email,
                PasswordHash = existing.PasswordHash,
                Salt = existing.Salt,
                FirstName = firstName?.Trim() ?? existing.FirstName,
                LastName = lastName?.Trim() ?? existing.LastName,
                Gender = gender ?? existing.Gender,
                Birthday = birthday ?? existing.Birthday,
                AccessKeyId = existing.AccessKeyId,
                SecretAccessKey = existing.SecretAccessKey,
                IsActive = existing.IsActive,
                CreatedUtc = existing.CreatedUtc
            };
        }

        TouchState();
        return _users.First(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public UserDeviceLink LinkUserToDevice(string userId, string deviceId, string claimSource)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("UserId is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("DeviceId is required.", nameof(deviceId));

        lock (_syncRoot)
        {
            var user = _users.FirstOrDefault(item =>
                item.Id.Equals(userId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
                throw new KeyNotFoundException("User record was not found.");

            var device = _devices.Values.FirstOrDefault(item =>
                item.DeviceId.Equals(deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (device is null)
                throw new KeyNotFoundException("Robot record was not found.");

            _userDeviceLinks.RemoveAll(item =>
                item.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase));
            var link = new UserDeviceLink(user.Id, device.DeviceId,
                string.IsNullOrWhiteSpace(claimSource) ? "portal-pairing" : claimSource.Trim(),
                DateTimeOffset.UtcNow);
            _userDeviceLinks.Add(link);
            TouchState();
            return link;
        }
    }

    public IReadOnlyList<DeviceRegistration> GetDevicesForUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return [];

        lock (_syncRoot)
        {
            var deviceIds = _userDeviceLinks
                .Where(item => item.UserId.Equals(userId.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(item => item.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _devices.Values
                .Where(item => deviceIds.Contains(item.DeviceId))
                .Select(device => CloneDeviceRegistration(device))
                .OrderBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public string? GetUserIdForDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        lock (_syncRoot)
            return _userDeviceLinks.FirstOrDefault(item =>
                item.DeviceId.Equals(deviceId.Trim(), StringComparison.OrdinalIgnoreCase))?.UserId;
    }

    public string IssueHubToken(string? deviceId = null, bool useDefaultRobot = true,
        HubTokenCredentialBinding? credentialBinding = null)
    {
        var resolvedDeviceId = !string.IsNullOrWhiteSpace(deviceId)
            ? deviceId.Trim()
            : useDefaultRobot
                ? _robot.DeviceId
                : null;
        var token = $"hub-{_account.AccountId}-{Guid.NewGuid():N}";
        var metadata = BuildSessionMetadata(_account.AccountId, resolvedDeviceId, ResolveDefaultLoopId());
        credentialBinding?.WriteTo(metadata);
        RegisterIssuedSession(token, new CloudSession
        {
            Kind = "hub",
            AccountId = _account.AccountId,
            Token = token,
            DeviceId = resolvedDeviceId,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365),
            Metadata = metadata
        });

        TouchState();
        return token;
    }

    public string IssueRobotToken(string deviceId)
    {
        var token = $"token-{deviceId}-{Guid.NewGuid():N}";
        RegisterIssuedSession(token, new CloudSession
        {
            Kind = "robot",
            AccountId = _account.AccountId,
            Token = token,
            DeviceId = deviceId,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(365),
            Metadata = BuildSessionMetadata(_account.AccountId, deviceId, ResolveDefaultLoopId())
        });

        TouchState();
        return token;
    }

    public string IssueDeploymentSmokeRobotToken(string deviceId)
    {
        ValidateDeploymentSmokeDeviceId(deviceId);
        var normalizedDeviceId = deviceId.Trim();
        if (!_devices.TryGetValue(normalizedDeviceId, out var device) ||
            !string.Equals(RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId),
                RobotRegistrationSources.DeploymentSmoke, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deployment-smoke token device is not classified as deployment-smoke.");
        _sessions.RemoveDurableForDevice(normalizedDeviceId, "robot");
        var token = $"token-{normalizedDeviceId}-{Guid.NewGuid():N}";
        RegisterIssuedSession(token, new CloudSession
        {
            Kind = "robot",
            AccountId = _account.AccountId,
            Token = token,
            DeviceId = normalizedDeviceId,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15),
            Metadata = BuildSessionMetadata(_account.AccountId, normalizedDeviceId, ResolveDefaultLoopId())
        });
        TouchState();
        return token;
    }
    public string IssueDeploymentSmokeHubToken(string deviceId)
    {
        ValidateDeploymentSmokeDeviceId(deviceId);
        var normalizedDeviceId = deviceId.Trim();
        if (!_devices.TryGetValue(normalizedDeviceId, out var device) ||
            !string.Equals(RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId),
                RobotRegistrationSources.DeploymentSmoke, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deployment-smoke token device is not classified as deployment-smoke.");
        _sessions.RemoveDurableForDevice(normalizedDeviceId, "hub");
        var token = $"hub-{_account.AccountId}-{Guid.NewGuid():N}";
        RegisterIssuedSession(token, new CloudSession
        {
            Kind = "hub",
            AccountId = _account.AccountId,
            Token = token,
            DeviceId = normalizedDeviceId,
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15),
            Metadata = BuildSessionMetadata(_account.AccountId, normalizedDeviceId, ResolveDefaultLoopId())
        });
        TouchState();
        return token;
    }

    private static void ValidateDeploymentSmokeDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) ||
            !deviceId.Trim().StartsWith("open-jibo-smoke-staging-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deployment-smoke tokens require the fixed staging smoke namespace.");
    }

    private void RegisterIssuedSession(string token, CloudSession session)
    {
        // Token issuance is itself visible in the status portal, even when the
        // client never reaches the WebSocket handshake. Serialize registration
        // with link/unlink so the issued row cannot miss a concurrent binding.
        lock (_syncRoot)
        {
            _sessions.RegisterDurableToken(token, session);
            InheritDialogMetadataFromDevice(session);
            if (!session.Metadata.ContainsKey("registeredDeviceId") &&
                !string.IsNullOrWhiteSpace(session.DeviceId) &&
                _devices.TryGetValue(session.DeviceId, out var registered) &&
                !registered.IsHidden && registered.ArchivedUtc is null)
            {
                session.Metadata["registeredDeviceId"] = registered.DeviceId;
                session.Metadata["registeredRobotId"] = registered.RobotId;
            }
        }
    }

    public CloudSession OpenSession(string kind, string? deviceId, string? token, string? hostName, string? path)
    {
        var durableToken = string.IsNullOrWhiteSpace(token) ? null : _sessions.FindDurable(token);
        // Path-token / per-connection listen sockets must not inherit the process-wide singleton
        // DeviceId — that collapses every robot onto one identity until CONTEXT arrives.
        var resolvedDeviceId = !string.IsNullOrWhiteSpace(deviceId)
            ? deviceId.Trim()
            : !string.IsNullOrWhiteSpace(durableToken?.DeviceId)
                ? durableToken.DeviceId
            : IsAmbiguousConnectionToken(token)
                ? null
                : _robot.DeviceId;
        var resolvedAccountId = durableToken?.AccountId ?? _account.AccountId;
        var resolvedLoopId = ResolveDefaultLoopId();
        var session = new CloudSession
        {
            Kind = kind,
            AccountId = resolvedAccountId,
            DeviceId = resolvedDeviceId,
            Token = token,
            HostName = hostName,
            Path = path,
            Metadata = BuildSessionMetadata(resolvedAccountId, resolvedDeviceId, resolvedLoopId)
        };

        if (durableToken is not null)
            foreach (var pair in durableToken.Metadata)
                session.Metadata[pair.Key] = pair.Value;

        if (!string.IsNullOrWhiteSpace(token))
            _sessions.RegisterActive(token, session);

        InheritDialogMetadataFromDevice(session);
        if (durableToken is not null &&
            !session.Metadata.ContainsKey("registeredDeviceId") &&
            !string.IsNullOrWhiteSpace(resolvedDeviceId))
        {
            var registered = FindDeviceByFriendlyId(resolvedDeviceId);
            if (registered is not null && !registered.IsHidden && registered.ArchivedUtc is null)
            {
                session.Metadata["registeredDeviceId"] = registered.DeviceId;
                session.Metadata["registeredRobotId"] = registered.RobotId;
            }
        }
        TouchState();

        return session;
    }

    public void ReinheritDialogMetadata(CloudSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        InheritDialogMetadataFromDevice(session);
        TouchState();
    }

    private static bool IsAmbiguousConnectionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        var trimmed = token.Trim();
        return trimmed.StartsWith("conn:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("v1/listen", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("listen", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("v1/proactive", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("proactive", StringComparison.OrdinalIgnoreCase);
    }

    private void InheritDialogMetadataFromDevice(CloudSession session)
    {
        if (string.IsNullOrWhiteSpace(session.DeviceId)) return;

        var donor = _sessions.Values
            .Where(candidate =>
                !string.Equals(candidate.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.DeviceId, session.DeviceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.LastSeenUtc)
            .ThenByDescending(candidate => candidate.CreatedUtc)
            .FirstOrDefault();
        if (donor is null)
        {
            var observed = _devices.GetValueOrDefault(session.DeviceId.Trim());
            if (observed?.HostMappings.TryGetValue("openjibo.boundRegisteredDeviceId", out var boundId) == true &&
                !string.IsNullOrWhiteSpace(boundId))
            {
                var boundDevice = FindDeviceByFriendlyId(boundId);
                if (boundDevice is not null && !boundDevice.IsHidden && boundDevice.ArchivedUtc is null)
                {
                    session.Metadata["registeredDeviceId"] = boundDevice.DeviceId;
                    session.Metadata["registeredRobotId"] = boundDevice.RobotId;
                    return;
                }
            }
            if (observed?.HostMappings.TryGetValue("openjibo.mergedIntoDeviceId", out var mergedInto) == true &&
                !string.IsNullOrWhiteSpace(mergedInto))
            {
                var target = FindDeviceByFriendlyId(mergedInto);
                if (target is not null && !target.IsHidden && target.ArchivedUtc is null)
                {
                    session.Metadata["registeredDeviceId"] = target.DeviceId;
                    session.Metadata["registeredRobotId"] = target.RobotId;
                }
            }
            return;
        }

        foreach (var pair in donor.Metadata)
        {
            if (pair.Value is null || !ShouldInheritDialogMetadataKey(pair.Key)) continue;
            if (string.Equals(pair.Key, "registeredDeviceId", StringComparison.OrdinalIgnoreCase))
            {
                var boundDevice = FindDeviceByFriendlyId(pair.Value.ToString() ?? string.Empty);
                // Archived/hidden inventory records are historical only. Never
                // resurrect one as the identity of a reconnecting session.
                if (boundDevice is null || boundDevice.IsHidden || boundDevice.ArchivedUtc is not null)
                    continue;
            }
            if (string.Equals(pair.Key, "registeredRobotId", StringComparison.OrdinalIgnoreCase) &&
                !session.Metadata.ContainsKey("registeredDeviceId"))
                continue;
            if (session.Metadata.ContainsKey(pair.Key)) continue;
            session.Metadata[pair.Key] = pair.Value;
        }
    }

    private static bool ShouldInheritDialogMetadataKey(string key)
    {
        // An administrator's explicit session-to-inventory binding must survive a
        // reconnect.  The runtime DeviceId remains the observed hardware identity;
        // carrying only these two fields reuses the verified mapping without
        // auto-claiming a new or cloned robot.
        if (string.Equals(key, "registeredDeviceId", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "registeredRobotId", StringComparison.OrdinalIgnoreCase))
            return true;

        return key.StartsWith("personalReport", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("householdList", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("chitchat", StringComparison.OrdinalIgnoreCase) ||
               key.StartsWith("greetings", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "pendingProactivityOffer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "lastClockDomain", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "sleepState", StringComparison.OrdinalIgnoreCase);
    }

    public CloudSession? FindIssuedToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var issued = _sessions.FindDurable(token.Trim());
        return issued is not null && CredentialBindingIsCurrent(issued.Metadata, _account)
            ? issued
            : null;
    }

    private static bool CredentialBindingIsCurrent(
        IEnumerable<KeyValuePair<string, object?>> metadata,
        AccountProfile account)
    {
        if (!HubTokenCredentialBinding.ContainsMetadata(metadata))
            return true;
        return HubTokenCredentialBinding.TryRead(metadata, out var binding) &&
               binding is not null &&
               string.Equals(
                   binding.CredentialFingerprint,
                   AwsSigV4RequestVerifier.CreateAccessKeyFingerprint(account.AccessKeyId),
                   StringComparison.Ordinal);
    }

    public CloudSession? FindSessionByToken(string token)
    {
        return _sessions.Find(token);
    }

    public CloudSession? FindActiveSessionByToken(string token) =>
        string.IsNullOrWhiteSpace(token) ? null : _sessions.FindActive(token);

    public void CloseSession(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessions.RemoveBySessionId(sessionId);
    }

    public bool BindSessionToDevice(string sessionId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(deviceId)) return false;

        lock (_syncRoot)
        {
            var device = FindDeviceByFriendlyId(deviceId);
            var session = _sessions.Values.FirstOrDefault(candidate =>
                candidate.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
            // Archived records are historical only. They can never be selected as a live
            // session identity, even by an explicit UI request.
            if (device is null || device.IsHidden || session is null) return false;

            var previousDeviceId = ReadSessionMetadata(session, "registeredDeviceId");
            AppendSessionBindingAudit(session, string.IsNullOrWhiteSpace(previousDeviceId) ? "linked" : "relinked",
                previousDeviceId, device.DeviceId, "portal-admin");
            // Keep the runtime loop identifier as DeviceId, and persist the explicitly selected
            // inventory identity separately so both hardware identifiers remain traceable.
            session.Metadata["registeredDeviceId"] = device.DeviceId;
            session.Metadata["registeredRobotId"] = device.RobotId;
            if (!string.IsNullOrWhiteSpace(session.DeviceId) &&
                _devices.TryGetValue(session.DeviceId, out var observed))
            {
                observed.HostMappings["openjibo.boundRegisteredDeviceId"] = device.DeviceId;
                observed.HostMappings["openjibo.boundRegisteredRobotId"] = device.RobotId;
            }
        }

        TouchState();
        return true;
    }

    public bool BindObservedIdentityToDevice(string observedDeviceId, string deviceId)
    {
        if (string.IsNullOrWhiteSpace(observedDeviceId) || string.IsNullOrWhiteSpace(deviceId)) return false;

        lock (_syncRoot)
        {
            var device = FindDeviceByFriendlyId(deviceId);
            var observed = _devices.GetValueOrDefault(observedDeviceId.Trim());
            if (device is null || device.IsHidden || device.ArchivedUtc is not null || observed is null) return false;

            observed.HostMappings["openjibo.boundRegisteredDeviceId"] = device.DeviceId;
            observed.HostMappings["openjibo.boundRegisteredRobotId"] = device.RobotId;
            foreach (var session in _sessions.Values.Where(candidate =>
                         string.Equals(candidate.DeviceId, observed.DeviceId, StringComparison.OrdinalIgnoreCase)))
            {
                session.Metadata["registeredDeviceId"] = device.DeviceId;
                session.Metadata["registeredRobotId"] = device.RobotId;
            }
        }

        TouchState();
        return true;
    }

    public bool ClearSessionDeviceBinding(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;

        lock (_syncRoot)
        {
            var session = _sessions.Values.FirstOrDefault(candidate =>
                candidate.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
            if (session is null) return false;

            // A session row is only one connection for an observed runtime identity.
            // Clear every historical donor for that same runtime ID so reconnect
            // inheritance cannot immediately recreate the link the admin removed.
            IEnumerable<CloudSession> relatedSessions = string.IsNullOrWhiteSpace(session.DeviceId)
                ? new[] { session }
                : _sessions.Values.Where(candidate =>
                    string.Equals(candidate.DeviceId, session.DeviceId, StringComparison.OrdinalIgnoreCase));
            foreach (var related in relatedSessions)
            {
                var previous = ReadSessionMetadata(related, "registeredDeviceId");
                if (!string.IsNullOrWhiteSpace(previous))
                    AppendSessionBindingAudit(related, "unlinked", previous, null, "portal-admin");
                related.Metadata.Remove("registeredDeviceId");
                related.Metadata.Remove("registeredRobotId");
                related.Metadata.Remove("identitySuggestionDeviceId");
            }

            if (!string.IsNullOrWhiteSpace(session.DeviceId) &&
                _devices.TryGetValue(session.DeviceId, out var boundObserved))
            {
                boundObserved.HostMappings.Remove("openjibo.boundRegisteredDeviceId");
                boundObserved.HostMappings.Remove("openjibo.boundRegisteredRobotId");
            }

            // Breaking a link on an archived merge source is an explicit unmerge of
            // that observed identity. Restore the source record and remove the marker
            // that would otherwise bind its next connection again.
            if (!string.IsNullOrWhiteSpace(session.DeviceId) &&
                _devices.TryGetValue(session.DeviceId, out var observed) &&
                observed.HostMappings.ContainsKey("openjibo.mergedIntoDeviceId"))
            {
                var mappings = new Dictionary<string, string>(observed.HostMappings, StringComparer.OrdinalIgnoreCase);
                mappings.Remove("openjibo.mergedIntoDeviceId");
                mappings.Remove("openjibo.boundRegisteredDeviceId");
                mappings.Remove("openjibo.boundRegisteredRobotId");
                _devices[observed.DeviceId] = new DeviceRegistration
                {
                    DeviceId = observed.DeviceId,
                    RobotId = observed.RobotId,
                    FriendlyName = observed.FriendlyName,
                    FirmwareVersion = observed.FirmwareVersion,
                    ApplicationVersion = observed.ApplicationVersion,
                    IsActive = true,
                    CertificateThumbprint = observed.CertificateThumbprint,
                    IssuedIdentityId = observed.IssuedIdentityId,
                    BuildHash = observed.BuildHash,
                    ConfigHash = observed.ConfigHash,
                    VerifiedSerialNumber = observed.VerifiedSerialNumber,
                    SerialEvidenceSource = observed.SerialEvidenceSource,
                    SerialEvidenceVerifiedUtc = observed.SerialEvidenceVerifiedUtc,
                    RegistrationSource = observed.RegistrationSource,
                    IsHidden = false,
                    ArchivedUtc = null,
                    HostMappings = mappings
                };
            }
        }

        TouchState();
        return true;
    }

    public IReadOnlyList<LoopRecord> GetLoops()
    {
        return _loops.ToArray();
    }


    public LoopRecord AddLoop(string? name, string? ownerAccountId, string? robotId, string? robotFriendlyId)
    {
        var now = DateTimeOffset.UtcNow;
        var resolvedOwnerAccountId = string.IsNullOrWhiteSpace(ownerAccountId) ? _account.AccountId : ownerAccountId.Trim();
        var resolvedRobotId = robotId?.Trim() ?? string.Empty;
        var resolvedRobotFriendlyId = robotFriendlyId?.Trim() ?? string.Empty;
        var baseName = string.IsNullOrWhiteSpace(name)
            ? string.IsNullOrWhiteSpace(resolvedRobotFriendlyId)
                ? "OpenJibo Loop"
                : $"{resolvedRobotFriendlyId} Loop"
            : name.Trim();
        var baseLoopId = $"loop-{Slugify(string.IsNullOrWhiteSpace(resolvedRobotFriendlyId) ? baseName : resolvedRobotFriendlyId)}";
        if (string.IsNullOrWhiteSpace(baseLoopId) || string.Equals(baseLoopId, "loop", StringComparison.OrdinalIgnoreCase))
            baseLoopId = $"loop-{Guid.NewGuid():N}";

        lock (_syncRoot)
        {
            var existing = FindLoopForRobotLocked(resolvedRobotId, resolvedRobotFriendlyId);
            if (existing is not null)
            {
                if ((string.IsNullOrWhiteSpace(existing.RobotId) && !string.IsNullOrWhiteSpace(resolvedRobotId)) ||
                    (string.IsNullOrWhiteSpace(existing.RobotFriendlyId) && !string.IsNullOrWhiteSpace(resolvedRobotFriendlyId)))
                {
                    var index = _loops.FindIndex(loop =>
                        loop.LoopId.Equals(existing.LoopId, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        existing = new LoopRecord
                        {
                            LoopId = existing.LoopId,
                            Name = existing.Name,
                            OwnerAccountId = existing.OwnerAccountId,
                            RobotId = string.IsNullOrWhiteSpace(existing.RobotId) ? resolvedRobotId : existing.RobotId,
                            RobotFriendlyId = string.IsNullOrWhiteSpace(existing.RobotFriendlyId)
                                ? resolvedRobotFriendlyId
                                : existing.RobotFriendlyId,
                            IsSuspended = existing.IsSuspended,
                            CreatedUtc = existing.CreatedUtc,
                            UpdatedUtc = DateTimeOffset.UtcNow
                        };
                        _loops[index] = existing;
                        TouchState();
                    }
                }

                return existing;
            }

            var candidateLoopId = baseLoopId;
            var suffix = 2;
            while (_loops.Any(loop => string.Equals(loop.LoopId, candidateLoopId, StringComparison.OrdinalIgnoreCase)))
                candidateLoopId = $"{baseLoopId}-{suffix++}";

            var loop = new LoopRecord
            {
                LoopId = candidateLoopId,
                Name = baseName,
                OwnerAccountId = resolvedOwnerAccountId,
                RobotId = resolvedRobotId,
                RobotFriendlyId = resolvedRobotFriendlyId,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            _loops.Add(loop);
            EnsureOwnerLoopMember(loop.LoopId);
            EnsureRobotLoopMember(loop.LoopId, resolvedRobotId);
            TouchState();
            return loop;
        }
    }

    private LoopRecord? FindLoopForRobotLocked(string robotId, string robotFriendlyId)
    {
        return _loops.FirstOrDefault(loop => LoopMatchesRobot(loop, robotId, robotFriendlyId));
    }

    private static bool LoopMatchesRobot(LoopRecord loop, string? robotId, string? robotFriendlyId)
    {
        // One loop per friendlyId (Pegasus robotID / BE robotFriendlyId). Prefer robotId as the
        // canonical key; only fall back to robotFriendlyId when robotId is empty. Never OR-match
        // a shared serial/device string across robots.
        var friendlyKey = !string.IsNullOrWhiteSpace(robotId)
            ? robotId.Trim()
            : robotFriendlyId?.Trim();
        if (string.IsNullOrWhiteSpace(friendlyKey)) return false;

        return string.Equals(loop.RobotId, friendlyKey, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(loop.RobotFriendlyId, friendlyKey, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PersonRecord> GetPeople(string? loopId = null)
    {
        return _people.Where(person => string.IsNullOrWhiteSpace(loopId) ||
                                      person.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public PersonRecord UpsertPerson(PersonRecord person)
    {
        if (person is null) throw new ArgumentNullException(nameof(person));
        if (string.IsNullOrWhiteSpace(person.PersonId))
            throw new ArgumentException("PersonId is required.", nameof(person));

        PersonRecord resolved;
        lock (_syncRoot)
        {
            var index = _people.FindIndex(existing =>
                existing.PersonId.Equals(person.PersonId, StringComparison.OrdinalIgnoreCase));
            var now = DateTimeOffset.UtcNow;
            resolved = new PersonRecord
            {
                PersonId = person.PersonId.Trim(),
                AccountId = string.IsNullOrWhiteSpace(person.AccountId) ? _account.AccountId : person.AccountId.Trim(),
                LoopId = string.IsNullOrWhiteSpace(person.LoopId) ? ResolveDefaultLoopId() : person.LoopId.Trim(),
                RobotId = string.IsNullOrWhiteSpace(person.RobotId) ? _robot.RobotId : person.RobotId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(person.DisplayName) ? person.PersonId.Trim() : person.DisplayName.Trim(),
                Alias = string.IsNullOrWhiteSpace(person.Alias) ? null : person.Alias.Trim(),
                IsPrimary = person.IsPrimary,
                CreatedUtc = index >= 0 ? _people[index].CreatedUtc : now,
                UpdatedUtc = now
            };

            if (index >= 0)
                _people[index] = resolved;
            else
                _people.Add(resolved);
        }

        TouchState();
        return resolved;
    }

    public int SyncPeopleFromLoopUsers(string loopId, string? robotId, IReadOnlyList<LoopUserSnapshot> loopUsers,
        string? ownerAccountId = null)
    {
        if (string.IsNullOrWhiteSpace(loopId) || loopUsers is null || loopUsers.Count == 0)
            return 0;

        var resolvedLoopId = loopId.Trim();
        var resolvedRobotId = string.IsNullOrWhiteSpace(robotId) ? _robot.RobotId : robotId.Trim();
        var upserted = 0;
        lock (_syncRoot)
        {
            foreach (var user in loopUsers)
            {
                if (string.IsNullOrWhiteSpace(user.Id)) continue;
                if (string.Equals(user.Type, "robot", StringComparison.OrdinalIgnoreCase)) continue;

                var personId = user.Id.Trim();
                var firstName = user.FirstName?.Trim();
                var lastName = user.LastName?.Trim();
                var nickname = user.Nickname?.Trim();
                var displayName = !string.IsNullOrWhiteSpace(nickname)
                    ? nickname
                    : string.Join(' ', new[] { firstName, lastName }.Where(static part => !string.IsNullOrWhiteSpace(part)));
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = personId;

                // Scope by loop (+ robot) so two Jibos never collide on the same PersonId.
                var existingPerson = _people.FindIndex(person =>
                    person.PersonId.Equals(personId, StringComparison.OrdinalIgnoreCase) &&
                    person.LoopId.Equals(resolvedLoopId, StringComparison.OrdinalIgnoreCase));
                if (existingPerson < 0)
                {
                    existingPerson = _people.FindIndex(person =>
                        person.PersonId.Equals(personId, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(resolvedRobotId) &&
                        person.RobotId.Equals(resolvedRobotId, StringComparison.OrdinalIgnoreCase));
                }

                var now = DateTimeOffset.UtcNow;
                var person = new PersonRecord
                {
                    PersonId = personId,
                    AccountId = string.IsNullOrWhiteSpace(user.AccountId)
                        ? (existingPerson >= 0 ? _people[existingPerson].AccountId : _account.AccountId)
                        : user.AccountId.Trim(),
                    LoopId = resolvedLoopId,
                    RobotId = resolvedRobotId,
                    DisplayName = displayName,
                    Alias = !string.IsNullOrWhiteSpace(nickname)
                        ? nickname
                        : !string.IsNullOrWhiteSpace(firstName)
                            ? firstName
                            : existingPerson >= 0
                                ? _people[existingPerson].Alias
                                : null,
                    IsPrimary = existingPerson >= 0
                        ? _people[existingPerson].IsPrimary
                        : string.Equals(user.Type, "owner", StringComparison.OrdinalIgnoreCase),
                    CreatedUtc = existingPerson >= 0 ? _people[existingPerson].CreatedUtc : now,
                    UpdatedUtc = now
                };

                if (existingPerson >= 0)
                    _people[existingPerson] = person;
                else
                    _people.Add(person);

                UpsertLoopMemberFromLoopUserLocked(resolvedLoopId, user, firstName, lastName, nickname);
                upserted++;
            }

            if (upserted > 0)
            {
                var rosterIds = new HashSet<string>(
                    loopUsers
                        .Where(static user =>
                            !string.IsNullOrWhiteSpace(user.Id) &&
                            !string.Equals(user.Type, "robot", StringComparison.OrdinalIgnoreCase))
                        .Select(static user => user.Id.Trim()),
                    StringComparer.OrdinalIgnoreCase);

                // Drop people previously attributed to this robot that are no longer in its roster.
                // This also clears cross-robot contamination from older shared-loop syncs.
                // Keep Portal-edited people until the robot has pulled the updated Loop.
                if (!string.IsNullOrWhiteSpace(resolvedRobotId))
                {
                    _people.RemoveAll(person =>
                        person.RobotId.Equals(resolvedRobotId, StringComparison.OrdinalIgnoreCase) &&
                        !rosterIds.Contains(person.PersonId) &&
                        !HasRecentPortalEditLocked(resolvedLoopId, person.PersonId));
                }

                _people.RemoveAll(person =>
                    person.PersonId.Equals("person-openjibo-household-member", StringComparison.OrdinalIgnoreCase) &&
                    person.LoopId.Equals(resolvedLoopId, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (upserted > 0)
            TouchState();

        return upserted;
    }

    private void UpsertLoopMemberFromLoopUserLocked(
        string loopId,
        LoopUserSnapshot user,
        string? firstName,
        string? lastName,
        string? nickname)
    {
        var personId = user.Id.Trim();
        var index = _loopMembers.FindIndex(member =>
            member.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
            (member.Id.Equals(personId, StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrWhiteSpace(user.AccountId) &&
              member.AccountId is not null &&
              member.AccountId.Equals(user.AccountId, StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase))));

        var memberType = string.IsNullOrWhiteSpace(user.Type)
            ? (index >= 0 ? _loopMembers[index].Type : "member")
            : user.Type.Trim();
        if (string.Equals(memberType, "robot", StringComparison.OrdinalIgnoreCase))
            return;

        var now = DateTimeOffset.UtcNow;
        var existing = index >= 0 ? _loopMembers[index] : null;
        var protectPortalEdit = existing?.PortalEditedUtc is not null;

        // When the robot still reports a stale name after a Portal edit, keep the Portal values.
        // Once the robot roster matches (or PortalEditedUtc is cleared), accept robot values again.
        string? resolvedFirstName;
        string? resolvedLastName;
        string? resolvedNickname;
        DateTimeOffset? portalEditedUtc = existing?.PortalEditedUtc;
        if (protectPortalEdit)
        {
            var robotMatchesPortal =
                NamesEqual(firstName, existing!.FirstName) &&
                NamesEqual(lastName, existing.LastName);
            if (robotMatchesPortal)
            {
                resolvedFirstName = firstName ?? existing.FirstName;
                resolvedLastName = lastName ?? existing.LastName;
                resolvedNickname = nickname ?? existing.Nickname;
                portalEditedUtc = null;
            }
            else
            {
                resolvedFirstName = existing.FirstName;
                resolvedLastName = existing.LastName;
                resolvedNickname = existing.Nickname ?? nickname;
            }
        }
        else
        {
            resolvedFirstName = firstName ?? existing?.FirstName;
            resolvedLastName = lastName ?? existing?.LastName;
            resolvedNickname = nickname ?? existing?.Nickname;
        }

        var member = new LoopMemberRecord
        {
            Id = existing?.Id ?? personId,
            LoopId = loopId,
            AccountId = string.IsNullOrWhiteSpace(user.AccountId)
                ? existing?.AccountId
                : user.AccountId.Trim(),
            Email = existing?.Email,
            FirstName = resolvedFirstName,
            LastName = resolvedLastName,
            Gender = existing?.Gender ?? "unknown",
            Birthday = existing?.Birthday,
            IsChild = existing?.IsChild ?? false,
            Status = "active",
            Type = memberType,
            Nickname = resolvedNickname,
            PhoneticName = existing?.PhoneticName,
            FaceEnrolled = existing?.FaceEnrolled ?? false,
            VoiceEnrolled = existing?.VoiceEnrolled ?? false,
            LegalGuardianId = existing?.LegalGuardianId,
            AgreementId = existing?.AgreementId,
            CreatedUtc = existing?.CreatedUtc ?? now,
            PortalEditedUtc = portalEditedUtc
        };

        // Prefer the robot's looper id as the stable member id (Pegasus personId).
        if (index >= 0 &&
            !member.Id.Equals(personId, StringComparison.OrdinalIgnoreCase) &&
            !_loopMembers.Any(existingMember =>
                existingMember.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                existingMember.Id.Equals(personId, StringComparison.OrdinalIgnoreCase)))
        {
            member = new LoopMemberRecord
            {
                Id = personId,
                LoopId = member.LoopId,
                AccountId = member.AccountId,
                Email = member.Email,
                FirstName = member.FirstName,
                LastName = member.LastName,
                Gender = member.Gender,
                Birthday = member.Birthday,
                IsChild = member.IsChild,
                Status = member.Status,
                Type = member.Type,
                Nickname = member.Nickname,
                PhoneticName = member.PhoneticName,
                FaceEnrolled = member.FaceEnrolled,
                VoiceEnrolled = member.VoiceEnrolled,
                LegalGuardianId = member.LegalGuardianId,
                AgreementId = member.AgreementId,
                CreatedUtc = member.CreatedUtc,
                PortalEditedUtc = member.PortalEditedUtc
            };
        }

        if (index >= 0)
            _loopMembers[index] = member;
        else
            _loopMembers.Add(member);
    }

    private bool HasRecentPortalEditLocked(string loopId, string personId)
    {
        return _loopMembers.Any(member =>
            member.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
            member.Id.Equals(personId, StringComparison.OrdinalIgnoreCase) &&
            member.PortalEditedUtc is not null &&
            !member.Status.Equals("removed", StringComparison.OrdinalIgnoreCase));
    }

    private static bool NamesEqual(string? left, string? right)
    {
        return string.Equals(
            left?.Trim() ?? string.Empty,
            right?.Trim() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<LoopMemberRecord> GetLoopMembers(string loopId)
    {
        return _loopMembers
            .Where(m => m.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                        !m.Status.Equals("removed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public IdentityGraphSnapshot GetIdentityGraph(string? loopId = null)
    {
        var useDefaultRobot = string.IsNullOrWhiteSpace(loopId);
        var resolvedLoopId = useDefaultRobot ? ResolveDefaultLoopId() : loopId!.Trim();
        // Preserve legacy single-robot behavior for unscoped protocol callers. Portal callers
        // always pass an explicit session loop and receive that loop's robot.
        var graphRobot = useDefaultRobot ? _robot : ResolveRobotForLoop(resolvedLoopId);
        var members = GetLoopMembers(resolvedLoopId);
        var people = _people
            .Where(person =>
                person.LoopId.Equals(resolvedLoopId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(person.RobotId) ||
                 person.RobotId.Equals(graphRobot.RobotId, StringComparison.OrdinalIgnoreCase) ||
                 person.RobotId.Equals(graphRobot.DeviceId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var relationships = new List<IdentityGraphRelationship>();

        AddIdentityRelationship(relationships, _account.AccountId, "account", "owns", resolvedLoopId, "loop",
            resolvedLoopId);
        AddIdentityRelationship(relationships, resolvedLoopId, "loop", "served-by", graphRobot.RobotId, "robot",
            resolvedLoopId);
        AddIdentityRelationship(relationships, graphRobot.RobotId, "robot", "runs-on", graphRobot.DeviceId, "device",
            resolvedLoopId);

        foreach (var person in people)
        {
            AddIdentityRelationship(relationships, person.PersonId, "person",
                person.IsPrimary ? "primary-member-of" : "member-of", resolvedLoopId, "loop", resolvedLoopId);

            if (!string.IsNullOrWhiteSpace(person.AccountId))
                AddIdentityRelationship(relationships, person.PersonId, "person", "backed-by", person.AccountId,
                    "account",
                    resolvedLoopId);

            if (!string.IsNullOrWhiteSpace(person.RobotId))
                AddIdentityRelationship(relationships, person.PersonId, "person",
                    person.IsPrimary ? "primary-user-of" : "known-by", person.RobotId, "robot", resolvedLoopId);
        }

        foreach (var member in members)
        {
            var subjectId = string.IsNullOrWhiteSpace(member.AccountId) ? member.Id : member.AccountId;
            AddIdentityRelationship(relationships, subjectId, member.Type, "member-of", resolvedLoopId, "loop",
                resolvedLoopId);
            AddLoopMemberRelationshipModel(relationships, member, members, resolvedLoopId);

            if (string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase))
            {
                var memberRobot = ResolveIdentityGraphRobotMemberDevice(member) ?? graphRobot;
                var memberRobotId = string.IsNullOrWhiteSpace(memberRobot.RobotId) ? subjectId : memberRobot.RobotId;

                AddIdentityRelationship(relationships, resolvedLoopId, "loop", "served-by", memberRobotId, "robot",
                    resolvedLoopId);
                AddIdentityRelationship(relationships, memberRobotId, "robot", "runs-on", memberRobot.DeviceId,
                    "device",
                    resolvedLoopId);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(member.AccountId))
                    AddIdentityRelationship(relationships, member.Id, "loop-member", "represented-by", member.AccountId,
                        "account", resolvedLoopId);

                if (member.FaceEnrolled)
                    AddIdentityRelationship(relationships, member.Id, "loop-member", "face-enrolled-with",
                        graphRobot.RobotId,
                        "robot", resolvedLoopId);

                if (member.VoiceEnrolled)
                    AddIdentityRelationship(relationships, member.Id, "loop-member", "voice-enrolled-with",
                        graphRobot.RobotId, "robot", resolvedLoopId);

                if (member.IsChild && !string.IsNullOrWhiteSpace(member.LegalGuardianId))
                {
                    AddIdentityRelationship(relationships, member.Id, "loop-member", "dependent-of",
                        member.LegalGuardianId, "loop-member", resolvedLoopId);
                    AddIdentityRelationship(relationships, member.LegalGuardianId, "loop-member", "guardian-of",
                        member.Id, "loop-member", resolvedLoopId);
                }
            }
        }

        var recognitionObservations = GetRecognitionObservations(resolvedLoopId);
        foreach (var observation in recognitionObservations)
            AddIdentityRelationship(relationships, observation.MemberId, "loop-member",
                $"{observation.Modality}-recognized-by", observation.RobotId, "robot", resolvedLoopId);

        var evidenceSignals = BuildIdentityGraphEvidenceSignals(resolvedLoopId, graphRobot, recognitionObservations);
        var contentHash = ComputeIdentityGraphContentHash(_account.AccountId, resolvedLoopId, graphRobot, people, members,
            relationships, evidenceSignals);

        var signaturePayload = BuildIdentityGraphSignaturePayload(_account.AccountId, resolvedLoopId, contentHash);
        var signature = SignIdentityGraphPayload(signaturePayload);
        var revokedAnchors = _revokedIdentityGraphAnchors.ToArray();
        var admissionAssessment = BuildSignedIdentityGraphAdmissionAssessment(_account.AccountId, resolvedLoopId,
            contentHash,
            evidenceSignals, revokedAnchors);
        var evidenceBundle = BuildSignedIdentityGraphEvidenceBundle(_account.AccountId, resolvedLoopId, graphRobot,
            contentHash, signature, admissionAssessment, people.Length, members.Count, relationships.Count,
            evidenceSignals.Count, SummarizeIdentityGraphRelationshipKinds(relationships),
            SummarizeIdentityGraphEvidenceSignalKinds(evidenceSignals));

        return new IdentityGraphSnapshot
        {
            AccountId = _account.AccountId,
            LoopId = resolvedLoopId,
            RobotId = graphRobot.RobotId,
            DeviceId = graphRobot.DeviceId,
            SnapshotVersion = IdentityGraphSnapshotVersion,
            ContentHash = contentHash,
            SignatureAlgorithm = IdentityGraphSignatureAlgorithm,
            SignatureKeyId = IdentityGraphSignatureKeyId,
            SignaturePayload = signaturePayload,
            Signature = signature,
            AdmissionAssessment = admissionAssessment,
            EvidenceBundle = evidenceBundle,
            People = people,
            Members = members,
            Relationships = relationships,
            EvidenceSignals = evidenceSignals
        };
    }

    private DeviceRegistration ResolveRobotForLoop(string loopId)
    {
        var loop = _loops.FirstOrDefault(item =>
            item.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase));
        if (loop is null)
            return _robot;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(loop.RobotId)) keys.Add(loop.RobotId);
        if (!string.IsNullOrWhiteSpace(loop.RobotFriendlyId)) keys.Add(loop.RobotFriendlyId);

        return _devices.Values.FirstOrDefault(device =>
                   keys.Contains(device.DeviceId) || keys.Contains(device.RobotId))
               ?? _robot;
    }


    public void RevokeIdentityGraphAnchor(string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return;

        lock (_syncRoot)
        {
            _revokedIdentityGraphAnchors.Add(anchor.Trim());
        }

        TouchState();
    }

    public LoopMemberRecord AddLoopMember(string loopId, string? accountId, string? email, string? firstName,
        string? lastName, string? gender, long? birthday, bool isChild, string type, string? legalGuardianId = null,
        bool markPortalEdited = false)
    {
        var member = new LoopMemberRecord
        {
            LoopId = loopId,
            AccountId = accountId,
            Email = email?.Trim(),
            FirstName = firstName?.Trim(),
            LastName = lastName?.Trim(),
            Gender = gender,
            Birthday = birthday,
            IsChild = isChild,
            Type = type,
            Status = "active",
            LegalGuardianId = legalGuardianId?.Trim(),
            PortalEditedUtc = markPortalEdited ? DateTimeOffset.UtcNow : null
        };
        lock (_syncRoot)
        {
            _loopMembers.Add(member);
        }

        TouchState();
        return member;
    }

    public LoopMemberRecord UpdateLoopMember(string loopId, string memberId, string? firstName, string? lastName,
        string? gender, long? birthday, bool isChild, string? nickname, string? phoneticName,
        bool markPortalEdited = false)
    {
        lock (_syncRoot)
        {
            var index = _loopMembers.FindIndex(m =>
                m.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                m.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException($"Member '{memberId}' not found in loop '{loopId}'.");

            var existing = _loopMembers[index];
            _loopMembers[index] = new LoopMemberRecord
            {
                Id = existing.Id,
                LoopId = existing.LoopId,
                AccountId = existing.AccountId,
                Email = existing.Email,
                FirstName = firstName?.Trim() ?? existing.FirstName,
                LastName = lastName?.Trim() ?? existing.LastName,
                Gender = gender ?? existing.Gender,
                Birthday = birthday ?? existing.Birthday,
                IsChild = isChild,
                PhoneNumber = existing.PhoneNumber,
                Status = existing.Status,
                Type = existing.Type,
                Nickname = nickname ?? existing.Nickname,
                PhoneticName = phoneticName ?? existing.PhoneticName,
                FaceEnrolled = existing.FaceEnrolled,
                VoiceEnrolled = existing.VoiceEnrolled,
                LegalGuardianId = existing.LegalGuardianId,
                AgreementId = existing.AgreementId,
                CreatedUtc = existing.CreatedUtc,
                PortalEditedUtc = markPortalEdited ? DateTimeOffset.UtcNow : existing.PortalEditedUtc
            };
        }

        TouchState();
        return GetLoopMember(loopId, memberId);
    }

    public bool RemoveLoopMember(string loopId, string memberId)
    {
        lock (_syncRoot)
        {
            var index = _loopMembers.FindIndex(m =>
                m.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                m.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;

            var existing = _loopMembers[index];
            _loopMembers[index] = new LoopMemberRecord
            {
                Id = existing.Id,
                LoopId = existing.LoopId,
                AccountId = existing.AccountId,
                Email = existing.Email,
                FirstName = existing.FirstName,
                LastName = existing.LastName,
                Gender = existing.Gender,
                Birthday = existing.Birthday,
                IsChild = existing.IsChild,
                PhoneNumber = existing.PhoneNumber,
                Status = "removed",
                Type = existing.Type,
                Nickname = existing.Nickname,
                PhoneticName = existing.PhoneticName,
                FaceEnrolled = existing.FaceEnrolled,
                VoiceEnrolled = existing.VoiceEnrolled,
                LegalGuardianId = existing.LegalGuardianId,
                AgreementId = existing.AgreementId,
                CreatedUtc = existing.CreatedUtc,
                PortalEditedUtc = existing.PortalEditedUtc
            };
        }

        TouchState();
        return true;
    }

    public LoopMemberRecord SetMemberEnrollment(string loopId, string memberId, bool? face, bool? voice)
    {
        lock (_syncRoot)
        {
            var index = _loopMembers.FindIndex(m =>
                m.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                m.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException($"Member '{memberId}' not found in loop '{loopId}'.");

            var existing = _loopMembers[index];
            _loopMembers[index] = new LoopMemberRecord
            {
                Id = existing.Id,
                LoopId = existing.LoopId,
                AccountId = existing.AccountId,
                Email = existing.Email,
                FirstName = existing.FirstName,
                LastName = existing.LastName,
                Gender = existing.Gender,
                Birthday = existing.Birthday,
                IsChild = existing.IsChild,
                PhoneNumber = existing.PhoneNumber,
                Status = existing.Status,
                Type = existing.Type,
                Nickname = existing.Nickname,
                PhoneticName = existing.PhoneticName,
                FaceEnrolled = face ?? existing.FaceEnrolled,
                VoiceEnrolled = voice ?? existing.VoiceEnrolled,
                LegalGuardianId = existing.LegalGuardianId,
                AgreementId = existing.AgreementId,
                CreatedUtc = existing.CreatedUtc,
            PortalEditedUtc = existing.PortalEditedUtc
            };
        }

        TouchState();
        return GetLoopMember(loopId, memberId);
    }


    public IReadOnlyList<RecognitionObservationRecord> GetRecognitionObservations(string loopId)
    {
        return _recognitionObservations
            .Where(observation => observation.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(observation => observation.ObservedUtc)
            .ThenBy(observation => observation.ObservationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RecognitionObservationRecord RecordRecognitionObservation(string loopId, string memberId, string modality,
        string outcome, double? confidence = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(loopId)) throw new ArgumentException("Loop id is required.", nameof(loopId));
        if (string.IsNullOrWhiteSpace(memberId))
            throw new ArgumentException("Member id is required.", nameof(memberId));
        if (string.IsNullOrWhiteSpace(modality))
            throw new ArgumentException("Recognition modality is required.", nameof(modality));

        var member = GetLoopMember(loopId, memberId);
        var normalizedModality = modality.Trim().ToLowerInvariant();
        var observation = new RecognitionObservationRecord
        {
            LoopId = loopId.Trim(),
            MemberId = member.Id,
            RobotId = _robot.RobotId,
            Modality = normalizedModality,
            Outcome = string.IsNullOrWhiteSpace(outcome) ? "recognized" : outcome.Trim().ToLowerInvariant(),
            Confidence = confidence,
            Source = source?.Trim()
        };

        lock (_syncRoot)
        {
            _recognitionObservations.Add(observation);
        }

        TouchState();
        return observation;
    }

    public IReadOnlyList<UpdateManifest> ListUpdates(string? subsystem = null, string? filter = null)
    {
        return _updates
            .Where(update =>
                subsystem is null || update.Subsystem.Equals(subsystem, StringComparison.OrdinalIgnoreCase))
            .Where(update => filter is null || string.Equals(update.Filter, filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public UpdateManifest? GetUpdateFrom(string? subsystem, string? fromVersion, string? filter)
    {
        return ListUpdates(subsystem, filter)
            .FirstOrDefault(update =>
                IsUpdateNewerThanRequest(update.ToVersion, fromVersion));
    }

    public UpdateManifest CreateUpdate(string? fromVersion, string? toVersion, string? changes, string? shaHash,
        long? length, string? subsystem, string? filter, IDictionary<string, object?>? dependencies)
    {
        var updateId = $"upd-{Interlocked.Increment(ref _nextUpdateIdSeed)}";
        var update = new UpdateManifest
        {
            UpdateId = updateId,
            FromVersion = fromVersion ?? "unknown",
            ToVersion = toVersion ?? fromVersion ?? "unknown",
            Changes = changes ?? string.Empty,
            Url = $"https://api.jibo.com/update/{updateId}",
            ShaHash = shaHash ?? "fake-sha-hash",
            Length = length ?? 0,
            Subsystem = subsystem ?? "unknown",
            Filter = filter
        };

        _updates.Add(update);
        TouchState();
        return update;
    }

    public UpdateManifest RemoveUpdate(string? updateId)
    {
        var existing = _updates.FirstOrDefault(update => update.UpdateId == updateId);
        if (existing is null)
            return new UpdateManifest
            {
                UpdateId = updateId ?? "unknown-update",
                Changes = "Update not found",
                Url = "https://api.jibo.com/update/missing",
                ShaHash = "missing",
                Subsystem = "unknown"
            };

        _updates.Remove(existing);
        TouchState();
        return existing;
    }

    public IReadOnlyList<MediaRecord> ListMedia(IReadOnlyList<string>? loopIds = null, long? after = null,
        long? before = null)
    {
        return _media
            .Where(item => !item.IsDeleted)
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
            if (!paths.Contains(_media[i].Path)) continue;

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

        if (replacements.Count > 0) TouchState();

        return replacements;
    }

    public MediaRecord CreateMedia(string loopId, string path, string type, string reference, bool isEncrypted,
        IDictionary<string, object?>? meta)
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

        var existingIndex =
            _media.FindIndex(existing => existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
            _media[existingIndex] = item;
        else
            _media.Add(item);

        TouchState();
        return item;
    }

    public IReadOnlyList<BackupRecord> GetBackups()
    {
        return _backups.ToArray();
    }

    public BackupRecord CreateBackup(string loopId, string name)
    {
        // A backup is a point-in-time copy of cloud state, not a container for every
        // backup that preceded it. Including _backups here causes recursive growth:
        // each new backup embeds all prior backup payloads and roughly doubles the
        // persisted snapshot size.
        var snapshotJson = JsonSerializer.Serialize(CaptureSnapshot(DateTimeOffset.UtcNow, includeBackups: false),
            PersistenceJsonOptions);
        var backup = new BackupRecord
        {
            LoopId = string.IsNullOrWhiteSpace(loopId) ? null : loopId.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? "backup" : name.Trim(),
            SnapshotJson = snapshotJson
        };

        _backups.Add(backup);
        TouchState();
        return backup;
    }

    public BackupRecord? RestoreBackup(string? backupId = null)
    {
        BackupRecord? backup;
        lock (_syncRoot)
        {
            backup = string.IsNullOrWhiteSpace(backupId)
                ? _backups.OrderByDescending(item => item.CreatedUtc).FirstOrDefault()
                : _backups.FirstOrDefault(item => item.BackupId.Equals(backupId, StringComparison.OrdinalIgnoreCase));

            if (backup is null || string.IsNullOrWhiteSpace(backup.SnapshotJson))
                return null;

            PersistentStateSnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<PersistentStateSnapshot>(backup.SnapshotJson,
                    PersistenceJsonOptions);
            }
            catch
            {
                snapshot = null;
            }

            if (snapshot is null)
                return null;

            var cleanupApplied = ApplySnapshot(snapshot);
            Interlocked.Increment(ref _revision);
            if (cleanupApplied)
            {
                // Repair happened while loading the backup; the refreshed snapshot will be persisted below.
            }
            SavePersistedStateLocked(DateTimeOffset.UtcNow);
        }
        return backup;
    }

    public IReadOnlyList<CalendarEventRecord> GetCalendarEvents(string? loopId = null)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(loopId) ? ResolveDefaultLoopId() : loopId.Trim();
        return _calendarEvents
            .Where(calendarEvent => calendarEvent.IsEnabled)
            .Where(calendarEvent =>
                string.Equals(calendarEvent.LoopId, resolvedLoopId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(calendarEvent => calendarEvent.Date)
            .ThenBy(calendarEvent => calendarEvent.TimeLabel ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(calendarEvent => calendarEvent.Summary, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public CalendarEventRecord UpsertCalendarEvent(CalendarEventRecord calendarEvent)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(calendarEvent.LoopId)
            ? ResolveDefaultLoopId()
            : calendarEvent.LoopId.Trim();
        var normalizedId = string.IsNullOrWhiteSpace(calendarEvent.Id)
            ? $"calendar-{resolvedLoopId}-{Slugify(calendarEvent.Summary)}"
            : calendarEvent.Id.Trim();

        var resolvedCalendarEvent = new CalendarEventRecord
        {
            Id = normalizedId,
            LoopId = resolvedLoopId,
            Summary =
                string.IsNullOrWhiteSpace(calendarEvent.Summary) ? "Calendar event" : calendarEvent.Summary.Trim(),
            TimeLabel = string.IsNullOrWhiteSpace(calendarEvent.TimeLabel) ? null : calendarEvent.TimeLabel.Trim(),
            Date = calendarEvent.Date,
            EndDate = calendarEvent.EndDate,
            IsAllDay = calendarEvent.IsAllDay,
            IsEnabled = calendarEvent.IsEnabled,
            Source = string.IsNullOrWhiteSpace(calendarEvent.Source) ? "manual" : calendarEvent.Source.Trim(),
            MemberId = calendarEvent.MemberId,
            Created = calendarEvent.Created
        };

        var existingIndex = _calendarEvents.FindIndex(existing =>
            string.Equals(existing.Id, normalizedId, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _calendarEvents[existingIndex] = resolvedCalendarEvent;
        else
            _calendarEvents.Add(resolvedCalendarEvent);

        TouchState();
        return resolvedCalendarEvent;
    }

    public IReadOnlyList<GreetingPresenceRecord> GetGreetingPresences(string? loopId = null)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(loopId) ? ResolveDefaultLoopId() : loopId.Trim();
        return _greetingPresences
            .Where(greeting =>
                string.Equals(greeting.LoopId, resolvedLoopId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static greeting => greeting.LastGreetedUtc ?? greeting.LastSeenUtc)
            .ThenByDescending(static greeting => greeting.UpdatedUtc)
            .ToArray();
    }

    public GreetingPresenceRecord UpsertGreetingPresence(GreetingPresenceRecord greetingPresence)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(greetingPresence.LoopId)
            ? ResolveDefaultLoopId()
            : greetingPresence.LoopId.Trim();
        var resolvedPersonId = string.IsNullOrWhiteSpace(greetingPresence.PersonId)
            ? greetingPresence.SpeakerId?.Trim() ?? "unknown-person"
            : greetingPresence.PersonId.Trim();
        var normalizedId = string.IsNullOrWhiteSpace(greetingPresence.Id)
            ? $"greeting-presence-{resolvedLoopId}-{Slugify(resolvedPersonId)}"
            : greetingPresence.Id.Trim();
        var now = DateTimeOffset.UtcNow;
        var resolvedPresence = new GreetingPresenceRecord
        {
            Id = normalizedId,
            AccountId = string.IsNullOrWhiteSpace(greetingPresence.AccountId)
                ? _account.AccountId
                : greetingPresence.AccountId.Trim(),
            LoopId = resolvedLoopId,
            PersonId = resolvedPersonId,
            SpeakerId =
                string.IsNullOrWhiteSpace(greetingPresence.SpeakerId) ? null : greetingPresence.SpeakerId.Trim(),
            PreferredName = string.IsNullOrWhiteSpace(greetingPresence.PreferredName)
                ? null
                : greetingPresence.PreferredName.Trim(),
            LastSeenUtc = greetingPresence.LastSeenUtc == default ? now : greetingPresence.LastSeenUtc,
            LastGreetedUtc = greetingPresence.LastGreetedUtc,
            LastGreetingRoute = string.IsNullOrWhiteSpace(greetingPresence.LastGreetingRoute)
                ? null
                : greetingPresence.LastGreetingRoute.Trim(),
            LastGreetingIntent = string.IsNullOrWhiteSpace(greetingPresence.LastGreetingIntent)
                ? null
                : greetingPresence.LastGreetingIntent.Trim(),
            CreatedUtc = greetingPresence.CreatedUtc == default ? now : greetingPresence.CreatedUtc,
            UpdatedUtc = now
        };

        var existingIndex = _greetingPresences.FindIndex(existing =>
            string.Equals(existing.LoopId, resolvedLoopId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.PersonId, resolvedPersonId, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _greetingPresences[existingIndex] = resolvedPresence;
        else
            _greetingPresences.Add(resolvedPresence);

        TouchState();
        return resolvedPresence;
    }

    public bool ShouldCreateSymmetricKey(string loopId)
    {
        return !_symmetricKeys.ContainsKey(loopId);
    }

    public string GetOrCreateSymmetricKey(string loopId)
    {
        if (_symmetricKeys.TryGetValue(loopId, out var existing)) return existing;

        var key = Convert.ToBase64String(Encoding.UTF8.GetBytes($"open-jibo-symmetric-key:{loopId}"));
        if (!_symmetricKeys.TryAdd(loopId, key)) return _symmetricKeys[loopId];

        TouchState();
        return key;
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
        TouchState();
        return record;
    }

    public KeyRequestRecord GetKeyRequest(string loopId, string? requestId, string? publicKey)
    {
        if (!string.IsNullOrWhiteSpace(requestId) && _keyRequests.TryGetValue(requestId, out var record)) return record;

        return new KeyRequestRecord
        {
            RequestId = requestId ?? "unknown-request",
            LoopId = loopId,
            PublicKey = publicKey ?? string.Empty
        };
    }

    public IReadOnlyList<KeyRequestRecord> GetIncomingKeyRequests()
    {
        return [];
    }

    public IReadOnlyList<KeyRequestRecord> GetBinaryRequests()
    {
        return [];
    }

    public IReadOnlyList<HolidayRecord> GetHolidays(string? loopId = null)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(loopId) ? ResolveDefaultLoopId() : loopId.Trim();
        var years = new[] { DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Year + 1 };

        var systemHolidays = years
            .SelectMany(year => _holidayCalendarProvider.GetPublicHolidays(null, year))
            .Where(holiday => holiday.IsEnabled)
            .Select(holiday => new HolidayRecord
            {
                Id = holiday.Id,
                EventId = holiday.EventId,
                Name = holiday.Name,
                Category = holiday.Category,
                Subcategory = holiday.Subcategory,
                LoopId = resolvedLoopId,
                MemberId = holiday.MemberId,
                IsEnabled = true,
                Date = holiday.Date,
                EndDate = holiday.EndDate,
                Source = holiday.Source,
                CountryCode = holiday.CountryCode,
                Created = holiday.Created
            })
            .ToList();

        var overrides = _holidayOverrides
            .Where(holiday => string.Equals(holiday.LoopId, resolvedLoopId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var overrideHoliday in overrides.Where(overrideHoliday =>
                     !string.IsNullOrWhiteSpace(overrideHoliday.EventId)))
            systemHolidays.RemoveAll(systemHoliday =>
                string.Equals(systemHoliday.EventId, overrideHoliday.EventId, StringComparison.OrdinalIgnoreCase));

        return systemHolidays
            .Concat(overrides.Where(holiday => holiday.IsEnabled))
            .OrderBy(holiday => holiday.Date)
            .ThenBy(holiday => holiday.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<CommuteProfileRecord> GetCommuteProfiles(string? loopId = null)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(loopId) ? ResolveDefaultLoopId() : loopId.Trim();
        return _commuteProfiles
            .Where(commute => commute.IsEnabled)
            .Where(commute => string.Equals(commute.LoopId, resolvedLoopId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static commute => commute.IsComplete)
            .ThenByDescending(static commute => commute.Updated)
            .ToArray();
    }

    public CommuteProfileRecord UpsertCommuteProfile(CommuteProfileRecord commuteProfile)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(commuteProfile.LoopId)
            ? ResolveDefaultLoopId()
            : commuteProfile.LoopId.Trim();
        var normalizedId = string.IsNullOrWhiteSpace(commuteProfile.Id)
            ? $"commute-{resolvedLoopId}"
            : commuteProfile.Id.Trim();

        var resolvedProfile = new CommuteProfileRecord
        {
            Id = normalizedId,
            LoopId = resolvedLoopId,
            MemberId = string.IsNullOrWhiteSpace(commuteProfile.MemberId) ? null : commuteProfile.MemberId.Trim(),
            IsEnabled = commuteProfile.IsEnabled,
            IsComplete = commuteProfile.IsComplete,
            Mode = string.IsNullOrWhiteSpace(commuteProfile.Mode) ? "driving" : commuteProfile.Mode.Trim(),
            WorkHour = commuteProfile.WorkHour,
            WorkMinute = commuteProfile.WorkMinute,
            OriginName = string.IsNullOrWhiteSpace(commuteProfile.OriginName) ? null : commuteProfile.OriginName.Trim(),
            DestinationName = string.IsNullOrWhiteSpace(commuteProfile.DestinationName)
                ? null
                : commuteProfile.DestinationName.Trim(),
            TypicalDurationMinutes = commuteProfile.TypicalDurationMinutes > 0
                ? commuteProfile.TypicalDurationMinutes
                : 25,
            Created = commuteProfile.Created == default ? DateTimeOffset.UtcNow : commuteProfile.Created,
            Updated = DateTimeOffset.UtcNow
        };

        var existingIndex = _commuteProfiles.FindIndex(existing =>
            string.Equals(existing.Id, normalizedId, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _commuteProfiles[existingIndex] = resolvedProfile;
        else
            _commuteProfiles.Add(resolvedProfile);

        TouchState();
        return resolvedProfile;
    }

    public HolidayRecord UpsertHoliday(HolidayRecord holiday)
    {
        var resolvedLoopId = string.IsNullOrWhiteSpace(holiday.LoopId) ? ResolveDefaultLoopId() : holiday.LoopId.Trim();
        var normalizedEventId = string.IsNullOrWhiteSpace(holiday.EventId)
            ? $"holiday-{resolvedLoopId}-{Slugify(holiday.Name)}"
            : holiday.EventId.Trim();
        var normalizedId = string.IsNullOrWhiteSpace(holiday.Id) ? normalizedEventId : holiday.Id.Trim();
        var resolvedHoliday = new HolidayRecord
        {
            Id = normalizedId,
            EventId = normalizedEventId,
            Name = string.IsNullOrWhiteSpace(holiday.Name) ? "Holiday" : holiday.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(holiday.Category) ? "holiday" : holiday.Category.Trim(),
            Subcategory = holiday.Subcategory,
            LoopId = resolvedLoopId,
            MemberId = holiday.MemberId,
            IsEnabled = holiday.IsEnabled,
            Date = holiday.Date,
            EndDate = holiday.EndDate,
            Source = string.IsNullOrWhiteSpace(holiday.Source) ? "manual" : holiday.Source.Trim(),
            CountryCode = string.IsNullOrWhiteSpace(holiday.CountryCode) ? "US" : holiday.CountryCode.Trim(),
            Created = holiday.Created
        };

        var existingIndex = _holidayOverrides.FindIndex(existing =>
            string.Equals(existing.LoopId, resolvedLoopId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.EventId, normalizedEventId, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _holidayOverrides[existingIndex] = resolvedHoliday;
        else
            _holidayOverrides.Add(resolvedHoliday);

        TouchState();
        return resolvedHoliday;
    }

    public void UpdateRobot(DeviceRegistration registration)
    {
        var oldRobotId = _robot.RobotId;
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

        for (var i = 0; i < _people.Count; i++)
        {
            var person = _people[i];
            if (!string.Equals(person.RobotId, oldRobotId, StringComparison.OrdinalIgnoreCase)) continue;

            _people[i] = new PersonRecord
            {
                PersonId = person.PersonId,
                AccountId = person.AccountId,
                LoopId = person.LoopId,
                RobotId = registration.RobotId,
                DisplayName = person.DisplayName,
                Alias = person.Alias,
                IsPrimary = person.IsPrimary,
                CreatedUtc = person.CreatedUtc,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        for (var i = 0; i < _loopMembers.Count; i++)
        {
            var member = _loopMembers[i];
            if (string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase) ||
                (member.AccountId != null && member.AccountId.Equals(oldRobotId, StringComparison.OrdinalIgnoreCase)))
                _loopMembers[i] = new LoopMemberRecord
                {
                    Id = member.Id,
                    LoopId = member.LoopId,
                    AccountId = string.Equals(member.AccountId, oldRobotId, StringComparison.OrdinalIgnoreCase)
                        ? registration.RobotId
                        : member.AccountId,
                    Email = member.Email,
                    FirstName = member.FirstName,
                    LastName = member.LastName,
                    Gender = member.Gender,
                    Birthday = member.Birthday,
                    IsChild = member.IsChild,
                    PhoneNumber = member.PhoneNumber,
                    Status = member.Status,
                    Type = member.Type,
                    Nickname = member.Nickname,
                    PhoneticName = member.PhoneticName,
                    FaceEnrolled = member.FaceEnrolled,
                    VoiceEnrolled = member.VoiceEnrolled,
                    LegalGuardianId = member.LegalGuardianId,
                    AgreementId = member.AgreementId,
                    CreatedUtc = member.CreatedUtc,
                PortalEditedUtc = member.PortalEditedUtc
                };
        }

        for (var i = 0; i < _loops.Count; i++)
        {
            var loop = _loops[i];
            if (!string.Equals(loop.RobotId, oldRobotId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(loop.RobotFriendlyId, oldRobotId, StringComparison.OrdinalIgnoreCase))
                continue;

            _loops[i] = new LoopRecord
            {
                LoopId = loop.LoopId,
                Name = loop.Name,
                OwnerAccountId = loop.OwnerAccountId,
                RobotId = registration.RobotId,
                RobotFriendlyId = registration.DeviceId,
                IsSuspended = loop.IsSuspended,
                CreatedUtc = loop.CreatedUtc,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        TouchState();
    }


    private DeviceRegistration? ResolveIdentityGraphRobotMemberDevice(LoopMemberRecord member)
    {
        if (!string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase)) return null;

        var candidates = new[]
                { member.AccountId, member.Id, member.Nickname, member.FirstName, member.Email }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!.Trim());

        foreach (var candidate in candidates)
        {
            var match = _devices.Values.FirstOrDefault(device =>
                device.RobotId.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                device.DeviceId.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                device.FriendlyName.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return null;
    }

    private static void AddLoopMemberRelationshipModel(ICollection<IdentityGraphRelationship> relationships,
        LoopMemberRecord member, IReadOnlyCollection<LoopMemberRecord> members, string loopId)
    {
        if (string.Equals(member.Type, "owner", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase))
            return;

        var owner = members.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, "owner", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Status, "active", StringComparison.OrdinalIgnoreCase));
        if (owner is null || string.Equals(owner.Id, member.Id, StringComparison.OrdinalIgnoreCase)) return;

        var (memberToOwner, ownerToMember) = NormalizeLoopMemberRelationship(member.Type);
        if (string.IsNullOrWhiteSpace(memberToOwner) || string.IsNullOrWhiteSpace(ownerToMember)) return;

        AddIdentityRelationship(relationships, member.Id, "loop-member", memberToOwner, owner.Id, "loop-member",
            loopId);
        AddIdentityRelationship(relationships, owner.Id, "loop-member", ownerToMember, member.Id, "loop-member",
            loopId);
    }

    private static (string MemberToOwner, string OwnerToMember) NormalizeLoopMemberRelationship(string? memberType)
    {
        return memberType?.Trim().ToLowerInvariant() switch
        {
            "family" or "household" => ("family-member-of", "has-family-member"),
            "friend" => ("friend-of", "has-friend"),
            "caregiver" or "guardian" => ("caregiver-for", "has-caregiver"),
            _ => ("loopmate-of", "has-loopmate")
        };
    }

    private static string ComputeIdentityGraphContentHash(string accountId, string loopId, DeviceRegistration robot,
        IReadOnlyCollection<PersonRecord> people, IReadOnlyCollection<LoopMemberRecord> members,
        IReadOnlyCollection<IdentityGraphRelationship> relationships,
        IReadOnlyCollection<IdentityGraphEvidenceSignal> evidenceSignals)
    {
        var lines = new List<string>
        {
            $"snapshot-version|{IdentityGraphSnapshotVersion}",
            $"account|{accountId}",
            $"loop|{loopId}",
            $"robot|{robot.RobotId}|device|{robot.DeviceId}|cert|{robot.CertificateThumbprint}|issued|{robot.IssuedIdentityId}|build|{robot.BuildHash}|config|{robot.ConfigHash}"
        };

        lines.AddRange(people
            .OrderBy(person => person.PersonId, StringComparer.OrdinalIgnoreCase)
            .Select(person =>
                $"person|{person.PersonId}|account|{person.AccountId}|robot|{person.RobotId}|primary|{person.IsPrimary}"));

        lines.AddRange(members
            .OrderBy(member => member.Id, StringComparer.OrdinalIgnoreCase)
            .Select(member =>
                $"member|{member.Id}|account|{member.AccountId}|type|{member.Type}|status|{member.Status}|child|{member.IsChild}|face|{member.FaceEnrolled}|voice|{member.VoiceEnrolled}|guardian|{member.LegalGuardianId}"));

        lines.AddRange(relationships
            .OrderBy(relationship => relationship.SubjectId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relationship => relationship.SubjectKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relationship => relationship.Relationship, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relationship => relationship.ObjectId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relationship => relationship.ObjectKind, StringComparer.OrdinalIgnoreCase)
            .Select(relationship =>
                $"relationship|{relationship.SubjectId}|{relationship.SubjectKind}|{relationship.Relationship}|{relationship.ObjectId}|{relationship.ObjectKind}|{relationship.LoopId}"));

        lines.AddRange(evidenceSignals
            .OrderBy(signal => signal.SignalKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(signal => signal.SignalId, StringComparer.OrdinalIgnoreCase)
            .Select(signal =>
                $"evidence|{signal.SignalKind}|{signal.SignalId}|{signal.Value}|{signal.Role}|{signal.LoopId}"));

        var payload = string.Join('\n', lines);
        return ComputeSha256Hex(payload);
    }

    private static IReadOnlyList<string> SummarizeIdentityGraphRelationshipKinds(
        IEnumerable<IdentityGraphRelationship> relationships)
    {
        return relationships
            .GroupBy(relationship => relationship.Relationship, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
    }

    private static IReadOnlyList<string> SummarizeIdentityGraphEvidenceSignalKinds(
        IEnumerable<IdentityGraphEvidenceSignal> evidenceSignals)
    {
        return evidenceSignals
            .GroupBy(signal => signal.SignalKind, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}")
            .ToArray();
    }


    private static IReadOnlyList<IdentityGraphEvidenceSignal> BuildIdentityGraphEvidenceSignals(string loopId,
        DeviceRegistration robot, IReadOnlyCollection<RecognitionObservationRecord>? recognitionObservations = null)
    {
        var signals = new List<IdentityGraphEvidenceSignal>();

        AddIdentityEvidenceSignal(signals, "device-id", robot.DeviceId, robot.DeviceId, loopId);
        AddIdentityEvidenceSignal(signals, "robot-id", robot.RobotId, robot.RobotId, loopId);
        AddIdentityEvidenceSignal(signals, "firmware-version", robot.RobotId, robot.FirmwareVersion, loopId);
        AddIdentityEvidenceSignal(signals, "application-version", robot.RobotId, robot.ApplicationVersion, loopId);
        AddIdentityEvidenceSignal(signals, "certificate-thumbprint", robot.RobotId, robot.CertificateThumbprint,
            loopId);
        AddIdentityEvidenceSignal(signals, "issued-identity", robot.RobotId, robot.IssuedIdentityId, loopId);
        AddIdentityEvidenceSignal(signals, "build-hash", robot.RobotId, robot.BuildHash, loopId);
        AddIdentityEvidenceSignal(signals, "config-hash", robot.RobotId, robot.ConfigHash, loopId);

        foreach (var mapping in robot.HostMappings
                     .Where(mapping => !mapping.Key.StartsWith("openjibo.", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(mapping => mapping.Key, StringComparer.OrdinalIgnoreCase))
            AddIdentityEvidenceSignal(signals, "host-mapping", mapping.Key, mapping.Value, loopId);

        foreach (var observation in recognitionObservations ?? [])
            AddIdentityEvidenceSignal(signals, $"recognition-{observation.Modality}", observation.ObservationId,
                $"{observation.MemberId}:{observation.Outcome}:{observation.Confidence?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}",
                loopId);

        return signals;
    }

    private static void AddIdentityEvidenceSignal(ICollection<IdentityGraphEvidenceSignal> signals, string signalKind,
        string? signalId, string? value, string loopId)
    {
        if (string.IsNullOrWhiteSpace(signalId) || string.IsNullOrWhiteSpace(value)) return;

        signals.Add(new IdentityGraphEvidenceSignal
        {
            SignalKind = signalKind,
            SignalId = signalId.Trim(),
            Value = value.Trim(),
            LoopId = loopId
        });
    }

    private static IdentityGraphAdmissionAssessment BuildSignedIdentityGraphAdmissionAssessment(string accountId,
        string loopId,
        string contentHash, IReadOnlyCollection<IdentityGraphEvidenceSignal> evidenceSignals,
        IReadOnlyCollection<string> revokedAnchors)
    {
        var assessment = BuildIdentityGraphAdmissionAssessment(evidenceSignals, revokedAnchors);
        var decisionPayload = BuildIdentityGraphAdmissionDecisionPayload(accountId, loopId, contentHash, assessment);
        var decisionHash = ComputeSha256Hex(decisionPayload);

        return new IdentityGraphAdmissionAssessment
        {
            PolicyVersion = assessment.PolicyVersion,
            Recommendation = assessment.Recommendation,
            Reasons = assessment.Reasons,
            RequiredEvidence = assessment.RequiredEvidence,
            SatisfiedEvidence = assessment.SatisfiedEvidence,
            BlockingEvidence = assessment.BlockingEvidence,
            RecommendedActions = assessment.RecommendedActions,
            RevocationChecks = assessment.RevocationChecks,
            RevocationAnchors = assessment.RevocationAnchors,
            RevocationListHash = assessment.RevocationListHash,
            DecisionPayload = decisionPayload,
            DecisionHash = decisionHash,
            SignatureAlgorithm = IdentityGraphSignatureAlgorithm,
            SignatureKeyId = IdentityGraphAdmissionSignatureKeyId,
            Signature = SignIdentityGraphPayload(decisionPayload)
        };
    }

    private static IdentityGraphAdmissionAssessment BuildIdentityGraphAdmissionAssessment(
        IReadOnlyCollection<IdentityGraphEvidenceSignal> evidenceSignals,
        IReadOnlyCollection<string> revokedAnchors)
    {
        var revocationListHash = ComputeRevocationListHash(revokedAnchors);

        string[] requiredEvidence =
        [
            "device-id",
            "robot-id",
            "application-version",
            "host-mapping"
        ];

        var presentEvidence = evidenceSignals
            .Select(signal => signal.SignalKind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingEvidence = requiredEvidence
            .Where(required => !presentEvidence.Contains(required))
            .ToArray();

        var satisfiedEvidence = requiredEvidence
            .Where(required => presentEvidence.Contains(required))
            .ToArray();
        var reasons = missingEvidence
            .Select(missing => $"missing-{missing}")
            .ToList();
        var blockingEvidence = missingEvidence
            .Select(missing => $"required:{missing}")
            .ToList();

        var untrustedHostMappings = evidenceSignals
            .Where(signal => signal.SignalKind.Equals("host-mapping", StringComparison.OrdinalIgnoreCase))
            .Where(signal => !IsTrustedOpenJiboHostMappingTarget(signal.Value))
            .Select(signal => $"host-mapping:{signal.SignalId}->{signal.Value}")
            .ToArray();

        if (untrustedHostMappings.Length > 0)
        {
            reasons.Add("untrusted-host-mapping-target");
            blockingEvidence.AddRange(untrustedHostMappings);
        }

        var revocationAnchors = BuildIdentityGraphRevocationAnchors(evidenceSignals);
        var revokedMatches = revocationAnchors
            .Where(anchor => revokedAnchors.Contains(anchor, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (revokedMatches.Length > 0)
        {
            reasons.Add("revoked-identity-anchor");
            blockingEvidence.AddRange(revokedMatches.Select(anchor => $"revoked:{anchor}"));
        }

        if (reasons.Count == 0)
            return new IdentityGraphAdmissionAssessment
            {
                Recommendation = "admit",
                Reasons = ["required-corroborating-evidence-present"],
                RequiredEvidence = requiredEvidence,
                SatisfiedEvidence = satisfiedEvidence,
                RecommendedActions = ["record-signed-snapshot-for-peer-admission"],
                RevocationChecks = ["no-local-revocation-evidence"],
                RevocationAnchors = revocationAnchors,
                RevocationListHash = revocationListHash
            };

        var revocationChecks = revokedMatches.Length > 0
            ? revokedMatches.Select(anchor => $"local-revocation-match:{anchor}").ToArray()
            : ["defer-revocation-admission-until-blocking-evidence-resolved"];

        return new IdentityGraphAdmissionAssessment
        {
            Recommendation = "quarantine",
            Reasons = reasons,
            RequiredEvidence = requiredEvidence,
            SatisfiedEvidence = satisfiedEvidence,
            BlockingEvidence = blockingEvidence,
            RecommendedActions =
                BuildIdentityGraphRecommendedActions(missingEvidence, untrustedHostMappings, revokedMatches),
            RevocationChecks = revocationChecks,
            RevocationAnchors = revocationAnchors,
            RevocationListHash = revocationListHash
        };
    }

    private static string ComputeRevocationListHash(IEnumerable<string> revokedAnchors)
    {
        var payload = string.Join('\n', revokedAnchors
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor))
            .Select(anchor => anchor.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));

        return ComputeSha256Hex(payload);
    }

    private static IReadOnlyList<string> BuildIdentityGraphRevocationAnchors(
        IReadOnlyCollection<IdentityGraphEvidenceSignal> evidenceSignals)
    {
        string[] anchorKinds =
        [
            "device-id",
            "robot-id",
            "certificate-thumbprint",
            "issued-identity"
        ];

        return evidenceSignals
            .Where(signal => anchorKinds.Contains(signal.SignalKind, StringComparer.OrdinalIgnoreCase))
            .OrderBy(signal => signal.SignalKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(signal => signal.SignalId, StringComparer.OrdinalIgnoreCase)
            .Select(signal => $"{signal.SignalKind}:{signal.SignalId}={signal.Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }


    private static IReadOnlyList<string> BuildIdentityGraphRecommendedActions(
        IReadOnlyCollection<string> missingEvidence,
        IReadOnlyCollection<string> untrustedHostMappings,
        IReadOnlyCollection<string> revokedMatches)
    {
        var actions = new List<string>();

        if (missingEvidence.Contains("device-id", StringComparer.OrdinalIgnoreCase) ||
            missingEvidence.Contains("robot-id", StringComparer.OrdinalIgnoreCase))
            actions.Add("verify-robot-identity-before-admission");

        if (missingEvidence.Contains("application-version", StringComparer.OrdinalIgnoreCase))
            actions.Add("capture-current-open-jibo-application-version");

        if (missingEvidence.Contains("host-mapping", StringComparer.OrdinalIgnoreCase))
            actions.Add("record-open-jibo-host-mapping");

        if (untrustedHostMappings.Count > 0)
            actions.Add("redirect-legacy-host-mapping-to-open-jibo-target");

        if (revokedMatches.Count > 0)
            actions.Add("keep-revoked-identity-anchor-quarantined");

        if (actions.Count == 0)
            actions.Add("manual-review-required");

        return actions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsTrustedOpenJiboHostMappingTarget(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var target = value.Trim();
        return target.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               target.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
               target.Contains("openjibo", StringComparison.OrdinalIgnoreCase);
    }


    private static string BuildIdentityGraphAdmissionDecisionPayload(string accountId, string loopId,
        string contentHash,
        IdentityGraphAdmissionAssessment assessment)
    {
        var lines = new[]
        {
            $"policy-version|{assessment.PolicyVersion}",
            $"account|{accountId}",
            $"loop|{loopId}",
            $"content-hash|{contentHash}",
            $"recommendation|{assessment.Recommendation}",
            $"reasons|{string.Join(',', assessment.Reasons.Order(StringComparer.Ordinal))}",
            $"required-evidence|{string.Join(',', assessment.RequiredEvidence.Order(StringComparer.Ordinal))}",
            $"satisfied-evidence|{string.Join(',', assessment.SatisfiedEvidence.Order(StringComparer.Ordinal))}",
            $"blocking-evidence|{string.Join(',', assessment.BlockingEvidence.Order(StringComparer.Ordinal))}",
            $"recommended-actions|{string.Join(',', assessment.RecommendedActions.Order(StringComparer.Ordinal))}",
            $"revocation-checks|{string.Join(',', assessment.RevocationChecks.Order(StringComparer.Ordinal))}",
            $"revocation-anchors|{string.Join(',', assessment.RevocationAnchors.Order(StringComparer.Ordinal))}",
            $"revocation-list-hash|{assessment.RevocationListHash}"
        };

        return string.Join('\n', lines);
    }


    private static IdentityGraphEvidenceBundle BuildSignedIdentityGraphEvidenceBundle(string accountId, string loopId,
        DeviceRegistration robot, string contentHash, string snapshotSignature,
        IdentityGraphAdmissionAssessment admissionAssessment, int peopleCount, int memberCount, int relationshipCount,
        int evidenceSignalCount, IReadOnlyList<string> relationshipKinds, IReadOnlyList<string> evidenceSignalKinds)
    {
        var payload = BuildIdentityGraphEvidenceBundlePayload(accountId, loopId, robot, contentHash, snapshotSignature,
            admissionAssessment, peopleCount, memberCount, relationshipCount, evidenceSignalCount, relationshipKinds,
            evidenceSignalKinds);
        var bundleHash = ComputeSha256Hex(payload);

        var signature = SignIdentityGraphPayload(payload);
        var envelope = BuildIdentityGraphEvidenceBundleEnvelope(payload, bundleHash, signature);

        return new IdentityGraphEvidenceBundle
        {
            AccountId = accountId,
            LoopId = loopId,
            RobotId = robot.RobotId,
            DeviceId = robot.DeviceId,
            SnapshotContentHash = contentHash,
            SnapshotSignature = snapshotSignature,
            AdmissionDecisionHash = admissionAssessment.DecisionHash,
            AdmissionSignature = admissionAssessment.Signature,
            AdmissionPolicyVersion = admissionAssessment.PolicyVersion,
            AdmissionRecommendation = admissionAssessment.Recommendation,
            AdmissionReasons = admissionAssessment.Reasons,
            RequiredEvidence = admissionAssessment.RequiredEvidence,
            SatisfiedEvidence = admissionAssessment.SatisfiedEvidence,
            RecommendedActions = admissionAssessment.RecommendedActions,
            RevocationChecks = admissionAssessment.RevocationChecks,
            RevocationAnchors = admissionAssessment.RevocationAnchors,
            RevocationListHash = admissionAssessment.RevocationListHash,
            TrustPurpose = "peer-admission-retention",
            PeerTransportStatus = "not-enabled",
            ReplicationReadiness =
                admissionAssessment.Recommendation.Equals("admit", StringComparison.OrdinalIgnoreCase)
                    ? "ready-for-retention"
                    : "blocked-by-admission",
            SyncDirection = "snapshot-retention-only",
            PeerAdmissionMode = "offline-signed-evidence",
            RetentionPolicy = "owner-retained-until-peer-admission",
            AdmissionReviewStatus = "requires-local-revocation-check",
            ExportedByCloudVersion = OpenJiboCloudBuildInfo.Version,
            ExportedByService = "open-jibo-cloud",
            DirectPeerTransportAllowed = false,
            PeopleCount = peopleCount,
            MemberCount = memberCount,
            RelationshipCount = relationshipCount,
            EvidenceSignalCount = evidenceSignalCount,
            RelationshipKinds = relationshipKinds,
            EvidenceSignalKinds = evidenceSignalKinds,
            BlockingEvidence = admissionAssessment.BlockingEvidence,
            Payload = payload,
            Envelope = envelope,
            BundleHash = bundleHash,
            SignatureAlgorithm = IdentityGraphSignatureAlgorithm,
            SignatureKeyId = IdentityGraphEvidenceBundleSignatureKeyId,
            Signature = signature
        };
    }


    private static string BuildIdentityGraphEvidenceBundleEnvelope(string payload, string bundleHash, string signature)
    {
        var lines = new[]
        {
            "envelope-version|identity-graph-evidence-envelope-v1",
            $"bundle-hash|{bundleHash}",
            $"bundle-signature-algorithm|{IdentityGraphSignatureAlgorithm}",
            $"bundle-signature-key-id|{IdentityGraphEvidenceBundleSignatureKeyId}",
            $"bundle-signature|{signature}",
            "payload-begin",
            payload,
            "payload-end"
        };

        return string.Join('\n', lines);
    }

    private static string BuildIdentityGraphEvidenceBundlePayload(string accountId, string loopId,
        DeviceRegistration robot,
        string contentHash, string snapshotSignature, IdentityGraphAdmissionAssessment admissionAssessment,
        int peopleCount, int memberCount, int relationshipCount, int evidenceSignalCount,
        IReadOnlyList<string> relationshipKinds, IReadOnlyList<string> evidenceSignalKinds)
    {
        var lines = new[]
        {
            "bundle-version|identity-graph-evidence-bundle-v1",
            $"account|{accountId}",
            $"loop|{loopId}",
            $"robot|{robot.RobotId}",
            $"device|{robot.DeviceId}",
            $"people-count|{peopleCount}",
            $"member-count|{memberCount}",
            $"relationship-count|{relationshipCount}",
            $"evidence-signal-count|{evidenceSignalCount}",
            $"relationship-kinds|{string.Join(',', relationshipKinds)}",
            $"evidence-signal-kinds|{string.Join(',', evidenceSignalKinds)}",
            $"snapshot-version|{IdentityGraphSnapshotVersion}",
            $"snapshot-content-hash|{contentHash}",
            $"snapshot-signature-key-id|{IdentityGraphSignatureKeyId}",
            $"snapshot-signature|{snapshotSignature}",
            $"admission-policy-version|{admissionAssessment.PolicyVersion}",
            $"admission-recommendation|{admissionAssessment.Recommendation}",
            $"admission-reasons|{string.Join(',', admissionAssessment.Reasons.Order(StringComparer.Ordinal))}",
            $"admission-required-evidence|{string.Join(',', admissionAssessment.RequiredEvidence.Order(StringComparer.Ordinal))}",
            $"admission-satisfied-evidence|{string.Join(',', admissionAssessment.SatisfiedEvidence.Order(StringComparer.Ordinal))}",
            $"admission-blocking-evidence|{string.Join(',', admissionAssessment.BlockingEvidence.Order(StringComparer.Ordinal))}",
            $"admission-recommended-actions|{string.Join(',', admissionAssessment.RecommendedActions.Order(StringComparer.Ordinal))}",
            $"admission-revocation-checks|{string.Join(',', admissionAssessment.RevocationChecks.Order(StringComparer.Ordinal))}",
            $"admission-revocation-anchors|{string.Join(',', admissionAssessment.RevocationAnchors.Order(StringComparer.Ordinal))}",
            $"admission-revocation-list-hash|{admissionAssessment.RevocationListHash}",
            "trust-purpose|peer-admission-retention",
            "peer-transport-status|not-enabled",
            $"replication-readiness|{(admissionAssessment.Recommendation.Equals("admit", StringComparison.OrdinalIgnoreCase) ? "ready-for-retention" : "blocked-by-admission")}",
            "sync-direction|snapshot-retention-only",
            "peer-admission-mode|offline-signed-evidence",
            "retention-policy|owner-retained-until-peer-admission",
            "admission-review-status|requires-local-revocation-check",
            $"exported-by-cloud-version|{OpenJiboCloudBuildInfo.Version}",
            "exported-by-service|open-jibo-cloud",
            "direct-peer-transport-allowed|false",
            $"admission-decision-hash|{admissionAssessment.DecisionHash}",
            $"admission-signature-key-id|{admissionAssessment.SignatureKeyId}",
            $"admission-signature|{admissionAssessment.Signature}"
        };

        return string.Join('\n', lines);
    }

    private static string BuildIdentityGraphSignaturePayload(string accountId, string loopId, string contentHash)
    {
        return $"{IdentityGraphSnapshotVersion}|{accountId}|{loopId}|{contentHash}";
    }

    private static string SignIdentityGraphPayload(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(IdentityGraphSigningKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void AddIdentityRelationship(ICollection<IdentityGraphRelationship> relationships, string? subjectId,
        string subjectKind, string relationship, string? objectId, string objectKind, string loopId)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || string.IsNullOrWhiteSpace(objectId)) return;

        var trimmedSubjectId = subjectId.Trim();
        var trimmedObjectId = objectId.Trim();
        if (relationships.Any(existing =>
                existing.SubjectId.Equals(trimmedSubjectId, StringComparison.OrdinalIgnoreCase) &&
                existing.SubjectKind.Equals(subjectKind, StringComparison.OrdinalIgnoreCase) &&
                existing.Relationship.Equals(relationship, StringComparison.OrdinalIgnoreCase) &&
                existing.ObjectId.Equals(trimmedObjectId, StringComparison.OrdinalIgnoreCase) &&
                existing.ObjectKind.Equals(objectKind, StringComparison.OrdinalIgnoreCase) &&
                existing.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase)))
            return;

        relationships.Add(new IdentityGraphRelationship
        {
            SubjectId = trimmedSubjectId,
            SubjectKind = subjectKind,
            Relationship = relationship,
            ObjectId = trimmedObjectId,
            ObjectKind = objectKind,
            LoopId = loopId
        });
    }

    private static bool IsUpdateNewerThanRequest(string candidateVersion, string? fromVersion)
    {
        if (string.IsNullOrWhiteSpace(fromVersion)) return true;

        if (string.IsNullOrWhiteSpace(candidateVersion)) return false;

        if (Version.TryParse(candidateVersion, out var candidate) &&
            Version.TryParse(fromVersion, out var requested))
            return candidate > requested;

        return string.Compare(candidateVersion, fromVersion, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string GenerateSalt()
    {
        Span<byte> saltBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(saltBytes);
        return Convert.ToHexString(saltBytes).ToLowerInvariant();
    }

    private static string HashPassword(string password, string salt)
    {
        return ComputeSha256Hex($"{salt}:{password}");
    }

    private static string ComputeSha256Hex(string value)
    {
        return ComputeSha256Hex(Encoding.UTF8.GetBytes(value));
    }

    private static string ComputeSha256Hex(byte[] value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private void TouchState()
    {
        Interlocked.Increment(ref _revision);
        SavePersistedState();
    }

    private void EnsureDefaultTrustedServers()
    {
        if (_trustedServers.Any(server =>
                server.IsTrustRoot &&
                server.CanonicalHost.Equals("api.openjibo.com", StringComparison.OrdinalIgnoreCase)))
            return;

        _trustedServers.Add(new TrustedServerRecord
        {
            CanonicalHost = "api.openjibo.com",
            DisplayName = "Open Jibo trust root API",
            ServerKind = "managed",
            IsListed = true,
            AcceptsPublicConnections = true,
            ParticipatesInCloudSync = true,
            RequiresHttps = true,
            IsTrustRoot = true,
            IsActive = true,
            Description = "Primary robot-facing hosted trust root for onboarding and managed cloud targets."
        });
    }

    private static string NormalizeTrustedServerHost(string value)
    {
        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return uri.Host;

        return trimmed.TrimEnd('/');
    }

    private static TrustedServerRecord CloneTrustedServer(TrustedServerRecord trustedServer)
    {
        return new TrustedServerRecord
        {
            ServerId = trustedServer.ServerId,
            CanonicalHost = trustedServer.CanonicalHost,
            DisplayName = trustedServer.DisplayName,
            ServerKind = trustedServer.ServerKind,
            IsListed = trustedServer.IsListed,
            AcceptsPublicConnections = trustedServer.AcceptsPublicConnections,
            ParticipatesInCloudSync = trustedServer.ParticipatesInCloudSync,
            RequiresHttps = trustedServer.RequiresHttps,
            IsTrustRoot = trustedServer.IsTrustRoot,
            IsActive = trustedServer.IsActive,
            Description = trustedServer.Description,
            RegisteredAtUtc = trustedServer.RegisteredAtUtc,
            UpdatedAtUtc = trustedServer.UpdatedAtUtc,
            LastSeenAtUtc = trustedServer.LastSeenAtUtc
        };
    }

    private static TrustedServerAdmissionRecord CloneTrustedServerAdmission(TrustedServerAdmissionRecord admission)
    {
        return new TrustedServerAdmissionRecord
        {
            AdmissionId = admission.AdmissionId,
            ServerId = admission.ServerId,
            CanonicalHost = admission.CanonicalHost,
            ServerKind = admission.ServerKind,
            Action = admission.Action,
            ActorDeviceId = admission.ActorDeviceId,
            ActorFriendlyId = admission.ActorFriendlyId,
            Reason = admission.Reason,
            SignatureAlgorithm = admission.SignatureAlgorithm,
            SignatureKeyId = admission.SignatureKeyId,
            Payload = admission.Payload,
            Signature = admission.Signature,
            CreatedUtc = admission.CreatedUtc
        };
    }

    private static string NormalizeServerKind(string? serverKind, string? fallbackServerKind = null)
    {
        var normalized = string.IsNullOrWhiteSpace(serverKind) ? fallbackServerKind : serverKind;
        normalized = string.IsNullOrWhiteSpace(normalized) ? "managed" : normalized.Trim();
        return normalized.Equals("hosted", StringComparison.OrdinalIgnoreCase)
            ? "managed"
            : normalized.ToLowerInvariant();
    }

    private static string NormalizeTrustedServerAction(string action)
    {
        return action.Trim().ToLowerInvariant() switch
        {
            "admit" or "revoke" or "reactivate" or "mark-seen" => action.Trim().ToLowerInvariant(),
            _ => throw new ArgumentException("Invalid trusted server action.", nameof(action))
        };
    }

    private static string BuildTrustedServerAdmissionPayload(
        TrustedServerRecord trustedServer,
        string action,
        string? actorDeviceId,
        string? actorFriendlyId,
        string? reason,
        DateTimeOffset createdUtc)
    {
        var lines = new[]
        {
            $"action|{action}",
            $"server-id|{trustedServer.ServerId}",
            $"canonical-host|{trustedServer.CanonicalHost}",
            $"server-kind|{trustedServer.ServerKind}",
            $"listed|{trustedServer.IsListed}",
            $"accepts-public-connections|{trustedServer.AcceptsPublicConnections}",
            $"participates-in-cloud-sync|{trustedServer.ParticipatesInCloudSync}",
            $"requires-https|{trustedServer.RequiresHttps}",
            $"trust-root|{trustedServer.IsTrustRoot}",
            $"active|{trustedServer.IsActive}",
            $"actor-device-id|{actorDeviceId?.Trim() ?? string.Empty}",
            $"actor-friendly-id|{actorFriendlyId?.Trim() ?? string.Empty}",
            $"reason|{reason?.Trim() ?? string.Empty}",
            $"created-utc|{createdUtc:O}"
        };

        return string.Join('\n', lines);
    }

    private static string SignTrustedServerAdmissionPayload(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TrustedServerAdmissionSigningKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private void ApplyConfiguredOwnerName()
    {
        if (string.IsNullOrWhiteSpace(_ownerFirstName) && string.IsNullOrWhiteSpace(_ownerLastName))
            return;

        _account = new AccountProfile
        {
            AccountId = _account.AccountId,
            Email = _account.Email,
            FirstName = !string.IsNullOrWhiteSpace(_ownerFirstName) ? _ownerFirstName : _account.FirstName,
            LastName = !string.IsNullOrWhiteSpace(_ownerLastName) ? _ownerLastName : _account.LastName,
            AccessKeyId = _account.AccessKeyId,
            SecretAccessKey = _account.SecretAccessKey
        };

        for (var i = 0; i < _people.Count; i++)
        {
            var person = _people[i];
            if (!person.AccountId.Equals(_account.AccountId, StringComparison.OrdinalIgnoreCase)) continue;

            _people[i] = new PersonRecord
            {
                PersonId = person.PersonId,
                AccountId = person.AccountId,
                LoopId = person.LoopId,
                RobotId = person.RobotId,
                DisplayName = $"{_account.FirstName} {_account.LastName}".Trim(),
                Alias = _account.FirstName,
                IsPrimary = person.IsPrimary,
                CreatedUtc = person.CreatedUtc,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
        }

        for (var i = 0; i < _loopMembers.Count; i++)
        {
            var member = _loopMembers[i];
            if (member.AccountId == null ||
                !member.AccountId.Equals(_account.AccountId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase))
                continue;

            _loopMembers[i] = new LoopMemberRecord
            {
                Id = member.Id,
                LoopId = member.LoopId,
                AccountId = member.AccountId,
                Email = member.Email,
                FirstName = _account.FirstName,
                LastName = _account.LastName,
                Gender = member.Gender,
                Birthday = member.Birthday,
                IsChild = member.IsChild,
                PhoneNumber = member.PhoneNumber,
                Status = member.Status,
                Type = member.Type,
                Nickname = member.Nickname,
                PhoneticName = member.PhoneticName,
                FaceEnrolled = member.FaceEnrolled,
                VoiceEnrolled = member.VoiceEnrolled,
                LegalGuardianId = member.LegalGuardianId,
                AgreementId = member.AgreementId,
                CreatedUtc = member.CreatedUtc,
            PortalEditedUtc = member.PortalEditedUtc
            };
        }
    }

    private void EnsureDefaultTopology()
    {
        if (_loops.Count == 0)
            _loops.Add(new LoopRecord
            {
                OwnerAccountId = _account.AccountId,
                RobotId = _robot.RobotId,
                RobotFriendlyId = _robot.DeviceId
            });

        EnsureOwnerLoopMember(_loops[0].LoopId);
        EnsureRobotLoopMember(_loops[0].LoopId, _robot.RobotId);

        if (_people.Count != 0)
        {
            EnsureDefaultCommuteProfile();
            return;
        }

        var loopId = _loops[0].LoopId;
        _people.Add(new PersonRecord
        {
            PersonId = "person-openjibo-owner",
            AccountId = _account.AccountId,
            LoopId = loopId,
            RobotId = _robot.RobotId,
            DisplayName = $"{_account.FirstName} {_account.LastName}",
            Alias = _account.FirstName,
            IsPrimary = true
        });
        _people.Add(new PersonRecord
        {
            PersonId = "person-openjibo-household-member",
            AccountId = _account.AccountId,
            LoopId = loopId,
            RobotId = _robot.RobotId,
            DisplayName = "OpenJibo Household Member",
            Alias = "Household Member",
            IsPrimary = false
        });

        EnsureDefaultCommuteProfile();
    }

    private static string CreateBootstrapDeviceId()
    {
        return $"openjibo-bootstrap-{Guid.NewGuid():N}";
    }

    private void EnsureOwnerLoopMember(string loopId)
    {
        if (_loopMembers.Any(member =>
                member.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                member.AccountId != null &&
                member.AccountId.Equals(_account.AccountId, StringComparison.OrdinalIgnoreCase) &&
                !member.Status.Equals("removed", StringComparison.OrdinalIgnoreCase)))
            return;

        _loopMembers.Add(new LoopMemberRecord
        {
            LoopId = loopId,
            AccountId = _account.AccountId,
            Email = _account.Email,
            FirstName = _account.FirstName,
            LastName = _account.LastName,
            Gender = "unknown",
            Birthday = 631152000000,
            Type = "owner",
            Status = "active"
        });
    }

    private void EnsureRobotLoopMember(string loopId, string robotId)
    {
        if (string.IsNullOrWhiteSpace(robotId)) return;

        if (_loopMembers.Any(member =>
                member.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
                member.AccountId != null &&
                member.AccountId.Equals(robotId, StringComparison.OrdinalIgnoreCase) &&
                !member.Status.Equals("removed", StringComparison.OrdinalIgnoreCase)))
            return;

        _loopMembers.Add(new LoopMemberRecord
        {
            LoopId = loopId,
            AccountId = robotId,
            FirstName = "Jibo",
            LastName = "Robot",
            Gender = "unknown",
            Type = "robot",
            Status = "active"
        });
    }

    private void EnsureDefaultCommuteProfile()
    {
        if (_commuteProfiles.Any(commute =>
                string.Equals(commute.LoopId, ResolveDefaultLoopId(), StringComparison.OrdinalIgnoreCase)))
            return;

        _commuteProfiles.Add(new CommuteProfileRecord
        {
            Id = $"commute-{ResolveDefaultLoopId()}",
            LoopId = ResolveDefaultLoopId(),
            IsEnabled = true,
            IsComplete = true,
            Mode = "driving",
            WorkHour = 8,
            WorkMinute = 30,
            OriginName = "home",
            DestinationName = "work",
            TypicalDurationMinutes = 25
        });
    }

    private LoopMemberRecord GetLoopMember(string loopId, string memberId)
    {
        return _loopMembers.First(m =>
            m.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase) &&
            m.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase));
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasDash = false;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasDash = false;
                continue;
            }

            if (lastWasDash) continue;

            builder.Append('-');
            lastWasDash = true;
        }

        return builder.ToString().Trim('-');
    }

    private static string ResolveDefaultLoopId(IReadOnlyList<LoopRecord> loops, AccountProfile account)
    {
        return loops.FirstOrDefault(loop =>
                   string.Equals(loop.OwnerAccountId, account.AccountId, StringComparison.OrdinalIgnoreCase))?.LoopId
               ?? loops.FirstOrDefault()?.LoopId
               ?? "openjibo-default-loop";
    }

    private string ResolveDefaultLoopId()
    {
        return ResolveDefaultLoopId(_loops, _account);
    }

    private static IDictionary<string, object?> BuildSessionMetadata(string accountId, string? deviceId, string loopId)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["accountId"] = accountId,
            ["loopId"] = loopId,
            ["deviceId"] = deviceId
        };
    }

    private static CloudSessionSnapshot MapSessionSnapshot(CloudSession session)
    {
        return new CloudSessionSnapshot
        {
            SessionId = session.SessionId,
            Kind = session.Kind,
            AccountId = session.AccountId,
            DeviceId = session.DeviceId,
            Token = session.Token,
            HostName = session.HostName,
            Path = session.Path,
            CreatedUtc = session.CreatedUtc,
            ExpiresUtc = session.ExpiresUtc,
            LastSeenUtc = session.LastSeenUtc,
            FollowUpExpiresUtc = session.FollowUpExpiresUtc,
            LastMessageType = session.LastMessageType,
            LastListenType = session.LastListenType,
            LastIntent = session.LastIntent,
            LastTranscript = session.LastTranscript,
            LastTransId = session.LastTransId,
            Metadata = session.Metadata
        };
    }

    private static DeviceRegistration CloneDeviceRegistration(DeviceRegistration device,
        string? friendlyName = null)
    {
        return new DeviceRegistration
        {
            DeviceId = device.DeviceId,
            RobotId = device.RobotId,
            FriendlyName = friendlyName ?? device.FriendlyName,
            FirmwareVersion = device.FirmwareVersion,
            ApplicationVersion = device.ApplicationVersion,
            IsActive = device.IsActive,
            CertificateThumbprint = device.CertificateThumbprint,
            IssuedIdentityId = device.IssuedIdentityId,
            BuildHash = device.BuildHash,
            ConfigHash = device.ConfigHash,
            VerifiedSerialNumber = device.VerifiedSerialNumber,
            SerialEvidenceSource = device.SerialEvidenceSource,
            SerialEvidenceVerifiedUtc = device.SerialEvidenceVerifiedUtc,
            RegistrationSource = RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId),
            IsHidden = device.IsHidden,
            ArchivedUtc = device.ArchivedUtc,
            HostMappings = new Dictionary<string, string>(device.HostMappings, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static CloudSession CloneSession(CloudSession session)
    {
        return new CloudSession
        {
            SessionId = session.SessionId,
            Kind = session.Kind,
            AccountId = session.AccountId,
            DeviceId = session.DeviceId,
            Token = session.Token,
            HostName = session.HostName,
            Path = session.Path,
            CreatedUtc = session.CreatedUtc,
            ExpiresUtc = session.ExpiresUtc,
            LastSeenUtc = session.LastSeenUtc,
            FollowUpExpiresUtc = session.FollowUpExpiresUtc,
            LastMessageType = session.LastMessageType,
            LastListenType = session.LastListenType,
            LastIntent = session.LastIntent,
            LastTranscript = session.LastTranscript,
            LastTransId = session.LastTransId,
            Metadata = new Dictionary<string, object?>(session.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private PersistentStateSnapshot CaptureSnapshot(DateTimeOffset now, bool includeBackups = true)
    {
        return new PersistentStateSnapshot
        {
            SchemaVersion = CurrentSchemaVersion,
            Revision = Interlocked.Read(ref _revision),
            LastLoadedUtc = _lastLoadedUtc,
            LastSavedUtc = now,
            Account = _account,
            Robot = _robot,
            RobotProfile = _robotProfile,
            Devices = _devices.Values.ToArray(),
            RobotCredentialBindings = _robotCredentialBindings.Values.ToArray(),
            Sessions = _sessions.DurableTokenValues.Select(MapSessionSnapshot).ToArray(),
            SymmetricKeys = _symmetricKeys.ToDictionary(entry => entry.Key, entry => entry.Value,
                StringComparer.OrdinalIgnoreCase),
            KeyRequests = _keyRequests.Values.ToArray(),
            Updates = _updates.ToArray(),
            Media = _media.ToArray(),
            Backups = includeBackups ? _backups.ToArray() : [],
            CommuteProfiles = _commuteProfiles.ToArray(),
            CalendarEvents = _calendarEvents.ToArray(),
            GreetingPresences = _greetingPresences.ToArray(),
            Loops = _loops.ToArray(),
            Holidays = _holidayOverrides.ToArray(),
            LoopMembers = _loopMembers.ToArray(),
            People = _people.ToArray(),
            Users = _users.ToArray(),
            RecognitionObservations = _recognitionObservations.ToArray(),
            RevokedIdentityGraphAnchors = _revokedIdentityGraphAnchors.ToArray(),
            TrustedServerAdmissions = _trustedServerAdmissions.ToArray(),
            TrustedServers = _trustedServers.ToArray(),
            UserDeviceLinks = _userDeviceLinks.ToArray()
        };
    }

    private void SavePersistedStateLocked(DateTimeOffset now)
    {
        var snapshot = CaptureSnapshot(now);
        _snapshotStore.Save(snapshot);
        _lastSavedUtc = now;
    }

    private bool ApplySnapshot(PersistentStateSnapshot snapshot)
    {
        _account = snapshot.Account ?? _account;
        _robot = snapshot.Robot is null ? _robot : NormalizePersistedDevice(snapshot.Robot);
        _robotProfile = snapshot.RobotProfile ?? _robotProfile;

        _devices.Clear();
        foreach (var device in snapshot.Devices ?? [])
        {
            var normalizedDevice = NormalizePersistedDevice(device);
            _devices[normalizedDevice.DeviceId] = normalizedDevice;
        }

        if (_devices.IsEmpty || !_devices.ContainsKey(_robot.DeviceId)) _devices[_robot.DeviceId] = _robot;

        _robotCredentialBindings.Clear();
        foreach (var binding in snapshot.RobotCredentialBindings ?? [])
            if (!string.IsNullOrWhiteSpace(binding.AccessKeyFingerprint) &&
                !string.IsNullOrWhiteSpace(binding.DeviceId) && _devices.ContainsKey(binding.DeviceId))
                _robotCredentialBindings.TryAdd(binding.AccessKeyFingerprint, binding);

        _sessions.Clear();
        foreach (var session in snapshot.Sessions ?? [])
            if (!string.IsNullOrWhiteSpace(session.Token))
                _sessions.RegisterDurableToken(session.Token, session.ToRecord());

        _symmetricKeys.Clear();
        foreach (var pair in snapshot.SymmetricKeys ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            _symmetricKeys[pair.Key] = pair.Value;

        _keyRequests.Clear();
        foreach (var keyRequest in snapshot.KeyRequests ?? []) _keyRequests[keyRequest.RequestId] = keyRequest;

        _updates.Clear();
        _updates.AddRange(snapshot.Updates ?? []);

        _media.Clear();
        _media.AddRange(snapshot.Media ?? []);

        _backups.Clear();
        var compactedBackups = false;
        foreach (var backup in snapshot.Backups ?? [])
        {
            var compactedSnapshotJson = CompactBackupSnapshotJson(backup.SnapshotJson);
            compactedBackups |= !string.Equals(compactedSnapshotJson, backup.SnapshotJson, StringComparison.Ordinal);
            _backups.Add(new BackupRecord
            {
                BackupId = backup.BackupId,
                CreatedUtc = backup.CreatedUtc,
                LoopId = backup.LoopId,
                Name = backup.Name,
                SnapshotJson = compactedSnapshotJson
            });
        }

        _commuteProfiles.Clear();
        _commuteProfiles.AddRange(snapshot.CommuteProfiles ?? []);

        _calendarEvents.Clear();
        _calendarEvents.AddRange(snapshot.CalendarEvents ?? []);

        _greetingPresences.Clear();
        _greetingPresences.AddRange(snapshot.GreetingPresences ?? []);

        _loops.Clear();
        _loops.AddRange(snapshot.Loops ?? []);
        // Older or partially onboarded snapshots may contain no loops. The robot
        // protocol requires ListLoops to return one concrete loop, so restore a
        // durable default tied to the current registered robot instead of sending
        // an empty array that makes stock Neo Hub fail acquisition.
        if (_loops.Count == 0)
        {
            _loops.Add(new LoopRecord
            {
                LoopId = "openjibo-default-loop",
                Name = "OpenJibo Default Loop",
                OwnerAccountId = _account.AccountId,
                RobotId = _robot.RobotId,
                RobotFriendlyId = _robot.DeviceId
            });
        }

        _holidayOverrides.Clear();
        _holidayOverrides.AddRange(snapshot.Holidays ?? []);

        _loopMembers.Clear();
        _loopMembers.AddRange(snapshot.LoopMembers ?? []);

        _people.Clear();
        _people.AddRange(snapshot.People ?? []);

        _users.Clear();
        _users.AddRange(snapshot.Users ?? []);

        _userDeviceLinks.Clear();
        foreach (var link in snapshot.UserDeviceLinks ?? [])
            if (!string.IsNullOrWhiteSpace(link.UserId) && !string.IsNullOrWhiteSpace(link.DeviceId) &&
                _users.Any(user => user.Id.Equals(link.UserId, StringComparison.OrdinalIgnoreCase)) &&
                _devices.ContainsKey(link.DeviceId))
                _userDeviceLinks.Add(link);

        _recognitionObservations.Clear();
        _recognitionObservations.AddRange(snapshot.RecognitionObservations ?? []);

        _revokedIdentityGraphAnchors.Clear();
        foreach (var anchor in snapshot.RevokedIdentityGraphAnchors ?? [])
            _revokedIdentityGraphAnchors.Add(anchor);

        _trustedServerAdmissions.Clear();
        foreach (var admission in snapshot.TrustedServerAdmissions ?? [])
            _trustedServerAdmissions.Add(admission);

        _trustedServers.Clear();
        foreach (var trustedServer in snapshot.TrustedServers ?? [])
            _trustedServers.Add(trustedServer);

        ApplyConfiguredOwnerName();
        EnsureDefaultTrustedServers();
        EnsureDefaultTopology();

        if (_robotProfile is null ||
            !string.Equals(_robotProfile.RobotId, _robot.RobotId, StringComparison.OrdinalIgnoreCase))
            _robotProfile = new RobotProfile
            {
                RobotId = _robot.RobotId,
                Payload = new Dictionary<string, object?>
                {
                    ["SSID"] = "my-ssid",
                    ["connectedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["platform"] = _robot.FirmwareVersion ?? "12.10.0",
                    ["serialNumber"] = _robot.DeviceId
                },
                UpdatedUtc = DateTimeOffset.UtcNow
            };

        var cleanupApplied = ClearArchivedSessionDeviceBindingsLocked();
        cleanupApplied |= RepairSupersededRobotPlaceholdersLocked();

        Interlocked.Exchange(ref _revision, snapshot.Revision);
        _lastLoadedUtc = snapshot.LastLoadedUtc ?? DateTimeOffset.UtcNow;
        _lastSavedUtc = snapshot.LastSavedUtc;
        return cleanupApplied || compactedBackups;
    }

    private static string? CompactBackupSnapshotJson(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return snapshotJson;

        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(nameof(PersistentStateSnapshot.Backups), out var backups) ||
                backups.ValueKind != JsonValueKind.Array || backups.GetArrayLength() == 0)
                return snapshotJson;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.NameEquals(nameof(PersistentStateSnapshot.Backups)))
                    {
                        writer.WriteStartArray();
                        writer.WriteEndArray();
                    }
                    else
                    {
                        property.Value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
        catch (JsonException)
        {
            // Preserve a malformed legacy payload. RestoreBackup already treats it
            // as unusable, and startup should not fail because an old backup is bad.
            return snapshotJson;
        }
    }

    private bool ClearArchivedSessionDeviceBindingsLocked()
    {
        var changed = false;
        foreach (var session in _sessions.Values)
        {
            if (!session.Metadata.TryGetValue("registeredDeviceId", out var value) ||
                string.IsNullOrWhiteSpace(value?.ToString()))
                continue;

            var device = FindDeviceByFriendlyId(value.ToString()!);
            if (device is null || !device.IsHidden) continue;

            AppendSessionBindingAudit(session, "cleared-archived-link", device.DeviceId, null, "startup-guard");
            session.Metadata.Remove("registeredDeviceId");
            session.Metadata.Remove("registeredRobotId");
            session.Metadata.Remove("identitySuggestionDeviceId");
            changed = true;
        }

        return changed;
    }

    private static void AppendSessionBindingAudit(CloudSession session, string action, string? previousDeviceId,
        string? deviceId, string source)
    {
        const string auditKey = "sessionBindingAudit";
        var entries = new List<SessionBindingAuditEntry>();
        if (session.Metadata.TryGetValue(auditKey, out var existing) && existing is not null)
        {
            try
            {
                entries.AddRange(JsonSerializer.Deserialize<List<SessionBindingAuditEntry>>(existing.ToString() ?? "[]",
                    PersistenceJsonOptions) ?? []);
            }
            catch (JsonException)
            {
                // A malformed legacy note is not a reason to block a deliberate admin action.
            }
        }

        entries.Add(new SessionBindingAuditEntry(action, previousDeviceId, deviceId, source, DateTimeOffset.UtcNow));
        session.Metadata[auditKey] = JsonSerializer.Serialize(entries.TakeLast(10), PersistenceJsonOptions);
    }

    private bool RepairSupersededRobotPlaceholdersLocked()
    {
        var devices = _devices.Values.ToArray();
        if (devices.Length < 2)
            return false;

        var sessions = _sessions.Values.ToArray();
        var disjointSet = new DisjointSet(devices.Length);

        for (var sessionIndex = 0; sessionIndex < sessions.Length; sessionIndex++)
        {
            var matchingDeviceIndices = new List<int>();
            for (var deviceIndex = 0; deviceIndex < devices.Length; deviceIndex++)
            {
                if (SessionMatchesDevice(sessions[sessionIndex], devices[deviceIndex]))
                    matchingDeviceIndices.Add(deviceIndex);
            }

            if (matchingDeviceIndices.Count < 2)
                continue;

            var firstMatch = matchingDeviceIndices[0];
            for (var i = 1; i < matchingDeviceIndices.Count; i++)
                disjointSet.Union(firstMatch, matchingDeviceIndices[i]);
        }

        var changed = false;
        foreach (var group in Enumerable.Range(0, devices.Length).GroupBy(disjointSet.Find))
        {
            var groupDevices = group.Select(index => devices[index]).ToArray();
            if (groupDevices.Length < 2)
                continue;

            var hasVerifiedDevice = groupDevices.Any(device => !IsPlaceholderRobotRecord(device));
            if (!hasVerifiedDevice)
                continue;

            foreach (var device in groupDevices.Where(device =>
                         IsPlaceholderRobotRecord(device) && !device.IsHidden && device.ArchivedUtc is null))
            {
                var updated = new DeviceRegistration
                {
                    DeviceId = device.DeviceId,
                    RobotId = device.RobotId,
                    FriendlyName = device.FriendlyName,
                    FirmwareVersion = device.FirmwareVersion,
                    ApplicationVersion = device.ApplicationVersion,
                    IsActive = device.IsActive,
                    CertificateThumbprint = device.CertificateThumbprint,
                    IssuedIdentityId = device.IssuedIdentityId,
                    BuildHash = device.BuildHash,
                    ConfigHash = device.ConfigHash,
                    VerifiedSerialNumber = device.VerifiedSerialNumber,
                    SerialEvidenceSource = device.SerialEvidenceSource,
                    SerialEvidenceVerifiedUtc = device.SerialEvidenceVerifiedUtc,
                    RegistrationSource = RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId),
                    IsHidden = true,
                    ArchivedUtc = device.ArchivedUtc ?? DateTimeOffset.UtcNow,
                    HostMappings = new Dictionary<string, string>(device.HostMappings, StringComparer.OrdinalIgnoreCase)
                };
                _devices[updated.DeviceId] = updated;
                changed = true;
            }
        }

        return changed;
    }

    private static bool SessionMatchesDevice(CloudSession session, DeviceRegistration device)
    {
        // Traffic-derived IDs are evidence, not ownership. Only a portal-admin binding
        // can connect a live session to an inventory record.
        return IdentityMatches(ReadSessionMetadata(session, "registeredDeviceId"), device.DeviceId) ||
               IdentityMatches(ReadSessionMetadata(session, "registeredRobotId"), device.RobotId);
    }

    private static IEnumerable<string> GetSessionIdentityValues(CloudSession session)
    {
        var values = new[]
        {
            session.DeviceId,
            ReadSessionMetadata(session, "registeredDeviceId"),
            ReadSessionMetadata(session, "registeredRobotId"),
            ReadSessionMetadata(session, "robotID"),
            ReadSessionMetadata(session, "robotId"),
            ReadSessionMetadata(session, "robotFriendlyId"),
            ReadSessionMetadata(session, "friendlyId"),
            ReadSessionMetadata(session, "deviceId")
        };

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                yield return value.Trim();
        }
    }

    private static string? ReadSessionMetadata(CloudSession session, string key) =>
        session.Metadata.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IsPlaceholderRobotRecord(DeviceRegistration device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.RobotId))
            return false;

        var normalizedSource = RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId);
        return string.Equals(device.FriendlyName, "OpenJibo Registered Robot", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.RobotId.Trim(), $"robot-{device.DeviceId.Trim()}",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedSource, RobotRegistrationSources.Unknown, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parents;
        private readonly int[] _ranks;

        public DisjointSet(int size)
        {
            _parents = Enumerable.Range(0, size).ToArray();
            _ranks = new int[size];
        }

        public int Find(int item)
        {
            if (_parents[item] != item)
                _parents[item] = Find(_parents[item]);

            return _parents[item];
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
                return;

            if (_ranks[leftRoot] < _ranks[rightRoot])
            {
                _parents[leftRoot] = rightRoot;
                return;
            }

            if (_ranks[leftRoot] > _ranks[rightRoot])
            {
                _parents[rightRoot] = leftRoot;
                return;
            }

            _parents[rightRoot] = leftRoot;
            _ranks[leftRoot]++;
        }
    }

    private static DeviceRegistration NormalizePersistedDevice(DeviceRegistration device)
    {
        var source = RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId);
        var inferredSynthetic = device.RegistrationSource == RobotRegistrationSources.Unknown &&
                               RobotRegistrationSources.IsSynthetic(source);
        var hidden = device.IsHidden || inferredSynthetic;

        return new DeviceRegistration
        {
            DeviceId = device.DeviceId,
            RobotId = device.RobotId,
            FriendlyName = device.FriendlyName,
            FirmwareVersion = device.FirmwareVersion,
            ApplicationVersion = device.ApplicationVersion,
            IsActive = device.IsActive,
            CertificateThumbprint = device.CertificateThumbprint,
            IssuedIdentityId = device.IssuedIdentityId,
            BuildHash = device.BuildHash,
            ConfigHash = device.ConfigHash,
            VerifiedSerialNumber = device.VerifiedSerialNumber,
            SerialEvidenceSource = device.SerialEvidenceSource,
            SerialEvidenceVerifiedUtc = device.SerialEvidenceVerifiedUtc,
            RegistrationSource = source,
            IsHidden = hidden,
            ArchivedUtc = hidden ? device.ArchivedUtc ?? DateTimeOffset.UtcNow : null,
            HostMappings = new Dictionary<string, string>(device.HostMappings, StringComparer.OrdinalIgnoreCase)
        };
    }

    private sealed record SessionBindingAuditEntry(string Action, string? PreviousDeviceId, string? DeviceId,
        string Source, DateTimeOffset OccurredUtc);

    private sealed class PersistentStateSnapshot
    {
        public string SchemaVersion { get; init; } = CurrentSchemaVersion;
        public long Revision { get; init; }
        public DateTimeOffset? LastLoadedUtc { get; init; }
        public DateTimeOffset? LastSavedUtc { get; init; }
        public AccountProfile? Account { get; init; }
        public DeviceRegistration? Robot { get; init; }
        public RobotProfile? RobotProfile { get; init; }
        public DeviceRegistration[]? Devices { get; init; }
        public RobotCredentialBinding[]? RobotCredentialBindings { get; init; }
        public CloudSessionSnapshot[]? Sessions { get; init; }
        public Dictionary<string, string>? SymmetricKeys { get; init; }
        public KeyRequestRecord[]? KeyRequests { get; init; }
        public UpdateManifest[]? Updates { get; init; }
        public MediaRecord[]? Media { get; init; }
        public BackupRecord[]? Backups { get; init; }
        public CommuteProfileRecord[]? CommuteProfiles { get; init; }
        public CalendarEventRecord[]? CalendarEvents { get; init; }
        public GreetingPresenceRecord[]? GreetingPresences { get; init; }
        public LoopRecord[]? Loops { get; init; }
        public HolidayRecord[]? Holidays { get; init; }
        public LoopMemberRecord[]? LoopMembers { get; init; }
        public PersonRecord[]? People { get; init; }
        public UserRecord[]? Users { get; init; }
        public RecognitionObservationRecord[]? RecognitionObservations { get; init; }
        public string[]? RevokedIdentityGraphAnchors { get; init; }
        public TrustedServerAdmissionRecord[]? TrustedServerAdmissions { get; init; }
        public TrustedServerRecord[]? TrustedServers { get; init; }
        public UserDeviceLink[]? UserDeviceLinks { get; init; }
    }

    private sealed class CloudSessionSnapshot
    {
        public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
        public string Kind { get; init; } = "http";
        public string? AccountId { get; init; }
        public string? DeviceId { get; init; }
        public string? Token { get; init; }
        public string? HostName { get; init; }
        public string? Path { get; init; }
        public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ExpiresUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FollowUpExpiresUtc { get; init; }
        public string? LastMessageType { get; init; }
        public string? LastListenType { get; init; }
        public string? LastIntent { get; init; }
        public string? LastTranscript { get; init; }
        public string? LastTransId { get; init; }
        public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();

        public CloudSession ToRecord()
        {
            return new CloudSession
            {
                SessionId = SessionId,
                Kind = Kind,
                AccountId = AccountId,
                DeviceId = DeviceId,
                Token = Token,
                HostName = HostName,
                Path = Path,
                CreatedUtc = CreatedUtc,
                ExpiresUtc = ExpiresUtc,
                LastSeenUtc = LastSeenUtc,
                FollowUpExpiresUtc = FollowUpExpiresUtc,
                LastMessageType = LastMessageType,
                LastListenType = LastListenType,
                LastIntent = LastIntent,
                LastTranscript = LastTranscript,
                LastTransId = LastTransId,
                Metadata = new Dictionary<string, object?>(Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
