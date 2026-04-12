using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Infrastructure.Telemetry;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenJiboCloud(this IServiceCollection services, IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<WebSocketTelemetryOptions>(configuration.GetSection("OpenJibo:Telemetry"));
        }

        services.AddSingleton<ICloudStateStore, InMemoryCloudStateStore>();
        services.AddSingleton<IConversationBroker, DemoConversationBroker>();
        services.AddSingleton<ISttStrategy, SyntheticBufferedAudioSttStrategy>();
        services.AddSingleton<ISttStrategySelector, DefaultSttStrategySelector>();
        services.AddSingleton<IWebSocketTelemetrySink, FileWebSocketTelemetrySink>();
        services.AddSingleton<ProtocolToTurnContextMapper>();
        services.AddSingleton<ResponsePlanToSocketMessagesMapper>();
        services.AddSingleton<WebSocketTurnFinalizationService>();
        services.AddSingleton<JiboCloudProtocolService>();
        services.AddSingleton<JiboWebSocketService>();

        return services;
    }
}
