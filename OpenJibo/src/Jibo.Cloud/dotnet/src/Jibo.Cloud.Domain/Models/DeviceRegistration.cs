namespace Jibo.Cloud.Domain.Models;

public sealed class DeviceRegistration
{
    public string DeviceId { get; init; } = "my-robot-serial-number";
    public string RobotId { get; init; } = "my-robot-name";
    public string FriendlyName { get; init; } = "OpenJibo Dev Robot";
    public string? FirmwareVersion { get; init; }
    public string? ApplicationVersion { get; init; }
    public bool IsActive { get; init; } = true;
    public IDictionary<string, string> HostMappings { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
