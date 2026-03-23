namespace Jibo.Runtime.Abstractions;

public interface IConversationBroker
{
    Task<ResponsePlan> HandleTurnAsync(TurnContext turn, CancellationToken cancellationToken = default);
}