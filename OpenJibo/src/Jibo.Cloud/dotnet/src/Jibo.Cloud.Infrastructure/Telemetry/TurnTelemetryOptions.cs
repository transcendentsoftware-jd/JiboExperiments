namespace Jibo.Cloud.Infrastructure.Telemetry;

public sealed class TurnTelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = "captures/turn";
}