using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class FileWebSocketTelemetrySinkTests : IDisposable
{
    private readonly string _directoryPath;

    public FileWebSocketTelemetrySinkTests()
    {
        _directoryPath = Path.Combine(Path.GetTempPath(), "OpenJibo.Tests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task RecordsFixtureOnConnectionClose()
    {
        var sink = CreateSink();
        var envelope = new WebSocketMessageEnvelope
        {
            ConnectionId = "conn-1",
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Token = "token-1",
            Text = """{"type":"LISTEN","transID":"trans-1","data":{"text":"hello jibo"}}"""
        };
        var session = new CloudSession
        {
            Token = "token-1",
            HostName = "neo-hub.jibo.com",
            Path = "/listen"
        };
        session.TurnState.TransId = "trans-1";

        await sink.RecordConnectionOpenedAsync(envelope, session);
        await sink.RecordInboundAsync(envelope, session, "LISTEN");
        await sink.RecordOutboundAsync(envelope, session,
        [
            new WebSocketReply { Text = """{"type":"LISTEN"}""" },
            new WebSocketReply { Text = """{"type":"EOS"}""" }
        ]);
        await sink.RecordConnectionClosedAsync(envelope, session, "test");

        var fixtureDirectory = Path.Combine(_directoryPath, "fixtures");
        var fixturePath = Directory.GetFiles(fixtureDirectory, "*.flow.json").Single();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        Assert.Equal("neo-hub.jibo.com", document.RootElement.GetProperty("session").GetProperty("hostName").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("steps").GetArrayLength());
        Assert.Equal("LISTEN", document.RootElement.GetProperty("steps")[0].GetProperty("expectedReplyTypes")[0].GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
        {
            Directory.Delete(_directoryPath, true);
        }
    }

    private FileWebSocketTelemetrySink CreateSink()
    {
        return new FileWebSocketTelemetrySink(
            NullLogger<FileWebSocketTelemetrySink>.Instance,
            Options.Create(new WebSocketTelemetryOptions
            {
                Enabled = true,
                ExportFixtures = true,
                DirectoryPath = _directoryPath
            }));
    }
}
