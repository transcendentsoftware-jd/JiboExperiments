namespace Jibo.Cloud.Domain.Models;

public sealed class LoopRecord
{
    public string LoopId { get; init; } = "openjibo-default-loop";
    public string Name { get; init; } = "OpenJibo Default Loop";
    public string OwnerAccountId { get; init; } = "usr_openjibo_owner";
    public string RobotId { get; init; } = "my-robot-name";
    public string RobotFriendlyId { get; init; } = "my-robot-serial-number";
    public bool IsSuspended { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
