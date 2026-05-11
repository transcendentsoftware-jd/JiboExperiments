namespace Jibo.Cloud.Infrastructure.News;

public sealed class NewsApiOptions
{
    public string BaseUrl { get; set; } = "https://newsapi.org";

    public string? ApiKey { get; set; }

    public string Country { get; set; } = "us";

    public string Language { get; set; } = "en";

    public string FallbackQuery { get; set; } = "robotics";

    public string[] DefaultCategories { get; set; } =
    [
        "general",
        "technology",
        "sports",
        "business"
    ];

    public int CacheTtlSeconds { get; set; } = 300;

    public int FailureCacheTtlSeconds { get; set; } = 45;
}
