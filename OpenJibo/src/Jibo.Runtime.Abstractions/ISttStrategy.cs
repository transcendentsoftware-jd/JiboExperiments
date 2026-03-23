namespace Jibo.Runtime.Abstractions;

public interface ISttStrategy
{
    string Name { get; }
    bool CanHandle(TurnContext turn);
    Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default);
}