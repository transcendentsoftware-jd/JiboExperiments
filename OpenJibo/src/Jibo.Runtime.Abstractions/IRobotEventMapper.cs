namespace Jibo.Runtime.Abstractions;

public interface IRobotEventMapper
{
    Task<TurnContext> MapToTurnContextAsync(RobotEvent robotEvent, CancellationToken cancellationToken = default);
}