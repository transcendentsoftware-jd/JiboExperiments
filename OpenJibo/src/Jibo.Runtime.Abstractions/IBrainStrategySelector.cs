namespace Jibo.Runtime.Abstractions;

public interface IBrainStrategySelector
{
    Task<IBrainStrategy> SelectAsync(
        TurnContext turn,
        ConversationSession session,
        CancellationToken cancellationToken = default);
}