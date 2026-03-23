namespace Jibo.Runtime.Abstractions;

public sealed class InvokeNativeSkillAction : PlanAction
{
    public override PlanActionType Type => PlanActionType.InvokeNativeSkill;
    public string SkillName { get; init; } = string.Empty;
    public IDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
}