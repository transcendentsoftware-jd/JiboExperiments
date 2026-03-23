namespace Jibo.Runtime.Abstractions;

public interface IRobotEventSource
{
    IAsyncEnumerable<RobotEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
}