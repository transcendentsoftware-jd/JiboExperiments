using System.Net;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class ChatGptSearchProviderTests
{
    [Fact]
    public async Task SearchAsync_ReturnsAnswer_WhenOpenAiResponds()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                                {
                                  "choices": [
                                    {
                                      "message": {
                                        "role": "assistant",
                                        "content": "Donald Trump was born on June 14, 1946. As of June 19, 2026, he is 80 years old."
                                      }
                                    }
                                  ]
                                }
                                """);
        });
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "How old is Donald Trump",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")));

        Assert.NotNull(result);
        Assert.Equal(SearchBackendKind.ChatGPT, result!.BackendKind);
        Assert.Contains("80 years old", result.AnswerText);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", capturedRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization.Parameter);

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-5.4-nano", json.RootElement.GetProperty("model").GetString());
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("You ARE Jibo", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("knowledge base", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("How old is Donald Trump", messages[1].GetProperty("content").GetString());
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task SearchAsync_UsesDefaultModel_WhenModelOmitted()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"Hi"}}]}""");
        });
        var provider = CreateProvider(handler, model: null);

        await provider.SearchAsync(new KnowledgeSearchRequest(
            "Hello",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", null)));

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal(SearchBackendSettingsResolver.DefaultChatGptModel,
            json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SearchAsync_StripsMarkdownBold_ForSpeech()
    {
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse("""
                                                                        {
                                                                          "choices": [
                                                                            {
                                                                              "message": {
                                                                                "role": "assistant",
                                                                                "content": "Donald Trump was born on **June 14, 1946**."
                                                                              }
                                                                            }
                                                                          ]
                                                                        }
                                                                        """));
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "How old is Donald Trump",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", null)));

        Assert.NotNull(result);
        Assert.Equal("Donald Trump was born on June 14, 1946.", result!.AnswerText);
    }

    [Fact]
    public async Task SearchAsync_ReturnsNull_WhenApiKeyMissing()
    {
        var provider = CreateProvider(
            new RecordingHttpMessageHandler(_ => JsonResponse("""{"choices":[]}""")),
            null);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "Hello",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, null, null)));

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_UsesCustomInstructions_WhenConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"Hi"}}]}""");
        });
        var provider = CreateProvider(handler, llmInstructions: "Custom assistant instructions.");

        await provider.SearchAsync(new KnowledgeSearchRequest(
            "Hello",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")));

        using var json = JsonDocument.Parse(capturedBody!);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal("Custom assistant instructions.", messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain("You ARE Jibo", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_VisionQuestionWithCamera_SendsFreshPhotoToModel()
    {
        var calls = new List<string>();
        string? chatBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            calls.Add($"{request.Method} {uri}");
            if (uri == "http://jibo.test:8486/media/photo")
                return JsonResponse("""{"id":"photo-1"}""");
            if (uri == "http://jibo.test:8486/media/photo?id=photo-1")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3])
                };
            chatBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"I see you waving at me."}}]}""");
        });
        var provider = CreateProvider(handler, cameraUrl: "http://jibo.test:8486");

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "what do you see",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")));

        Assert.Equal("I see you waving at me.", result!.AnswerText);
        Assert.Equal("POST http://jibo.test:8486/media/photo", calls[0]);
        Assert.Equal("GET http://jibo.test:8486/media/photo?id=photo-1", calls[1]);
        using var json = JsonDocument.Parse(chatBody!);
        var content = json.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.StartsWith(
            "data:image/jpeg;base64,",
            content[1].GetProperty("image_url").GetProperty("url").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_VisionQuestion_CameraFails_SaysSoWithoutCallingModel()
    {
        var chatCalled = false;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "jibo.test")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            chatCalled = true;
            return JsonResponse("""{"choices":[]}""");
        });
        var provider = CreateProvider(handler, cameraUrl: "http://jibo.test:8486");

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "can you see me",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")));

        Assert.Equal(ChatGptSearchProvider.CameraUnavailableReply, result!.AnswerText);
        Assert.False(chatCalled);
    }

    [Fact]
    public async Task SearchAsync_OrdinaryQuestionWithCamera_DoesNotTakePhoto()
    {
        var cameraCalled = false;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "jibo.test") cameraCalled = true;
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"Paris."}}]}""");
        });
        var provider = CreateProvider(handler, cameraUrl: "http://jibo.test:8486");

        await provider.SearchAsync(new KnowledgeSearchRequest(
            "What is the capital of France",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")));

        Assert.False(cameraCalled);
    }

    [Theory]
    [InlineData("{\"id\":\"abc\"}", "abc")]
    [InlineData("{\"id\":\"\"}", null)]
    [InlineData("not json", null)]
    public void ReadPhotoId_ReadsMediaServiceAnswer(string json, string? expected)
    {
        Assert.Equal(expected, ChatGptSearchProvider.ReadPhotoId(json));
    }

    private static ChatGptSearchProvider CreateProvider(
        HttpMessageHandler handler,
        string? apiKey = "test-api-key",
        string? model = "gpt-5.4-nano",
        string? llmInstructions = null,
        string? cameraUrl = null)
    {
        var primary = model is null
            ? $"ChatGPT!{apiKey}"
            : $"ChatGPT!{apiKey}!{model}";

        return new ChatGptSearchProvider(
            new HttpClient(handler),
            SearchBackendOptions.Create(primary, null, 300, 45, llmInstructions),
            NullLogger<ChatGptSearchProvider>.Instance)
        {
            CameraUrlOverride = cameraUrl is null ? null : new Uri(cameraUrl)
        };
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}