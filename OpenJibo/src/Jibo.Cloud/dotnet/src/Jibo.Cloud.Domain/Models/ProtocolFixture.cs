namespace Jibo.Cloud.Domain.Models;

public sealed class ProtocolFixture
{
    public string Name { get; init; } = string.Empty;
    public ProtocolEnvelope Request { get; init; } = new();
    public int ExpectedStatusCode { get; init; } = 200;
}
