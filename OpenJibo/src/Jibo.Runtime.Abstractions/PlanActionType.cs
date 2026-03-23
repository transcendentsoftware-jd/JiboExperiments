namespace Jibo.Runtime.Abstractions;

public enum PlanActionType
{
    Speak = 0,
    Listen = 1,
    ShowVisual = 2,
    Animate = 3,
    InvokeNativeSkill = 4,
    SetContext = 5,
    EmitEvent = 6
}