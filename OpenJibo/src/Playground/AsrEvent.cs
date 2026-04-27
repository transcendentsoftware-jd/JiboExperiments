using System.Text.Json.Serialization;

namespace Playground;

public sealed class AsrEvent
{
    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("task_id")]
    public string? TaskId { get; set; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("utterances")]
    public List<AsrUtterance>? Utterances { get; set; }
}