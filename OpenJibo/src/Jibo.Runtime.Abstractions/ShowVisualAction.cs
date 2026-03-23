namespace Jibo.Runtime.Abstractions;

public sealed class ShowVisualAction : PlanAction
{
    public override PlanActionType Type => PlanActionType.ShowVisual;
    public string VisualId { get; init; } = string.Empty;
    public IDictionary<string, object?> Parameters { get; init; } = new Dictionary<string, object?>();
}