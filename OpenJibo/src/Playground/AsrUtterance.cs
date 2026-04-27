using System.Text.Json.Serialization;

namespace Playground;

public sealed class AsrUtterance
{
    [JsonPropertyName("utterance")]
    public string? Utterance { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }
}