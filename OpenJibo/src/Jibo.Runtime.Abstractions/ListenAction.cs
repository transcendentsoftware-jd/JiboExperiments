namespace Jibo.Runtime.Abstractions;

public sealed class ListenAction : PlanAction
{
    public override PlanActionType Type => PlanActionType.Listen;
    public TimeSpan Timeout { get; init; }
    public string? Mode { get; init; } // follow-up, open-mic, command
}