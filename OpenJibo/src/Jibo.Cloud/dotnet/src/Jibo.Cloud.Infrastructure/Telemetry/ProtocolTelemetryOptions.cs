namespace Jibo.Cloud.Infrastructure.Telemetry;

public sealed class ProtocolTelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = "captures/http";
}
