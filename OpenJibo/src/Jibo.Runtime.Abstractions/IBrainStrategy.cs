namespace Jibo.Runtime.Abstractions;

public interface IBrainStrategy
{
    string Name { get; }
    bool CanHandle(TurnContext turn, ConversationSession session);
    Task<BrainDecision> DecideAsync(
        TurnContext turn,
        ConversationSession session,
        CancellationToken cancellationToken = default);
}