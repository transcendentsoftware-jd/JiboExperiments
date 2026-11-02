using Azure.Monitor.OpenTelemetry.Exporter;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.DependencyInjection;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Infrastructure.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System.Diagnostics;
using System.Text;

OpenJiboEnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);

ConfigureOperationalMetrics(builder);

if (ShouldResetDiagnosticsOnStartup(builder.Configuration))
    ResetDiagnosticsDirectories(builder.Configuration);

builder.Host.UseSerilog((context, _, loggerConfiguration) =>
{
    var minimumLevel = ParseLogEventLevel(context.Configuration["OpenJibo:Logging:MinimumLevel"]);
    var logDirectory = ResolveConfiguredPath(context.Configuration,
        "OpenJibo:Logging:DirectoryPath",
        "captures/logs");
    var logFileName = context.Configuration["OpenJibo:Logging:FileName"] ?? "openjibo-.log";
    Directory.CreateDirectory(logDirectory);

    loggerConfiguration
        .MinimumLevel.Is(minimumLevel)
        .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "OpenJibo.Cloud.Api")
        .Filter.ByExcluding(e =>
            PortalLogPollingDiagnosticsState.DisableServerLogsEndpointLogging &&
            e.RenderMessage().Contains("/api/portal/server/logs"))
        .WriteTo.Console(
            theme: AnsiConsoleTheme.Code,
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            Path.Combine(logDirectory, logFileName),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true,
            restrictedToMinimumLevel: minimumLevel,
            outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}");
});

builder.Services.AddOpenJiboCloud(builder.Configuration);
builder.Services.AddSingleton<HomeAssistantWebSocketHandler>();
builder.Services.AddSingleton<SingleRobotHttpHubAccessGuard>();
builder.Services.AddSingleton<WebSocketTransportPolicy>();
builder.Services.AddSingleton<WebSocketRequestCoordinator>();
builder.Services.AddHubChannelLeases(builder.Configuration);
builder.Services.AddHttpClient("OpenJiboFleetPeerSync", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHostedService<FleetPeerSyncService>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<RobotDiagnosticBeaconStore>();

var app = builder.Build();

PortalLogPollingDiagnosticsState.DisableServerLogsEndpointLogging =
    bool.TryParse(builder.Configuration["OpenJibo:Logging:DisableServerLogsEndpointLogging"], out var disableLogs) && disableLogs;

app.Logger.LogInformation("Starting Open Jibo Cloud Api version {Version}", OpenJiboCloudBuildInfo.Version);
app.Logger.LogInformation(
    "Protocol auth diagnostics effectiveEnabled={Enabled} containerAppRevision={Revision}",
    bool.TryParse(builder.Configuration["OpenJibo:ProtocolAuthDiagnostics:Enabled"], out var protocolAuthDiagnosticsEnabled) &&
    protocolAuthDiagnosticsEnabled,
    Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local");

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

app.Use(async (context, next) =>
{
    var skipLogging = PortalLogPollingDiagnosticsState.DisableServerLogsEndpointLogging &&
        context.Request.Path.StartsWithSegments("/api/portal/server/logs");

    if (skipLogging)
    {
        await next();
        return;
    }

    var started = Stopwatch.GetTimestamp();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Jibo.Cloud.Api.RequestDiagnostics");
    var remoteIp = context.Connection.RemoteIpAddress?.ToString();
    var userAgent = context.Request.Headers.UserAgent.ToString();
    var socketKind = context.WebSockets.IsWebSocketRequest
        ? SocketKindResolver.Resolve(context.Request.Host.Host, context.Request.Path)
        : "http";
    var safePath = RequestLogSanitizer.RedactWebSocketPath(socketKind, context.Request.Path);
    var safeQuery = RequestLogSanitizer.RedactQuery(context.Request.QueryString,
        context.WebSockets.IsWebSocketRequest);

    logger.LogInformation(
        "HTTP request started traceId={TraceId} method={Method} host={Host} path={Path} " +
        "query={Query} remoteIp={RemoteIp} userAgent={UserAgent} webSocket={WebSocket}",
        context.TraceIdentifier,
        context.Request.Method,
        context.Request.Host.Host,
        safePath,
        safeQuery,
        remoteIp,
        userAgent,
        context.WebSockets.IsWebSocketRequest);

    try
    {
        await next();
    }
    catch (Exception exception)
    {
        logger.LogError(
            exception,
            "HTTP request failed traceId={TraceId} method={Method} host={Host} path={Path} remoteIp={RemoteIp} webSocket={WebSocket}",
            context.TraceIdentifier,
            context.Request.Method,
            context.Request.Host.Host,
            safePath,
            remoteIp,
            context.WebSockets.IsWebSocketRequest);
        throw;
    }
    finally
    {
        logger.LogInformation(
            "HTTP request completed traceId={TraceId} method={Method} host={Host} path={Path} " +
            "statusCode={StatusCode} elapsedMs={ElapsedMs} remoteIp={RemoteIp} webSocket={WebSocket}",
            context.TraceIdentifier,
            context.Request.Method,
            context.Request.Host.Host,
            safePath,
            context.Response.StatusCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            remoteIp,
            context.WebSockets.IsWebSocketRequest);
    }
});

app.Use(async (context, next) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        await next();
        return;
    }

    var coordinator = context.RequestServices.GetRequiredService<WebSocketRequestCoordinator>();
    await coordinator.HandleAsync(context);
});

