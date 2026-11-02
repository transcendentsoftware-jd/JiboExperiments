using System.Security.Cryptography;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;

namespace Jibo.Cloud.Api.Hosting;

internal static class HubChannelLeaseRegistration
{
    internal static void AddHubChannelLeases(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new HubChannelLeaseOptions();
        configuration.GetSection(HubChannelLeaseOptions.SectionName).Bind(options);
        if (!options.Enabled)
        {
            services.AddSingleton(HubChannelLeasePolicy.Disabled);
            return;
        }
        // No fallback to the cloud-state or replay admission connection/key.
        services.AddSingleton<IHubChannelLeaseStore>(_ => new PostgreSqlHubChannelLeaseStore(options.ConnectionString ?? ""));
        services.AddSingleton(provider =>
        {
            byte[]? key = null;
            try
            {
                if (options.HmacKey is null || options.HmacKey.Length > 100) throw new ArgumentException("Lease key is missing.");
                key = WebEncoders.Base64UrlDecode(options.HmacKey);
                if (WebEncoders.Base64UrlEncode(key) != options.HmacKey) throw new ArgumentException("Lease key encoding is invalid.");
                return new HubChannelLeasePolicy(provider.GetRequiredService<IHubChannelLeaseStore>(), key);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
            {
                provider.GetService<ILoggerFactory>()?.CreateLogger("Jibo.Cloud.HubChannelLeases")
                    .LogWarning("Hub channel leases are unavailable because their dedicated configuration is invalid.");
                return HubChannelLeasePolicy.Unavailable;
            }
            finally
            {
                options.HmacKey = null;
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        });
    }
}
