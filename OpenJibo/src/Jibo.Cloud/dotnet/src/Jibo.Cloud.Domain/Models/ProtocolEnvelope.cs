using System.Text.Json;

namespace Jibo.Cloud.Domain.Models;

public sealed class ProtocolEnvelope
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset ReceivedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Transport { get; init; } = "http";
    public string Method { get; init; } = "POST";
    public string HostName { get; init; } = "api.jibo.com";
    public string Path { get; init; } = "/";
    public string? ServicePrefix { get; init; }
    public string? Operation { get; init; }
    public string? DeviceId { get; init; }
    public string? CorrelationId { get; init; }
    public string? FirmwareVersion { get; init; }
    public string? ApplicationVersion { get; init; }
    public string BodyText { get; init; } = string.Empty;
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public JsonElement? TryParseBody()
    {
        if (string.IsNullOrWhiteSpace(BodyText))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(BodyText);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}
