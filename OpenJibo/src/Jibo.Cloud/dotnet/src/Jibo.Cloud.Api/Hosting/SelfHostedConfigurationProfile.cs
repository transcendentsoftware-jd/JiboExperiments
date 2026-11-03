namespace Jibo.Cloud.Api.Hosting;

/// <summary>Public, allowlisted configuration facts; no state-store query or inventory.</summary>
internal sealed record SelfHostedConfigurationProfile(
    string Schema, string Service, string DeploymentMode, string StateBackend,
    string PersonalMemoryBackend, bool PersistentStorageConfigured,
    bool TokenlessSingleRobotHubConfigured, bool ConfigurationOnly = true,
    bool DatabaseConnectivityVerified = false, bool RobotCompatibilityVerified = false,
    bool ReadyForApply = false)
{
    internal static SelfHostedConfigurationProfile? FromConfiguration(IConfiguration configuration)
    {
        if (!string.Equals(configuration["OpenJibo:Deployment:Mode"], "self-hosted-isolated",
                StringComparison.OrdinalIgnoreCase))
            return null;

        static string Backend(string? value) => value?.ToLowerInvariant() switch
        {
            "postgresql" => "postgresql",
            "sqlite" => "sqlite",
            _ => "other-or-unspecified"
        };

        var state = Backend(configuration["OpenJibo:State:Backend"]);
        var memory = Backend(configuration["OpenJibo:PersonalMemory:Backend"]);
        var compatibility = bool.TryParse(configuration["OpenJibo:SelfHosted:AllowTokenlessSingleRobotHub"],
            out var enabled) && enabled;
        return new("openjibo.self-hosted-configuration.v1", "OpenJibo Cloud Api", "self-hosted-isolated",
            state, memory, state == "postgresql" && memory == "postgresql", compatibility);
    }
}
