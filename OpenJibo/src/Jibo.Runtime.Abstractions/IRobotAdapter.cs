namespace Jibo.Runtime.Abstractions;

public interface IRobotAdapter
{
    Task PublishPlanAsync(ResponsePlan plan, CancellationToken cancellationToken = default);
}