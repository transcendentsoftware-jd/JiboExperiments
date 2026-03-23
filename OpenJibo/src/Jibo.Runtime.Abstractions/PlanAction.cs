namespace Jibo.Runtime.Abstractions;

public abstract class PlanAction
{
    public abstract PlanActionType Type { get; }
    public int Sequence { get; init; }
}