app.MapGet("/health", () => Results.Json(new
{
    ok = true,
    service = "OpenJibo Cloud Api",
    version = OpenJiboCloudBuildInfo.Version
}));

app.MapGet("/health/replica", (HttpContext context, ReleaseSmokeAuthorizationOptions authorization) =>
{
    if (!authorization.Enabled) return Results.NotFound();
    if (!authorization.IsSecretAuthorized(
            context.Request.Headers[ReleaseSmokeAuthorizationOptions.SecretHeaderName].ToString()))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    context.Response.Headers.CacheControl = "no-store";
    var revision = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local";
    var replica = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
    return Results.Json(new
    {
        ok = true,
        revision,
        replica,
        instanceId = $"{revision}/{replica}"
    });
});

app.MapPost("/health/sigv4-replay-proof", async (
    HttpContext context,
    IServiceProvider services,
    CancellationToken cancellationToken) =>
{
    var proofService = services.GetService<AwsSigV4ReplayProbeProofService>();
    if (proofService is null) return Results.NotFound();

    var requestEnvelope = await ApiRequestEnvelopeFactory.CreateAsync(context, cancellationToken);
    var signedEnvelope = new ProtocolEnvelope
    {
        RequestId = requestEnvelope.RequestId,
        Transport = requestEnvelope.Transport,
        Method = requestEnvelope.Method,
        HostName = requestEnvelope.HostName,
        Path = "/",
        ServicePrefix = requestEnvelope.ServicePrefix,
        Operation = requestEnvelope.Operation,
        DeviceId = requestEnvelope.DeviceId,
        CorrelationId = requestEnvelope.CorrelationId,
        FirmwareVersion = requestEnvelope.FirmwareVersion,
        ApplicationVersion = requestEnvelope.ApplicationVersion,
        BodyText = requestEnvelope.BodyText,
        BodyBytes = requestEnvelope.BodyBytes,
        Headers = requestEnvelope.Headers,
        QueryParameters = requestEnvelope.QueryParameters
    };
    var proof = await proofService.ProveAsync(signedEnvelope, cancellationToken);
    if (!proof.Verified) return Results.StatusCode(StatusCodes.Status409Conflict);

    context.Response.Headers.CacheControl = "no-store";
    return Results.Json(new
    {
        verified = true,
        priorObservationCount = proof.PriorObservationCount
    });
});

app.MapPortalStaticFiles();
app.MapPortalEndpoints();

