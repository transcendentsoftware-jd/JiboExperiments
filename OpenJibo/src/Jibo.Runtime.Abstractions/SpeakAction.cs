namespace Jibo.Runtime.Abstractions;

public sealed class SpeakAction : PlanAction
{
    public override PlanActionType Type => PlanActionType.Speak;
    public string Text { get; init; } = string.Empty;
    public string? Voice { get; init; }
    public string? Style { get; init; }
    public bool CanBeInterrupted { get; init; } = true;
}