using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboExperienceContentCache(IJiboExperienceContentRepository repository)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JiboExperienceCatalog? _catalog;

    public async Task<JiboExperienceCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is not null)
        {
            return _catalog;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _catalog ??= await repository.GetCatalogAsync(cancellationToken);
            return _catalog;
        }
        finally
        {
            _gate.Release();
        }
    }
}
