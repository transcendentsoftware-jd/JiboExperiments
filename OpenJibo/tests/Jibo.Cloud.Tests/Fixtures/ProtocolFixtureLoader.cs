using System.Text.Json;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Tests.Fixtures;

internal static class ProtocolFixtureLoader
{
    public static ProtocolFixture Load(string relativePath)
    {
        var fullPath = Path.Combine(AppContext.BaseDirectory, relativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        var root = document.RootElement;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in headerElement.EnumerateObject())
            {
                headers[property.Name] = property.Value.ToString();
            }
        }

        var bodyText = root.TryGetProperty("body", out var bodyElement)
            ? bodyElement.GetRawText()
            : string.Empty;

        var target = headers.TryGetValue("x-amz-target", out var targetValue)
            ? targetValue
            : string.Empty;
        var targetParts = target.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);

        return new ProtocolFixture
        {
            Name = Path.GetFileNameWithoutExtension(relativePath),
            Request = new ProtocolEnvelope
            {
                HostName = root.TryGetProperty("host", out var hostElement) ? hostElement.GetString() ?? "api.jibo.com" : "api.jibo.com",
                Method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() ?? "POST" : "POST",
                Path = root.TryGetProperty("path", out var pathElement) ? pathElement.GetString() ?? "/" : "/",
                Headers = headers,
                ServicePrefix = targetParts.Length > 0 ? targetParts[0] : null,
                Operation = targetParts.Length > 1 ? targetParts[1] : null,
                BodyText = bodyText
            },
            ExpectedStatusCode = 200
        };
    }
}
