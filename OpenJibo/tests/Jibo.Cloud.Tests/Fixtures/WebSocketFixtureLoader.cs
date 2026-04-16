using System.Text.Json;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Tests.Fixtures;

internal static class WebSocketFixtureLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static WebSocketFixture Load(string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        var root = document.RootElement;

        var session = root.GetProperty("session");
        var steps = new List<WebSocketFixtureStep>();
        foreach (var stepElement in root.GetProperty("steps").EnumerateArray())
        {
            steps.Add(new WebSocketFixtureStep
            {
                Message = new WebSocketMessageEnvelope
                {
                    HostName = session.GetProperty("hostName").GetString() ?? "neo-hub.jibo.com",
                    Path = session.GetProperty("path").GetString() ?? "/listen",
                    Kind = session.GetProperty("kind").GetString() ?? "neo-hub-listen",
                    Token = session.GetProperty("token").GetString(),
                    Text = stepElement.TryGetProperty("text", out var text) ? text.GetRawText() : null,
                    Binary = stepElement.TryGetProperty("binary", out var binary) && binary.ValueKind == JsonValueKind.Array
                        ? binary.EnumerateArray().Select(item => (byte)item.GetInt32()).ToArray()
                        : null
                },
                ExpectedReplyTypes = stepElement.GetProperty("expectedReplyTypes")
                    .EnumerateArray()
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray(),
                ExpectedReplies = stepElement.TryGetProperty("expectedReplies", out var expectedReplies) && expectedReplies.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.Deserialize<List<ExpectedWebSocketReply>>(expectedReplies.GetRawText(), SerializerOptions) ?? []
                    : []
            });
        }

        return new WebSocketFixture
        {
            Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? Path.GetFileNameWithoutExtension(relativePath) : Path.GetFileNameWithoutExtension(relativePath),
            Steps = steps
        };
    }
}

internal sealed class WebSocketFixture
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<WebSocketFixtureStep> Steps { get; init; } = [];
}

internal sealed class WebSocketFixtureStep
{
    public WebSocketMessageEnvelope Message { get; init; } = new();
    public IReadOnlyList<string> ExpectedReplyTypes { get; init; } = [];
    public IReadOnlyList<ExpectedWebSocketReply> ExpectedReplies { get; init; } = [];
}

internal sealed class ExpectedWebSocketReply
{
    public string Type { get; init; } = string.Empty;
    public int? DelayMs { get; init; }
    public JsonElement? JsonSubset { get; init; }
}
