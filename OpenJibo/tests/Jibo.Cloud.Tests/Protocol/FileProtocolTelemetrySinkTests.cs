using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class FileProtocolTelemetrySinkTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _repoRoot;
    private readonly string _appBaseDirectory;

    public FileProtocolTelemetrySinkTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "OpenJibo.ProtocolTelemetry.Tests", Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_workspaceRoot, "OpenJibo");
        _appBaseDirectory = Path.Combine(_repoRoot, "src", "Jibo.Cloud", "dotnet", "src", "Jibo.Cloud.Api", "bin", "Debug", "net10.0");

        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(_appBaseDirectory);
        File.WriteAllText(Path.Combine(_repoRoot, "OpenJibo.slnx"), string.Empty);
    }

    [Fact]
    public async Task RecordAsync_ResolvesRelativePathAgainstOpenJiboRepoRoot()
    {
        var captureDirectory = CapturePathResolver.Resolve("captures/http", _repoRoot, _appBaseDirectory);
        var sink = new FileProtocolTelemetrySink(
            NullLogger<FileProtocolTelemetrySink>.Instance,
            Options.Create(new ProtocolTelemetryOptions
            {
                Enabled = true,
                DirectoryPath = captureDirectory
            }));

        var envelope = new ProtocolEnvelope
        {
            HostName = "api.jibo.com",
            Method = "POST",
            Path = "/",
            ServicePrefix = "Notification_20150505",
            Operation = "NewRobotToken",
            BodyText = """{"deviceId":"robot-123"}"""
        };

        await sink.RecordAsync(envelope, ProtocolDispatchResult.Ok(new { token = "token-robot-123" }));

        var captureFile = Directory.GetFiles(captureDirectory, "*.events.ndjson").Single();
        var contents = await File.ReadAllTextAsync(captureFile);

        Assert.Contains("Notification_20150505", contents);
        Assert.DoesNotContain(Path.Combine("bin", "Debug"), captureFile, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, true);
        }
    }
}
