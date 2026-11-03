using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Api;

public sealed class ExperienceContentWarmupServiceTests
{
    [Fact]
    public async Task StartupWaitsForContentAndTheFirstTurnReusesTheLoadedCatalog()
    {
        var ready = new TaskCompletionSource<JiboExperienceCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Repository(_ => ready.Task);
        var cache = new JiboExperienceContentCache(repository);
        var service = new ExperienceContentWarmupService(cache, NullLogger<ExperienceContentWarmupService>.Instance);
        var startup = service.StartAsync(CancellationToken.None);
        Assert.False(startup.IsCompleted);
        var catalog = new JiboExperienceCatalog();
        ready.SetResult(catalog);
        await startup;
        Assert.Same(catalog, await cache.GetCatalogAsync());
        Assert.Equal(1, repository.Calls);
    }

    [Fact]
    public async Task CancelledStartupDoesNotBeginLoadingContent()
    {
        var repository = new Repository(_ => Task.FromResult(new JiboExperienceCatalog()));
        var service = new ExperienceContentWarmupService(new JiboExperienceContentCache(repository),
            NullLogger<ExperienceContentWarmupService>.Instance);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cancelled.Token));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task LoadingFailureIsAStartupFailure()
    {
        var repository = new Repository(_ => Task.FromException<JiboExperienceCatalog>(new InvalidOperationException("fixture")));
        var service = new ExperienceContentWarmupService(new JiboExperienceContentCache(repository),
            NullLogger<ExperienceContentWarmupService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(1, repository.Calls);
    }

    private sealed class Repository(Func<CancellationToken,Task<JiboExperienceCatalog>> load) : IJiboExperienceContentRepository
    {
        public int Calls { get; private set; }
        public Task<JiboExperienceCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return load(cancellationToken);
        }
    }
}