app.MapMethods("/{**path}", ["GET", "POST", "PUT"], async (HttpContext context, JiboCloudProtocolService service,
    IProtocolTelemetrySink telemetrySink, ITransportMetrics transportMetrics,
    ReleaseSmokeAuthorizationOptions releaseSmokeAuthorization, CancellationToken cancellationToken) =>
{
    if (PortalStaticFileMapper.IsPortalPath(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Not found", cancellationToken);
        return;
    }

    var envelope = await ApiRequestEnvelopeFactory.CreateAsync(context, cancellationToken);
    app.Logger.LogInformation(
        "Protocol request received requestId={RequestId} traceId={TraceId} method={Method} host={Host} path={Path} " +
        "servicePrefix={ServicePrefix} operation={Operation} deviceId={DeviceId} firmwareVersion={FirmwareVersion} " +
        "applicationVersion={ApplicationVersion}",
        envelope.RequestId,
        context.TraceIdentifier,
        envelope.Method,
        envelope.HostName,
        envelope.Path,
        envelope.ServicePrefix,
        envelope.Operation,
        envelope.DeviceId,
        envelope.FirmwareVersion,
        envelope.ApplicationVersion);
    // The robot-facing OTA protocol is not an update-administration boundary.
    // Keep mutation APIs available to internal fixtures while denying them over public HTTP.
    var isPublicUpdateMutation =
        (envelope.ServicePrefix ?? string.Empty).StartsWith("Update_", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(envelope.Operation, "CreateUpdate", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(envelope.Operation, "RemoveUpdate", StringComparison.OrdinalIgnoreCase));
    var result = isPublicUpdateMutation
        ? ProtocolDispatchResult.Raw(StatusCodes.Status403Forbidden, """{"error":"forbidden"}""", "application/json")
        : await service.DispatchAsync(envelope, cancellationToken);
    if (isPublicUpdateMutation) result.Headers["Cache-Control"] = "no-store";
    transportMetrics.HttpPayload("in", "protocol", envelope.Method, result.StatusCode, envelope.BodyBytes?.Length ?? 0);
    transportMetrics.HttpPayload("out", "protocol", envelope.Method, result.StatusCode,
        Encoding.UTF8.GetByteCount(result.BodyText ?? string.Empty));
    try
    {
        await telemetrySink.RecordAsync(envelope, result, cancellationToken);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "HTTP telemetry recording failed for {Method} {Host}{Path}; returning response without telemetry.",
            envelope.Method,
            envelope.HostName,
            envelope.Path);
    }

    context.Response.StatusCode = result.StatusCode;
    context.Response.ContentType = result.ContentType;
    if (releaseSmokeAuthorization.IsSecretAuthorized(
            context.Request.Headers[ReleaseSmokeAuthorizationOptions.SecretHeaderName].ToString()))
    {
        var revision = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local";
        var replica = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
        context.Response.Headers[ReleaseSmokeAuthorizationOptions.ReplicaInstanceHeaderName] =
            $"{revision}/{replica}";
        context.Response.Headers[ReleaseSmokeAuthorizationOptions.ReplicaRevisionHeaderName] = revision;
    }

    app.Logger.LogInformation(
        "Protocol request completed requestId={RequestId} traceId={TraceId} operation={Operation} " +
        "deviceId={DeviceId} statusCode={StatusCode} contentType={ContentType} responseBytes={ResponseBytes}",
        envelope.RequestId,
        context.TraceIdentifier,
        envelope.Operation,
        envelope.DeviceId,
        result.StatusCode,
        result.ContentType,
        result.BodyText?.Length ?? 0);

    foreach (var header in result.Headers) context.Response.Headers[header.Key] = header.Value;

    if (!string.IsNullOrEmpty(result.BodyText)) await context.Response.WriteAsync(result.BodyText, cancellationToken);
});

app.Run();

return;

static bool ShouldResetDiagnosticsOnStartup(IConfiguration configuration)
{
    return bool.TryParse(configuration["OpenJibo:Logging:ResetOnStartup"], out var reset) && reset;
}

static void ConfigureOperationalMetrics(WebApplicationBuilder builder)
{
    var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    if (string.IsNullOrWhiteSpace(connectionString)) return;

    var revision = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local";
    var replica = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: "openjibo-cloud",
            serviceVersion: OpenJiboCloudBuildInfo.Version,
            serviceInstanceId: $"{revision}/{replica}"))
        .WithMetrics(metrics => metrics
            .AddMeter(TransportMetrics.MeterName)
            .AddMeter(AwsSigV4ReplayObservationPublisher.MeterName)
            .AddMeter("System.Runtime")
            .AddMeter("Npgsql")
            .AddAzureMonitorMetricExporter(options => options.ConnectionString = connectionString));
}

static void ResetDiagnosticsDirectories(IConfiguration configuration)
{
    var paths = new[]
    {
        ResolveConfiguredPath(configuration, "OpenJibo:Telemetry:DirectoryPath", "captures/websocket"),
        ResolveConfiguredPath(configuration, "OpenJibo:ProtocolTelemetry:DirectoryPath", "captures/http"),
        ResolveConfiguredPath(configuration, "OpenJibo:TurnTelemetry:DirectoryPath", "captures/turn"),
        ResolveConfiguredPath(configuration, "OpenJibo:Logging:DirectoryPath", "captures/logs")
    };

    foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // Startup cleanup is best-effort so a stale file never blocks the app.
        }
}

static string ResolveConfiguredPath(IConfiguration configuration, string key, string defaultPath)
{
    var configuredPath = configuration[key];
    if (string.IsNullOrWhiteSpace(configuredPath)) configuredPath = defaultPath;

    if (Path.IsPathRooted(configuredPath)) return Path.GetFullPath(configuredPath);

    var repoRoot = FindOpenJiboRepoRoot(Directory.GetCurrentDirectory()) ??
                   FindOpenJiboRepoRoot(AppContext.BaseDirectory) ??
                   Directory.GetCurrentDirectory();

    return Path.GetFullPath(configuredPath, repoRoot);
}

static string? FindOpenJiboRepoRoot(string? startPath)
{
    if (string.IsNullOrWhiteSpace(startPath)) return null;

    var directory = new DirectoryInfo(Path.GetFullPath(startPath));
    if (directory is { Exists: false, Parent: not null }) directory = directory.Parent;

    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "OpenJibo.slnx"))) return directory.FullName;

        directory = directory.Parent;
    }

    return null;
}

static LogEventLevel ParseLogEventLevel(string? value)
{
    return Enum.TryParse<LogEventLevel>(value, true, out var level)
        ? level
        : LogEventLevel.Debug;
}

public partial class Program;
