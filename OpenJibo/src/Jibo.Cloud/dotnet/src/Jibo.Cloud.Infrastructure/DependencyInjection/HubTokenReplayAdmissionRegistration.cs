using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.DependencyInjection;

internal static class HubTokenReplayAdmissionRegistration
{
    internal static void Add(IServiceCollection services, IConfiguration? configuration)
    {
        var options = new HubTokenReplayAdmissionOptions();
        configuration?.GetSection(HubTokenReplayAdmissionOptions.SectionName).Bind(options);
        if (!options.Enabled)
        {
            services.AddSingleton<IHubTokenReplayAdmission>(
                new FixedHubTokenReplayAdmission(HubTokenReplayAdmissionOutcome.Disabled));
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(options.HmacKey) || string.IsNullOrWhiteSpace(options.ConnectionString))
                throw new ArgumentException("Strict admission configuration is incomplete.");
            var current = AwsSigV4ReplayDigestKey.FromBase64Url(options.HmacKey, options.KeyVersion);
            var hasPrevious = !string.IsNullOrWhiteSpace(options.PreviousHmacKey);
            if (hasPrevious != (options.PreviousKeyVersion > 0) || options.PreviousKeyVersion < 0)
                throw new ArgumentException("Previous admission key and version must be configured together.");
            var previous = hasPrevious
                ? AwsSigV4ReplayDigestKey.FromBase64Url(options.PreviousHmacKey!, options.PreviousKeyVersion) : null;
            // Validate key-slot combinations before adding either live registration.
            HubTokenReplayAdmission.ValidateKeySlots(current, previous);
            var connection = options.ConnectionString;
            services.AddSingleton<IHubTokenReplayAdmissionStore>(_ => new PostgreSqlHubTokenReplayAdmissionStore(connection));
            services.AddSingleton<IHubTokenReplayAdmission>(provider =>
            {
                try
                {
                    return new HubTokenReplayAdmission(provider.GetRequiredService<IHubTokenReplayAdmissionStore>(), current, previous);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    return Unavailable(provider);
                }
            });
        }
        catch (ArgumentException)
        {
            services.AddSingleton<IHubTokenReplayAdmission>(Unavailable);
        }
        finally
        {
            options.HmacKey = options.PreviousHmacKey = null;
        }
    }

    private static IHubTokenReplayAdmission Unavailable(IServiceProvider provider)
    {
        provider.GetService<ILoggerFactory>()?.CreateLogger("Jibo.Cloud.HubTokenReplayAdmission")
            .LogWarning("Strict Hub replay admission is unavailable because its dedicated configuration is invalid.");
        return new FixedHubTokenReplayAdmission(HubTokenReplayAdmissionOutcome.Unavailable);
    }

}
