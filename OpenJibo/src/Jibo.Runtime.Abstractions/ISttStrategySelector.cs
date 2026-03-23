namespace Jibo.Runtime.Abstractions;

public interface ISttStrategySelector
{
    Task<ISttStrategy> SelectAsync(TurnContext turn, CancellationToken cancellationToken = default);
}