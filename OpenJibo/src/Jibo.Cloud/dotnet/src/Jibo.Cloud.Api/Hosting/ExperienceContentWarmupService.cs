using System.Diagnostics;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Api.Hosting;

internal sealed class ExperienceContentWarmupService(
    JiboExperienceContentCache cache,
    ILogger<ExperienceContentWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        logger.LogInformation("Loading bundled experience content before accepting requests");
        await cache.GetCatalogAsync(cancellationToken);
        logger.LogInformation("Bundled experience content loaded in {ElapsedMs} ms", timer.ElapsedMilliseconds);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
