namespace Jibo.Runtime.Abstractions;

public interface IResponsePlanner
{
    Task<ResponsePlan> BuildPlanAsync(
        TurnContext turn,
        ConversationSession session,
        BrainDecision decision,
        CancellationToken cancellationToken = default);
}