namespace Jibo.Cloud.Domain.Models;

public sealed class PersonRecord
{
    public string PersonId { get; init; } = "person-openjibo-owner";
    public string AccountId { get; init; } = "usr_openjibo_owner";
    public string LoopId { get; init; } = "openjibo-default-loop";
    public string RobotId { get; init; } = "my-robot-name";
    public string DisplayName { get; init; } = "Jibo Owner";
    public string? Alias { get; init; }
    public bool IsPrimary { get; init; } = true;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
