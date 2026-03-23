namespace Jibo.Runtime.Abstractions;

public sealed class BrainDecision
{
    public BrainRoute Route { get; init; }
    public string IntentName { get; init; } = string.Empty;
    public float Confidence { get; init; }

    public string? CapabilityName { get; init; }
    public string? NativeSkillName { get; init; }

    public IDictionary<string, object?> Slots { get; init; } = new Dictionary<string, object?>();
    public IDictionary<string, object?> ContextUpdates { get; init; } = new Dictionary<string, object?>();
}