namespace Jibo.Cloud.Application.Abstractions;

public interface IJiboExperienceContentRepository
{
    Task<JiboExperienceCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);
}

public sealed class JiboConditionedReply
{
    public string Condition { get; init; } = string.Empty;
    public string Reply { get; init; } = string.Empty;
}

public sealed class JiboExperienceCatalog
{
    public IReadOnlyList<string> Jokes { get; init; } = [];
    public IReadOnlyList<string> DanceAnimations { get; init; } = [];
    public IReadOnlyList<string> GreetingReplies { get; init; } = [];
    public IReadOnlyList<string> HowAreYouReplies { get; init; } = [];
    public IReadOnlyList<JiboConditionedReply> EmotionReplies { get; init; } = [];
    public IReadOnlyList<string> PersonalityReplies { get; init; } = [];
    public IReadOnlyList<string> PizzaReplies { get; init; } = [];
    public IReadOnlyList<string> SurpriseReplies { get; init; } = [];
    public IReadOnlyList<string> PersonalReportReplies { get; init; } = [];
    public IReadOnlyList<string> PersonalReportKickOffReplies { get; init; } = [];
    public IReadOnlyList<string> PersonalReportOutroReplies { get; init; } = [];
    public IReadOnlyList<string> ReportSkillTemplates { get; init; } = [];
    public IReadOnlyList<string> WeatherIntroReplies { get; init; } = [];
    public IReadOnlyList<string> WeatherTomorrowIntroReplies { get; init; } = [];
    public IReadOnlyList<string> WeatherTodayHighLowReplies { get; init; } = [];
    public IReadOnlyList<string> WeatherTomorrowHighLowReplies { get; init; } = [];
    public IReadOnlyList<string> WeatherServiceDownReplies { get; init; } = [];
    public IReadOnlyList<string> WeatherReplies { get; init; } = [];
    public IReadOnlyList<string> CalendarReplies { get; init; } = [];
    public IReadOnlyList<string> CommuteReplies { get; init; } = [];
    public IReadOnlyList<string> NewsReplies { get; init; } = [];
    public IReadOnlyList<string> NewsBriefings { get; init; } = [];
    public IReadOnlyList<string> GenericFallbackReplies { get; init; } = [];
    public IReadOnlyList<string> DanceReplies { get; init; } = [];
    public IReadOnlyList<string> DanceQuestionReplies { get; init; } = [];
}
