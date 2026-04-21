namespace Jibo.Cloud.Application.Abstractions;

public interface IJiboExperienceContentRepository
{
    Task<JiboExperienceCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);
}

public sealed class JiboExperienceCatalog
{
    public IReadOnlyList<string> Jokes { get; init; } = [];
    public IReadOnlyList<string> DanceAnimations { get; init; } = [];
    public IReadOnlyList<string> GreetingReplies { get; init; } = [];
    public IReadOnlyList<string> HowAreYouReplies { get; init; } = [];
    public IReadOnlyList<string> SurpriseReplies { get; init; } = [];
    public IReadOnlyList<string> PersonalReportReplies { get; init; } = [];
    public IReadOnlyList<string> WeatherReplies { get; init; } = [];
    public IReadOnlyList<string> CalendarReplies { get; init; } = [];
    public IReadOnlyList<string> CommuteReplies { get; init; } = [];
    public IReadOnlyList<string> NewsReplies { get; init; } = [];
    public IReadOnlyList<string> NewsBriefings { get; init; } = [];
    public IReadOnlyList<string> GenericFallbackReplies { get; init; } = [];
}
