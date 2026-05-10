namespace Jibo.Cloud.Application.Abstractions;

public interface INewsBriefingProvider
{
    Task<NewsBriefingSnapshot?> GetBriefingAsync(
        NewsBriefingRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record NewsBriefingRequest(
    IReadOnlyList<string> PreferredCategories,
    int MaxHeadlines = 3);

public sealed record NewsHeadline(
    string Title,
    string? Summary = null,
    string? Category = null,
    string? SourceName = null,
    string? Url = null);

public sealed record NewsBriefingSnapshot(
    IReadOnlyList<NewsHeadline> Headlines,
    string? SourceName = null);
