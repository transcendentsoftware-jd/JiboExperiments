using System.Text.Json;

namespace Jibo.Cloud.Domain.Models;

public sealed class CapturedWebSocketFixture
{
    public string Name { get; init; } = string.Empty;
    public CapturedWebSocketFixtureSession Session { get; init; } = new();
    public IReadOnlyList<CapturedWebSocketFixtureStep> Steps { get; init; } = [];
}

public sealed class CapturedWebSocketFixtureSession
{
    public string HostName { get; init; } = string.Empty;
    public string Path { get; init; } = "/";
    public string Kind { get; init; } = "unknown";
    public string? Token { get; init; }
}

public sealed class CapturedWebSocketFixtureStep
{
    public JsonElement? Text { get; init; }
    public IReadOnlyList<int>? Binary { get; init; }
    public IReadOnlyList<string> ExpectedReplyTypes { get; init; } = [];
}
