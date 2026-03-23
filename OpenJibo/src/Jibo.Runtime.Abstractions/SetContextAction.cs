namespace Jibo.Runtime.Abstractions;

public sealed class SetContextAction : PlanAction
{
    public override PlanActionType Type => PlanActionType.SetContext;
    public IDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();
}