using System.Text.Json;

namespace Jibo.Cloud.Domain.Models;

public sealed class ProtocolDispatchResult
{
    public int StatusCode { get; init; } = 200;
    public string ContentType { get; init; } = "application/x-amz-json-1.1";
    public string BodyText { get; init; } = "{}";
    public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public static ProtocolDispatchResult Ok(object? body = null)
    {
        return new ProtocolDispatchResult
        {
            StatusCode = 200,
            BodyText = JsonSerializer.Serialize(body ?? new { ok = true })
        };
    }

    public static ProtocolDispatchResult NoContent()
    {
        return new ProtocolDispatchResult
        {
            StatusCode = 204,
            BodyText = string.Empty,
            ContentType = "text/plain"
        };
    }

    public static ProtocolDispatchResult Raw(int statusCode, string bodyText, string contentType = "text/plain")
    {
        return new ProtocolDispatchResult
        {
            StatusCode = statusCode,
            BodyText = bodyText,
            ContentType = contentType
        };
    }
}
