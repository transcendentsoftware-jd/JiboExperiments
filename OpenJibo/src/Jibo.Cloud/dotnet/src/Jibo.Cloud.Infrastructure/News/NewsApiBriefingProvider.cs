using System.Collections.Concurrent;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.News;

public sealed class NewsApiBriefingProvider(
    HttpClient httpClient,
    NewsApiOptions options,
    ILogger<NewsApiBriefingProvider> logger)
    : INewsBriefingProvider
{
    private readonly ConcurrentDictionary<string, CacheEntry<NewsBriefingSnapshot?>> briefingCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<NewsBriefingSnapshot?> GetBriefingAsync(
        NewsBriefingRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            logger.LogWarning("NewsAPI provider disabled because no API key is configured.");
            return null;
        }

        string? cacheKey = null;
        try
        {
            var categories = ResolveCategories(request.PreferredCategories).ToArray();
            if (categories.Length == 0)
            {
                categories = ["general"];
            }

            var requestedHeadlineCount = Math.Clamp(request.MaxHeadlines, 1, MaxHeadlines);
            cacheKey = BuildCacheKey(categories, requestedHeadlineCount);
            logger.LogInformation(
                "NewsAPI request started. Categories={Categories} RequestedHeadlineCount={RequestedHeadlineCount} CacheKey={CacheKey}",
                string.Join(",", categories),
                requestedHeadlineCount,
                cacheKey);
            if (TryGetCachedValue(briefingCache, cacheKey, out var cachedBriefing))
            {
                logger.LogInformation(
                    "NewsAPI cache hit. CacheKey={CacheKey} HasSnapshot={HasSnapshot} HeadlineCount={HeadlineCount}",
                    cacheKey,
                    cachedBriefing is not null,
                    cachedBriefing?.Headlines.Count ?? 0);
                return cachedBriefing;
            }

            var headlines = new List<NewsHeadline>(requestedHeadlineCount);
            var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var category in categories)
            {
                var uri = BuildTopHeadlinesUri(category, requestedHeadlineCount);
                using var response = await httpClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "NewsAPI request failed for category {Category}. StatusCode={StatusCode} Reason={ReasonPhrase}",
                        category,
                        (int)response.StatusCode,
                        response.ReasonPhrase);
                    continue;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("status", out var statusNode) &&
                    statusNode.ValueKind == JsonValueKind.String &&
                    !string.Equals(statusNode.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "NewsAPI returned non-ok status for category {Category}. Status={Status} Code={Code} Message={Message}",
                        category,
                        statusNode.GetString(),
                        ReadString(document.RootElement, "code") ?? string.Empty,
                        ReadString(document.RootElement, "message") ?? string.Empty);
                }

                if (!document.RootElement.TryGetProperty("articles", out var articles) ||
                    articles.ValueKind != JsonValueKind.Array)
                {
                    logger.LogWarning("NewsAPI response missing articles array for category {Category}.", category);
                    continue;
                }

                foreach (var article in articles.EnumerateArray())
                {
                    var title = NormalizeHeadlineTitle(ReadString(article, "title"));
                    if (string.IsNullOrWhiteSpace(title) || !seenTitles.Add(title))
                    {
                        continue;
                    }

                    var summary = ReadString(article, "description");
                    var source = article.TryGetProperty("source", out var sourceNode) &&
                                 sourceNode.ValueKind == JsonValueKind.Object
                        ? ReadString(sourceNode, "name")
                        : null;
                    var url = ReadString(article, "url");
                    headlines.Add(new NewsHeadline(title, summary, category, source, url));

                    if (headlines.Count >= requestedHeadlineCount)
                    {
                        var snapshot = new NewsBriefingSnapshot(headlines, "NewsAPI");
                        SetCachedValue(briefingCache, cacheKey, snapshot, options.CacheTtlSeconds);
                        logger.LogInformation(
                            "NewsAPI request succeeded. Categories={Categories} HeadlineCount={HeadlineCount}",
                            string.Join(",", categories),
                            headlines.Count);
                        return snapshot;
                    }
                }
            }

            if (headlines.Count == 0)
            {
                SetCachedValue(briefingCache, cacheKey, null, options.FailureCacheTtlSeconds);
                logger.LogWarning(
                    "NewsAPI returned no usable headlines. Categories={Categories} RequestedHeadlineCount={RequestedHeadlineCount}",
                    string.Join(",", categories),
                    requestedHeadlineCount);
                return null;
            }

            var populatedSnapshot = new NewsBriefingSnapshot(headlines, "NewsAPI");
            SetCachedValue(briefingCache, cacheKey, populatedSnapshot, options.CacheTtlSeconds);
            logger.LogInformation(
                "NewsAPI request partially filled headlines. Categories={Categories} HeadlineCount={HeadlineCount} RequestedHeadlineCount={RequestedHeadlineCount}",
                string.Join(",", categories),
                headlines.Count,
                requestedHeadlineCount);
            return populatedSnapshot;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "NewsAPI lookup failed.");
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                SetCachedValue(briefingCache, cacheKey, null, options.FailureCacheTtlSeconds);
            }
            return null;
        }
    }

    private IEnumerable<string> ResolveCategories(IReadOnlyList<string> preferredCategories)
    {
        var requested = preferredCategories
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim().ToLowerInvariant())
            .Where(SupportedCategories.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested.Length > 0)
        {
            return requested.Take(MaxCategories);
        }

        return options.DefaultCategories
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim().ToLowerInvariant())
            .Where(SupportedCategories.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCategories);
    }

    private Uri BuildTopHeadlinesUri(string category, int headlineCount)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var queryParts = new (string Key, string Value)[]
        {
            ("country", options.Country),
            ("category", category),
            ("pageSize", headlineCount.ToString()),
            ("apiKey", options.ApiKey!)
        };
        var query = string.Join(
            "&",
            queryParts.Select(part =>
                $"{Uri.EscapeDataString(part.Key)}={Uri.EscapeDataString(part.Value)}"));
        return new Uri($"{baseUrl}/v2/top-headlines?{query}");
    }

    private static string? ReadString(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private static string? NormalizeHeadlineTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var trimmed = title.Trim();
        var suffixIndex = trimmed.LastIndexOf(" - ", StringComparison.Ordinal);
        if (suffixIndex > 30)
        {
            trimmed = trimmed[..suffixIndex].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private string BuildCacheKey(IReadOnlyList<string> categories, int requestedHeadlineCount)
    {
        var categoryKey = string.Join(",", categories.Select(category => category.Trim().ToLowerInvariant()));
        return $"{options.Country.Trim().ToLowerInvariant()}|{requestedHeadlineCount}|{categoryKey}";
    }

    private static bool TryGetCachedValue<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        out T value)
    {
        value = default!;
        if (!cache.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (entry.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            value = entry.Value;
            return true;
        }

        cache.TryRemove(key, out _);
        return false;
    }

    private static void SetCachedValue<T>(
        ConcurrentDictionary<string, CacheEntry<T>> cache,
        string key,
        T value,
        int ttlSeconds)
    {
        cache[key] = new CacheEntry<T>(
            value,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, ttlSeconds)));
    }

    private static readonly HashSet<string> SupportedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "business",
        "entertainment",
        "general",
        "health",
        "science",
        "sports",
        "technology"
    };

    private const int MaxHeadlines = 5;
    private const int MaxCategories = 2;

    private sealed record CacheEntry<T>(T Value, DateTimeOffset ExpiresUtc);
}
