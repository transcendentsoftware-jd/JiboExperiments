using System.Net;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Tests.Api;

public sealed class RuntimeMaintenanceModeTests
{
    private static WebApplicationFactory<Program> Factory(string value = "true") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenJibo:Maintenance:Enabled", value);
            // Closed startup must not attempt to initialize these profiles.
            builder.UseSetting("OpenJibo:State:Backend", "PostgreSql");
            builder.UseSetting("OpenJibo:State:ConnectionString", "deliberately-invalid");
            builder.UseSetting("OpenJibo:PersonalMemory:Backend", "PostgreSql");
            builder.UseSetting("OpenJibo:PersonalMemory:ConnectionString", "deliberately-invalid");
        });

    [Theory]
    [InlineData("GET", "/health/live", 200)]
    [InlineData("HEAD", "/health/live", 200)]
    [InlineData("GET", "/health/ready", 503)]
    [InlineData("HEAD", "/health/ready", 503)]
    [InlineData("GET", "/health", 404)]
    [InlineData("GET", "/", 404)]
    [InlineData("POST", "/", 404)]
    [InlineData("POST", "/health/live", 404)]
    [InlineData("GET", "/listen", 404)]
    [InlineData("GET", "/v1/proactive", 404)]
    [InlineData("GET", "/api/portal/status", 404)]
    [InlineData("GET", "/index.html", 404)]
    public async Task Closed_profile_has_only_liveness_and_unready_health_without_runtime_services(string method, string path, int status)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        // A privileged-looking header cannot open a closed listener.
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-token");
        using var response = await client.SendAsync(request);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(factory.Services.GetService<ICloudStateStore>());
        Assert.Null(factory.Services.GetService<IPersonalMemoryStore>());
        Assert.Null(factory.Services.GetService<IRuntimeAccessClient>());
        if (method == "HEAD" || status != 200) Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("malformed")]
    public void Invalid_maintenance_configuration_refuses_startup(string value)
    {
        using var factory = Factory(value);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("maintenance", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
