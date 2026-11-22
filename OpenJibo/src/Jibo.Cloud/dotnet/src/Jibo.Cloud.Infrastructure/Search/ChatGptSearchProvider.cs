using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Search;

public sealed class ChatGptSearchProvider(
    HttpClient httpClient,
    SearchBackendOptions options,
    ILogger<ChatGptSearchProvider> logger)
    : IKnowledgeSearchProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public SearchBackendKind Kind => SearchBackendKind.ChatGPT;

    /// <summary>Test hook: camera service address used instead of OPENJIBO_JIBO_CAMERA_URL.</summary>
    internal Uri? CameraUrlOverride { get; init; }

    internal const string CameraUnavailableReply =
        "I couldn't get a picture from my camera just now. Try asking me again in a moment.";

    public async Task<KnowledgeSearchResult?> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.BackendSpec.Kind != SearchBackendKind.ChatGPT ||
            string.IsNullOrWhiteSpace(request.BackendSpec.Credential) ||
            string.IsNullOrWhiteSpace(request.Query))
            return null;

        var endpoint = SearchBackendSettingsResolver.ResolveEndpoint(request.BackendSpec);
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        var model = SearchBackendSettingsResolver.ResolveModel(request.BackendSpec);

        // "What do you see?": take a fresh photo with Jibo's camera and let the model describe it.
        // Never cached, since the answer depends on the moment.
        var cameraUri = CameraUrlOverride ?? RobotCameraSettings.CameraBaseUri;
        if (cameraUri is not null && RobotCameraSettings.IsVisionQuestion(request.Query))
        {
            var photo = await TryTakeCameraPhotoAsync(cameraUri, cancellationToken);
            return photo is null
                ? new KnowledgeSearchResult(CameraUnavailableReply, SearchBackendKind.ChatGPT)
                : await TryCompleteAsync(
                    endpoint,
                    request.BackendSpec.Credential,
                    model,
                    request.Query.Trim(),
                    cancellationToken,
                    photo);
        }

        var cacheKey = BuildCacheKey(request.BackendSpec, model, request.Query.Trim());
        if (TryGetCachedValue(cacheKey, out var cachedResult))
            return cachedResult;

        var result = await TryCompleteAsync(
            endpoint,
            request.BackendSpec.Credential,
            model,
            request.Query.Trim(),
            cancellationToken);
        SetCachedValue(
            cacheKey,
            result,
            result.Outcome == KnowledgeSearchOutcome.Found
                ? options.CacheTtlSeconds
                : options.FailureCacheTtlSeconds);
        return result;
    }

    private async Task<KnowledgeSearchResult> TryCompleteAsync(
        string endpoint,
        string apiKey,
        string model,
        string prompt,
        CancellationToken cancellationToken,
        byte[]? photo = null)
    {
        try
        {
            var (systemMessage, userMessage) = JiboKnowledgeSearchPrompts.BuildChatGptMessages(
                prompt,
                options.LlmInstructions);
            object userContent = photo is null
                ? userMessage
                : new object[]
                {
                    new
                    {
                        type = "text",
                        text = userMessage + "\n\n(The attached photo was just taken with your own camera. " +
                               "Describe what you see in one to three short spoken sentences.)"
                    },
                    new
                    {
                        type = "image_url",
                        image_url = new
                        {
                            url = $"data:{DetectImageMediaType(photo) ?? "image/jpeg"};base64,{Convert.ToBase64String(photo)}"
                        }
                    }
                };
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Content = JsonContent.Create(
                new ChatCompletionRequest(
                    model,
                    [
                        new ChatCompletionMessage("system", systemMessage),
                        new ChatCompletionMessage("user", userContent)
                    ]));

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ChatGPT lookup failed with status {StatusCode}.", response.StatusCode);
                return KnowledgeSearchResult.Unavailable(SearchBackendKind.ChatGPT);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
            var answerText = KnowledgeSearchResponseFormatter.NormalizeForSpeech(
                parsed?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty);
            return string.IsNullOrWhiteSpace(answerText)
                ? KnowledgeSearchResult.NotFound(SearchBackendKind.ChatGPT)
                : new KnowledgeSearchResult(answerText, SearchBackendKind.ChatGPT);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "ChatGPT lookup failed for model {Model}.", model);
            return KnowledgeSearchResult.Unavailable(SearchBackendKind.ChatGPT);
        }
    }

    // Jibo's media service: POST /media/photo {"camera":1,"type":2,"filters":[]} -> {"id":"..."},
    // then GET /media/photo?id=... returns the JPEG (camera 1 = right, type 2 = snapshot).
    private async Task<byte[]?> TryTakeCameraPhotoAsync(Uri cameraUri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var takeRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(cameraUri, "/media/photo"))
            {
                Content = new StringContent("""{"camera":1,"type":2,"filters":[]}""", Encoding.UTF8, "application/json")
            };
            using var takeResponse = await httpClient.SendAsync(takeRequest, timeout.Token);
            if (!takeResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Jibo camera did not take a photo (HTTP {Status}).", (int)takeResponse.StatusCode);
                return null;
            }

            var photoId = ReadPhotoId(await takeResponse.Content.ReadAsStringAsync(timeout.Token));
            if (photoId is null) return null;

            var bytes = await httpClient.GetByteArrayAsync(
                new Uri(cameraUri, "/media/photo?id=" + Uri.EscapeDataString(photoId)),
                timeout.Token);
            if (DetectImageMediaType(bytes) is null) return null;

            logger.LogInformation("Took a photo with Jibo's camera ({Bytes} bytes).", bytes.Length);
            return bytes;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Jibo camera photo failed.");
            return null;
        }
    }

    internal static string? ReadPhotoId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("id", out var id) &&
                   id.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(id.GetString())
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? DetectImageMediaType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        return null;
    }

    private static string BuildCacheKey(SearchBackendSpec spec, string model, string query)
    {
        return $"chatgpt|{spec.Credential}|{model}|{query}";
    }

    private bool TryGetCachedValue(string cacheKey, out KnowledgeSearchResult? result)
    {
        result = null;
        if (!_cache.TryGetValue(cacheKey, out var entry) || entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return false;

        result = entry.Result;
        return true;
    }

    private void SetCachedValue(string cacheKey, KnowledgeSearchResult? result, int ttlSeconds)
    {
        _cache[cacheKey] = new CacheEntry(
            result,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, ttlSeconds)));
    }

    private sealed record CacheEntry(KnowledgeSearchResult? Result, DateTimeOffset ExpiresAtUtc);

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<ChatCompletionMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream = false);

    private sealed record ChatCompletionMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")]
        object Content);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")]
        IReadOnlyList<ChatCompletionChoice>? Choices);

    private sealed record ChatCompletionChoice(
        [property: JsonPropertyName("message")]
        ChatCompletionResponseMessage? Message);

    // Requests may carry text or text + image parts; responses always carry text.
    private sealed record ChatCompletionResponseMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")]
        string? Content);
}