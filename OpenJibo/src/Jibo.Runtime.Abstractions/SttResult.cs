namespace Jibo.Runtime.Abstractions;

public sealed class SttResult
{
    public string Text { get; init; } = string.Empty;
    public string? Provider { get; init; }
    public float? Confidence { get; init; }
    public string? Locale { get; init; }
    public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();
}