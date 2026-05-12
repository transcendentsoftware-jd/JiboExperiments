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
            string? failureStatus = null;
            string? failureMessage = null;
            int? failureStatusCode = null;
            string? failureEndpoint = null;
            string? failureErrorCode = null;

            void CaptureFailure(
                string status,
                string? message,
                int? statusCode,
                Uri? endpoint,
                string? errorCode = null)
            {
                if (!string.IsNullOrWhiteSpace(failureStatus))
                {
                    return;
                }

                failureStatus = status;
                failureMessage = message;
                failureStatusCode = statusCode;
                failureEndpoint = endpoint is null ? null : SanitizeEndpoint(endpoint);
                failureErrorCode = errorCode;
            }

            foreach (var category in categories)
            {
                var uri = BuildTopHeadlinesUri(category, requestedHeadlineCount);
                using var response = await httpClient.GetAsync(uri, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await TryReadResponseBodySnippetAsync(response, cancellationToken);
                    var apiError = TryParseApiError(responseBody);
                    CaptureFailure(
                        "http_error",
                        apiError?.Message ?? $"Category '{category}' returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                        (int)response.StatusCode,
                        uri,
                        apiError?.Code);
                    logger.LogWarning(
                        "NewsAPI request failed for category {Category}. StatusCode={StatusCode} Reason={ReasonPhrase} ErrorCode={ErrorCode} ErrorMessage={ErrorMessage} Body={Body}",
                        category,
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        apiError?.Code ?? string.Empty,
                        apiError?.Message ?? string.Empty,
                        responseBody ?? string.Empty);
                    continue;
                }

                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("status", out var statusNode) &&
                    statusNode.ValueKind == JsonValueKind.String &&
                    !string.Equals(statusNode.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    CaptureFailure(
                        "api_error",
                        ReadString(document.RootElement, "message"),
                        null,
                        uri,
                        ReadString(document.RootElement, "code"));
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
                    CaptureFailure(
                        "schema_error",
                        $"Category '{category}' response did not include an articles array.",
                        null,
                        uri);
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
                        var snapshot = new NewsBriefingSnapshot(
                            headlines,
                            "NewsAPI",
                            ProviderStatus: "success");
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
                logger.LogInformation(
                    "NewsAPI category lookup produced no headlines. Falling back to uncategorized top headlines. Categories={Categories}",
                    string.Join(",", categories));

                var broadUri = BuildTopHeadlinesUri(category: null, requestedHeadlineCount);
                using var broadResponse = await httpClient.GetAsync(broadUri, cancellationToken);
                if (broadResponse.IsSuccessStatusCode)
                {
                    using var broadStream = await broadResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var broadDocument = await JsonDocument.ParseAsync(broadStream, cancellationToken: cancellationToken);
                    if (broadDocument.RootElement.TryGetProperty("articles", out var broadArticles) &&
                        broadArticles.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var article in broadArticles.EnumerateArray())
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
                            headlines.Add(new NewsHeadline(title, summary, "general", source, url));

                            if (headlines.Count >= requestedHeadlineCount)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        CaptureFailure(
                            "schema_error",
                            "Uncategorized fallback response did not include an articles array.",
                            null,
                            broadUri);
                        logger.LogWarning("NewsAPI uncategorized fallback response missing articles array.");
                    }
                }
                else
                {
                    var fallbackBody = await TryReadResponseBodySnippetAsync(broadResponse, cancellationToken);
                    var apiError = TryParseApiError(fallbackBody);
                    CaptureFailure(
                        "http_error",
                        apiError?.Message ?? $"Uncategorized fallback returned {(int)broadResponse.StatusCode} {broadResponse.ReasonPhrase}.",
                        (int)broadResponse.StatusCode,
                        broadUri,
                        apiError?.Code);
                    logger.LogWarning(
                        "NewsAPI uncategorized fallback failed. StatusCode={StatusCode} Reason={ReasonPhrase} ErrorCode={ErrorCode} ErrorMessage={ErrorMessage} Body={Body}",
                        (int)broadResponse.StatusCode,
                        broadResponse.ReasonPhrase,
                        apiError?.Code ?? string.Empty,
                        apiError?.Message ?? string.Empty,
                        fallbackBody ?? string.Empty);
                }
            }

            if (headlines.Count == 0)
            {
                logger.LogInformation(
                    "NewsAPI uncategorized headlines were empty. Falling back to everything query. Query={Query}",
                    options.FallbackQuery);

                var everythingUri = BuildEverythingUri(requestedHeadlineCount);
                using var everythingResponse = await httpClient.GetAsync(everythingUri, cancellationToken);
                if (everythingResponse.IsSuccessStatusCode)
                {
                    using var everythingStream = await everythingResponse.Content.ReadAsStreamAsync(cancellationToken);
                    using var everythingDocument = await JsonDocument.ParseAsync(everythingStream, cancellationToken: cancellationToken);
                    if (everythingDocument.RootElement.TryGetProperty("articles", out var everythingArticles) &&
                        everythingArticles.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var article in everythingArticles.EnumerateArray())
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
                            headlines.Add(new NewsHeadline(title, summary, "general", source, url));

                            if (headlines.Count >= requestedHeadlineCount)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        CaptureFailure(
                            "schema_error",
                            "Everything fallback response did not include an articles array.",
                            null,
                            everythingUri);
                        logger.LogWarning("NewsAPI everything fallback response missing articles array.");
                    }
                }
                else
                {
                    var everythingBody = await TryReadResponseBodySnippetAsync(everythingResponse, cancellationToken);
                    var apiError = TryParseApiError(everythingBody);
                    CaptureFailure(
                        "http_error",
                        apiError?.Message ?? $"Everything fallback returned {(int)everythingResponse.StatusCode} {everythingResponse.ReasonPhrase}.",
                        (int)everythingResponse.StatusCode,
                        everythingUri,
                        apiError?.Code);
                    logger.LogWarning(
                        "NewsAPI everything fallback failed. StatusCode={StatusCode} Reason={ReasonPhrase} ErrorCode={ErrorCode} ErrorMessage={ErrorMessage} Body={Body}",
                        (int)everythingResponse.StatusCode,
                        everythingResponse.ReasonPhrase,
                        apiError?.Code ?? string.Empty,
                        apiError?.Message ?? string.Empty,
                        everythingBody ?? string.Empty);
                }
            }

            if (headlines.Count == 0)
            {
                var emptySnapshot = new NewsBriefingSnapshot(
                    Array.Empty<NewsHeadline>(),
                    "NewsAPI",
                    ProviderStatus: failureStatus ?? "empty",
                    ProviderMessage: failureMessage ?? "NewsAPI returned no usable headlines.",
                    ProviderHttpStatusCode: failureStatusCode,
                    ProviderEndpoint: failureEndpoint,
                    ProviderErrorCode: failureErrorCode);
                SetCachedValue(briefingCache, cacheKey, emptySnapshot, options.FailureCacheTtlSeconds);
                logger.LogWarning(
                    "NewsAPI returned no usable headlines. Categories={Categories} RequestedHeadlineCount={RequestedHeadlineCount}",
                    string.Join(",", categories),
                    requestedHeadlineCount);
                return emptySnapshot;
            }

            var populatedSnapshot = new NewsBriefingSnapshot(
                headlines,
                "NewsAPI",
                ProviderStatus: "success");
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
            var exceptionSnapshot = new NewsBriefingSnapshot(
                Array.Empty<NewsHeadline>(),
                "NewsAPI",
                ProviderStatus: "exception",
                ProviderMessage: exception.Message);
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                SetCachedValue(briefingCache, cacheKey, exceptionSnapshot, options.FailureCacheTtlSeconds);
            }
            return exceptionSnapshot;
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

    private Uri BuildTopHeadlinesUri(string? category, int headlineCount)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var queryParts = new List<(string Key, string Value)>
        {
            ("country", options.Country),
            ("pageSize", headlineCount.ToString()),
            ("apiKey", options.ApiKey!)
        };
        if (!string.IsNullOrWhiteSpace(category))
        {
            queryParts.Add(("category", category));
        }

        var query = string.Join(
            "&",
            queryParts.Select(part =>
                $"{Uri.EscapeDataString(part.Key)}={Uri.EscapeDataString(part.Value)}"));
        return new Uri($"{baseUrl}/v2/top-headlines?{query}");
    }

    private Uri BuildEverythingUri(int headlineCount)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var queryParts = new List<(string Key, string Value)>
        {
            ("language", options.Language),
            ("sortBy", "publishedAt"),
            ("q", options.FallbackQuery),
            ("pageSize", headlineCount.ToString()),
            ("apiKey", options.ApiKey!)
        };

        var query = string.Join(
            "&",
            queryParts.Select(part =>
                $"{Uri.EscapeDataString(part.Key)}={Uri.EscapeDataString(part.Value)}"));
        return new Uri($"{baseUrl}/v2/everything?{query}");
    }

    private static async Task<string?> TryReadResponseBodySnippetAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            const int maxLength = 400;
            return body.Length <= maxLength
                ? body
                : body[..maxLength];
        }
        catch
        {
            return null;
        }
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

    private static ApiError? TryParseApiError(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var code = ReadString(document.RootElement, "code");
            var message = ReadString(document.RootElement, "message");
            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            return new ApiError(code, message);
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeEndpoint(Uri uri)
    {
        var path = uri.GetLeftPart(UriPartial.Path);
        if (string.IsNullOrWhiteSpace(uri.Query))
        {
            return path;
        }

        var filtered = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(static pair =>
            {
                var key = pair.Split('=', 2)[0];
                return !string.Equals(key, "apiKey", StringComparison.OrdinalIgnoreCase);
            });
        var safeQuery = string.Join("&", filtered);
        return string.IsNullOrWhiteSpace(safeQuery) ? path : $"{path}?{safeQuery}";
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

    private sealed record ApiError(string? Code, string? Message);
    private sealed record CacheEntry<T>(T Value, DateTimeOffset ExpiresUtc);
}
