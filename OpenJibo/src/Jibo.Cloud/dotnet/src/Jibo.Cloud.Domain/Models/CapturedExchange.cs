namespace Jibo.Cloud.Domain.Models;

public sealed class CapturedExchange
{
    public string CaptureId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public ProtocolEnvelope Request { get; init; } = new();
    public ProtocolDispatchResult Response { get; init; } = ProtocolDispatchResult.Ok();
    public string Confidence { get; init; } = "observed";
    public IDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
