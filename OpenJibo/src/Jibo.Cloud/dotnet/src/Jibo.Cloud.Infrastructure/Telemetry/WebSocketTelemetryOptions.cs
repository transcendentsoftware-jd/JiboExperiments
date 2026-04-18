namespace Jibo.Cloud.Infrastructure.Telemetry;

public sealed class WebSocketTelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public bool ExportFixtures { get; set; } = true;
    public string DirectoryPath { get; set; } = "captures/websocket";
}