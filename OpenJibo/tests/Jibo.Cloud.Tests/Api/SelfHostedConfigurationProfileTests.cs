using System.Net;
using System.Text.Json;
using Jibo.Cloud.Api.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Tests.Api;

public sealed class SelfHostedConfigurationProfileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("managed")]
    [InlineData("self-hosted-hybrid")]
    [InlineData("secret-unknown-mode")]
    public void OtherModes_DoNotExposeProfile(string? mode)
    {
        Assert.Null(SelfHostedConfigurationProfile.FromConfiguration(Config(mode)));
    }

    [Theory]
    [InlineData("PostgreSql", "postgresql", true)]
    [InlineData("Sqlite", "sqlite", false)]
    [InlineData("secret-backend-path", "other-or-unspecified", false)]
    [InlineData(null, "other-or-unspecified", false)]
    public void Profile_AllowListsConfigurationWithoutCertifyingIt(string? backend, string expected, bool persistent)
    {
        var config = Config("self-hosted-isolated", backend);
        var profile = SelfHostedConfigurationProfile.FromConfiguration(config)!;
        Assert.Equal(expected, profile.StateBackend);
        Assert.Equal(persistent, profile.PersistentStorageConfigured);
        Assert.True(profile.ConfigurationOnly);
        Assert.False(profile.DatabaseConnectivityVerified);
        Assert.False(profile.RobotCompatibilityVerified);
        Assert.False(profile.ReadyForApply);
        Assert.DoesNotContain("secret-", JsonSerializer.Serialize(profile));
    }

    [Fact]
    public async Task Endpoint_IsRedactedUnauthenticatedAndUncached()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenJibo:Deployment:Mode", "self-hosted-isolated");
            builder.UseSetting("OpenJibo:State:Backend", "Sqlite");
            builder.UseSetting("OpenJibo:State:ConnectionString", "Data Source=:memory:");
            builder.UseSetting("OpenJibo:PersonalMemory:Backend", "Sqlite");
            builder.UseSetting("OpenJibo:PersonalMemory:ConnectionString", "Data Source=:memory:");
            builder.UseSetting("OpenJibo:OwnerFirstName", "SECRET_OWNER");
            builder.UseSetting("OpenJibo:SelfHosted:AllowTokenlessSingleRobotHub", "true");
            builder.UseSetting("OpenJibo:Stt:EnableLocalWhisperCpp", "false");
            builder.UseSetting("OpenJibo:Stt:EnableWhisperServer", "false");
        });
        using var response = await factory.CreateClient().GetAsync("/health/self-hosted");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET_OWNER", raw);
        Assert.DoesNotContain("ConnectionString", raw);
        using var json = JsonDocument.Parse(raw);
        Assert.Equal("openjibo.self-hosted-configuration.v1", json.RootElement.GetProperty("schema").GetString());
        Assert.True(json.RootElement.GetProperty("tokenlessSingleRobotHubConfigured").GetBoolean());
        Assert.False(json.RootElement.GetProperty("databaseConnectivityVerified").GetBoolean());
        Assert.Equal(11, json.RootElement.EnumerateObject().Count());
    }

    private static IConfiguration Config(string? mode, string? backend = "PostgreSql") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenJibo:Deployment:Mode"] = mode,
            ["OpenJibo:State:Backend"] = backend,
            ["OpenJibo:PersonalMemory:Backend"] = backend,
            ["OpenJibo:State:ConnectionString"] = "secret-connection-string",
            ["OpenJibo:OwnerFirstName"] = "secret-owner"
        }).Build();
}
