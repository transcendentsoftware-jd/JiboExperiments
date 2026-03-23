namespace Jibo.Runtime.Abstractions;

public sealed class AnimateAction : PlanAction
{
    public override PlanActionType Type => PlanActionType.Animate;
    public string AnimationId { get; init; } = string.Empty;
    public IDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
}