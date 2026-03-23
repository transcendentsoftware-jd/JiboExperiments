namespace Jibo.Runtime.Abstractions;

public sealed class ResponsePlan
{
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = string.Empty;
    public ResponseStatus Status { get; init; } = ResponseStatus.Succeeded;

    public string? IntentName { get; init; }
    public string? Topic { get; init; }
    public IList<PlanAction> Actions { get; init; } = new List<PlanAction>();

    public FollowUpPolicy FollowUp { get; init; } = FollowUpPolicy.None;
    public IDictionary<string, object?> ContextUpdates { get; init; } = new Dictionary<string, object?>();

    public string? DebugRoute { get; init; }
    public IDictionary<string, object?> Diagnostics { get; init; } = new Dictionary<string, object?>();
}