namespace Jibo.Cloud.Domain.Models;

public sealed class RobotProfile
{
    public string RobotId { get; init; } = "my-robot-name";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public IDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
    public IDictionary<string, object?> CalibrationPayload { get; init; } = new Dictionary<string, object?>();
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
