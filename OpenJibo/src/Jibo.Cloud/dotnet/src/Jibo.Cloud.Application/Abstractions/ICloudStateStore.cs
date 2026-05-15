using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Abstractions;

public interface ICloudStateStore
{
    AccountProfile GetAccount();
    DeviceRegistration GetRobot();
    RobotProfile GetRobotProfile();
    DeviceRegistration GetOrCreateDevice(string deviceId, string? firmwareVersion, string? applicationVersion);
    string IssueHubToken();
    string IssueRobotToken(string deviceId);
    CloudSession OpenSession(string kind, string? deviceId, string? token, string? hostName, string? path);
    CloudSession? FindSessionByToken(string token);
    IReadOnlyList<LoopRecord> GetLoops();
    IReadOnlyList<PersonRecord> GetPeople();
    IReadOnlyList<UpdateManifest> ListUpdates(string? subsystem = null, string? filter = null);
    UpdateManifest? GetUpdateFrom(string? subsystem, string? fromVersion, string? filter);
    UpdateManifest CreateUpdate(string? fromVersion, string? toVersion, string? changes, string? shaHash, long? length, string? subsystem, string? filter, IDictionary<string, object?>? dependencies);
    UpdateManifest RemoveUpdate(string? updateId);
    IReadOnlyList<MediaRecord> ListMedia(IReadOnlyList<string>? loopIds = null, long? after = null, long? before = null);
    IReadOnlyList<MediaRecord> GetMedia(IReadOnlyList<string> paths);
    IReadOnlyList<MediaRecord> RemoveMedia(IReadOnlyList<string> paths);
    MediaRecord CreateMedia(string loopId, string path, string type, string reference, bool isEncrypted, IDictionary<string, object?>? meta);
    IReadOnlyList<BackupRecord> GetBackups();
    bool ShouldCreateSymmetricKey(string loopId);
    string GetOrCreateSymmetricKey(string loopId);
    KeyRequestRecord CreateKeyRequest(string loopId, string publicKey);
    KeyRequestRecord GetKeyRequest(string loopId, string? requestId, string? publicKey);
    IReadOnlyList<KeyRequestRecord> GetIncomingKeyRequests();
    IReadOnlyList<KeyRequestRecord> GetBinaryRequests();
    IReadOnlyList<object> GetHolidays();
    void UpdateRobot(DeviceRegistration registration);
}
