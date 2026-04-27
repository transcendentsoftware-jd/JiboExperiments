using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Cloud.Infrastructure.Content;
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
        var sttOptions = new BufferedAudioSttOptions();
        if (configuration is not null)
        {
            services.Configure<WebSocketTelemetryOptions>(configuration.GetSection("OpenJibo:Telemetry"));
            services.Configure<ProtocolTelemetryOptions>(configuration.GetSection("OpenJibo:ProtocolTelemetry"));
            services.Configure<TurnTelemetryOptions>(configuration.GetSection("OpenJibo:TurnTelemetry"));
            configuration.GetSection("OpenJibo:Stt").Bind(sttOptions);
        }

        services.AddSingleton(sttOptions);
        var statePersistencePath = configuration?["OpenJibo:State:PersistencePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "cloud-state.json");
        services.AddSingleton<ICloudStateStore>(_ => new InMemoryCloudStateStore(statePersistencePath));
        services.AddSingleton<IJiboExperienceContentRepository, InMemoryJiboExperienceContentRepository>();
        services.AddSingleton<JiboExperienceContentCache>();
        services.AddSingleton<IJiboRandomizer, DefaultJiboRandomizer>();
        services.AddSingleton<JiboInteractionService>();
        services.AddSingleton<IConversationBroker, DemoConversationBroker>();
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton<ISttStrategy, SyntheticBufferedAudioSttStrategy>();
        services.AddSingleton<ISttStrategy, LocalWhisperCppBufferedAudioSttStrategy>();
        services.AddSingleton<ISttStrategySelector, DefaultSttStrategySelector>();
        services.AddSingleton<IWebSocketTelemetrySink, FileWebSocketTelemetrySink>();
        services.AddSingleton<IProtocolTelemetrySink, FileProtocolTelemetrySink>();
        services.AddSingleton<ITurnTelemetrySink, FileTurnTelemetrySink>();
        services.AddSingleton<ProtocolToTurnContextMapper>();
        services.AddSingleton<ResponsePlanToSocketMessagesMapper>();
        services.AddSingleton<WebSocketTurnFinalizationService>();
        services.AddSingleton<JiboCloudProtocolService>();
        services.AddSingleton<JiboWebSocketService>();

        return services;
    }
}
