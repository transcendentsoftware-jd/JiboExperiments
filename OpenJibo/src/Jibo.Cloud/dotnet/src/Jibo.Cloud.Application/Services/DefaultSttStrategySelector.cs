using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class DefaultSttStrategySelector(IEnumerable<ISttStrategy> strategies) : ISttStrategySelector
{
    private readonly IReadOnlyList<ISttStrategy> _strategies = strategies.ToArray();

    public Task<ISttStrategy> SelectAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var strategy = _strategies.FirstOrDefault(candidate => candidate.CanHandle(turn));
        return strategy is null
            ? throw new InvalidOperationException("No STT strategy can handle the current turn.")
            : Task.FromResult(strategy);
    }
}
