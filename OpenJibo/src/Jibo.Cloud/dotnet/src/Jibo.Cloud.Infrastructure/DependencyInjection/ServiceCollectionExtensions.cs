using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenJiboCloud(this IServiceCollection services)
    {
        services.AddSingleton<ICloudStateStore, InMemoryCloudStateStore>();
        services.AddSingleton<IConversationBroker, DemoConversationBroker>();
        services.AddSingleton<ISttStrategy, SyntheticBufferedAudioSttStrategy>();
        services.AddSingleton<ISttStrategySelector, DefaultSttStrategySelector>();
        services.AddSingleton<ProtocolToTurnContextMapper>();
        services.AddSingleton<ResponsePlanToSocketMessagesMapper>();
        services.AddSingleton<WebSocketTurnFinalizationService>();
        services.AddSingleton<JiboCloudProtocolService>();
        services.AddSingleton<JiboWebSocketService>();

        return services;
    }
}
