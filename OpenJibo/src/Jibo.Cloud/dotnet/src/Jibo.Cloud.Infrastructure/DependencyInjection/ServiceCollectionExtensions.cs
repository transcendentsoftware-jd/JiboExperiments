using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Cloud.Infrastructure.Calendar;
using Jibo.Cloud.Infrastructure.Commute;
using Jibo.Cloud.Infrastructure.Conversions;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Dictionary;
using Jibo.Cloud.Infrastructure.FunFacts;
using Jibo.Cloud.Infrastructure.Holidays;
using Jibo.Cloud.Infrastructure.Media;
using Jibo.Cloud.Infrastructure.News;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Cloud.Infrastructure.Search;
using Jibo.Cloud.Infrastructure.Telemetry;
using Jibo.Cloud.Infrastructure.Weather;
using Jibo.Cloud.Infrastructure.Wikipedia;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

// ReSharper disable UnusedMethodReturnValue.Global

namespace Jibo.Cloud.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenJiboCloud(this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        var sttOptions = new BufferedAudioSttOptions();
        if (configuration is not null)
        {
            services.Configure<WebSocketTelemetryOptions>(configuration.GetSection("OpenJibo:Telemetry"));
            services.Configure<ProtocolTelemetryOptions>(configuration.GetSection("OpenJibo:ProtocolTelemetry"));
            services.Configure<TurnTelemetryOptions>(configuration.GetSection("OpenJibo:TurnTelemetry"));
            configuration.GetSection("OpenJibo:Stt").Bind(sttOptions);
        }

        var skipSttDependencyValidation = string.Equals(
            configuration?["OpenJibo:Testing:SkipSttDependencyValidation"],
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (!skipSttDependencyValidation)
            BufferedAudioSttPathResolver.ValidateResolvedDependencies(sttOptions);

        var openWeatherOptions = new OpenWeatherOptions();
        configuration?.GetSection("OpenJibo:Weather:OpenWeather").Bind(openWeatherOptions);

        if (string.IsNullOrWhiteSpace(openWeatherOptions.ApiKey))
            openWeatherOptions.ApiKey = Environment.GetEnvironmentVariable("OPENWEATHER_API_KEY");

        var newsApiOptions = new NewsApiOptions();
        configuration?.GetSection("OpenJibo:News:NewsApi").Bind(newsApiOptions);

        if (string.IsNullOrWhiteSpace(newsApiOptions.ApiKey))
            newsApiOptions.ApiKey = Environment.GetEnvironmentVariable("NEWSAPI_KEY");

        var holidayOptions = new HolidayCalendarOptions();
        configuration?.GetSection("OpenJibo:Holiday").Bind(holidayOptions);

        var uselessFactsOptions = new UselessFactsOptions();
        configuration?.GetSection("OpenJibo:FunFacts:UselessFacts").Bind(uselessFactsOptions);

        var freeDictionaryApiOptions = new FreeDictionaryApiOptions();
        configuration?.GetSection("OpenJibo:Dictionary:FreeDictionaryApi").Bind(freeDictionaryApiOptions);

        var wikipediaSummaryOptions = new WikipediaSummaryOptions();
        configuration?.GetSection("OpenJibo:Wikipedia").Bind(wikipediaSummaryOptions);

        var searchSection = configuration?.GetSection("OpenJibo:Search");
        var llmInstructions = SearchInstructionsResolver.Resolve(
            Environment.GetEnvironmentVariable("OPENJIBO_SEARCH_INSTRUCTIONS")
            ?? searchSection?["Instructions"],
            Environment.GetEnvironmentVariable("OPENJIBO_SEARCH_INSTRUCTIONS_FILE")
            ?? searchSection?["InstructionsFile"]);
        var searchBackendOptions = SearchBackendOptions.Create(
            Environment.GetEnvironmentVariable("OPENJIBO_SEARCH_BACKEND")
            ?? searchSection?["Primary"]
            ?? searchSection?["Backend"],
            Environment.GetEnvironmentVariable("OPENJIBO_SEARCH_FALLBACK")
            ?? searchSection?["Fallback"]
            ?? searchSection?["FallbackBackend"],
            searchSection?.GetValue("CacheTtlSeconds", 300) ?? 300,
            searchSection?.GetValue("FailureCacheTtlSeconds", 45) ?? 45,
            llmInstructions);

        services.AddSingleton(sttOptions);
        services.AddHostedService<WhisperServerHostedService>();
        services.AddSingleton(openWeatherOptions);
        services.AddSingleton(newsApiOptions);
        services.AddSingleton(holidayOptions);
        services.AddSingleton(uselessFactsOptions);
        services.AddSingleton(freeDictionaryApiOptions);
        services.AddSingleton(wikipediaSummaryOptions);
        services.AddSingleton(searchBackendOptions);
        services.AddHttpClient<IWeatherReportProvider, OpenWeatherReportProvider>();
        services.AddHttpClient<INewsBriefingProvider, NewsApiBriefingProvider>();
        services.AddHttpClient<IFunFactProvider, UselessFactsFunFactProvider>();
        services.AddHttpClient<IWordDefinitionProvider, FreeDictionaryApiDefinitionProvider>();
        services.AddHttpClient<IWikipediaSummaryProvider, WikipediaSummaryProvider>();
        services.AddHttpClient<WolframAlphaSearchProvider>();
        services.AddHttpClient<OllamaSearchProvider>();
        services.AddHttpClient<ChatGptSearchProvider>();
        services.AddSingleton<IKnowledgeSearchProvider, WolframAlphaSearchProvider>();
        services.AddSingleton<IKnowledgeSearchProvider, OllamaSearchProvider>();
        services.AddSingleton<IKnowledgeSearchProvider, ChatGptSearchProvider>();
        services.AddSingleton<IKnowledgeSearchService, KnowledgeSearchService>();
        services.AddSingleton<IHolidayCalendarProvider>(provider =>
            new NagerDateHolidayCalendarProvider(provider.GetRequiredService<HolidayCalendarOptions>()));
        services.AddSingleton<IHolidayCountdownCatalog>(_ =>
        {
            var catalogPath = Path.Combine(AppContext.BaseDirectory, "Content", "HolidayCountdownCatalog.json");
            return new HolidayCountdownCatalogLoader().LoadFromFile(catalogPath);
        });
        services.AddSingleton<IMeasurementConversionCatalog>(_ =>
        {
            var catalogPath = Path.Combine(AppContext.BaseDirectory, "Content", "MeasurementConversionCatalog.json");
            return new MeasurementConversionCatalogLoader().LoadFromFile(catalogPath);
        });
        services.AddHttpClient<IIcalFeedFetcher, IcalFeedFetcher>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        services.AddSingleton<IcalCalendarFeedInspector>();
        services.AddSingleton<CloudStateCalendarReportProvider>();
        services.AddSingleton<ICalendarReportProvider>(provider =>
            new IcalCalendarReportProvider(
                provider.GetRequiredService<IUserIntegrationStore>(),
                provider.GetRequiredService<ICloudStateStore>(),
                provider.GetRequiredService<IIcalFeedFetcher>(),
                provider.GetRequiredService<CloudStateCalendarReportProvider>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<IcalCalendarReportProvider>>()));
        services.AddSingleton<ICommuteReportProvider>(provider =>
            new CloudStateCommuteReportProvider(provider.GetRequiredService<ICloudStateStore>()));
        var statePersistencePath = configuration?["OpenJibo:State:PersistencePath"]
                                   ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "cloud-state.json");
        var personalMemoryPersistencePath = configuration?["OpenJibo:PersonalMemory:PersistencePath"]
                                            ?? Path.Combine(AppContext.BaseDirectory, "App_Data",
                                                "personal-memory.json");
        var stateBackendKind = ParseBackendKind(configuration?["OpenJibo:State:Backend"]);
        var personalMemoryBackendKind = ParseBackendKind(configuration?["OpenJibo:PersonalMemory:Backend"]);
        var stateConnectionString = configuration?["OpenJibo:State:ConnectionString"]
                                    ?? Environment.GetEnvironmentVariable("OPENJIBO_STATE_STORAGE_CONNECTION_STRING")
                                    ?? Environment.GetEnvironmentVariable("OPENJIBO_STATE_SQL_CONNECTION_STRING");
        var personalMemoryConnectionString = configuration?["OpenJibo:PersonalMemory:ConnectionString"]
                                             ?? Environment.GetEnvironmentVariable(
                                                 "OPENJIBO_PERSONAL_MEMORY_STORAGE_CONNECTION_STRING")
                                             ?? Environment.GetEnvironmentVariable(
                                                 "OPENJIBO_PERSONAL_MEMORY_SQL_CONNECTION_STRING");
        var ownerFirstName = configuration?["OpenJibo:OwnerFirstName"];
        var ownerLastName = configuration?["OpenJibo:OwnerLastName"];
        switch (stateBackendKind)
        {
            case PersistenceBackendKind.Sqlite when string.IsNullOrWhiteSpace(stateConnectionString):
            {
                var dbPath = Path.ChangeExtension(statePersistencePath, ".db");
                stateConnectionString = $"Data Source={dbPath}";
                break;
            }
            case PersistenceBackendKind.PostgreSql when string.IsNullOrWhiteSpace(stateConnectionString):
                stateConnectionString = BuildPostgreSqlConnectionString("openjibo_state");
                break;
        }

        switch (personalMemoryBackendKind)
        {
            case PersistenceBackendKind.Sqlite when
                string.IsNullOrWhiteSpace(personalMemoryConnectionString):
            {
                var dbPath = Path.ChangeExtension(personalMemoryPersistencePath, ".db");
                personalMemoryConnectionString = $"Data Source={dbPath}";
                break;
            }
            case PersistenceBackendKind.PostgreSql when
                string.IsNullOrWhiteSpace(personalMemoryConnectionString):
                personalMemoryConnectionString = BuildPostgreSqlConnectionString("openjibo_memory");
                break;
        }

        var mediaOptions = new MediaContentStoreOptions();
        configuration?.GetSection("OpenJibo:Media").Bind(mediaOptions);

        if (string.IsNullOrWhiteSpace(mediaOptions.ConnectionString))
            mediaOptions.ConnectionString =
                Environment.GetEnvironmentVariable("OPENJIBO_MEDIA_STORAGE_CONNECTION_STRING");

        services.AddSingleton<IPersistenceSnapshotStoreFactory, PersistenceSnapshotStoreFactory>();
        services.AddSingleton<IMediaContentStoreFactory, MediaContentStoreFactory>();
        var userIntegrationsPersistencePath = configuration?["OpenJibo:UserIntegrations:PersistencePath"]
                                              ?? Path.Combine(AppContext.BaseDirectory, "App_Data",
                                                  "user-integrations.json");
        services.AddSingleton<UserDataEncryptionService>();
        services.AddSingleton(provider =>
            new EncryptedUserDataSnapshotStore(
                userIntegrationsPersistencePath,
                provider.GetRequiredService<UserDataEncryptionService>()));
        services.AddSingleton<IUserIntegrationStore>(provider =>
            new InMemoryUserIntegrationStore(provider.GetRequiredService<EncryptedUserDataSnapshotStore>(),
                provider.GetRequiredService<ICloudStateStore>()));
        if (stateBackendKind == PersistenceBackendKind.PostgreSql)
        {
            var maxPoolSize = configuration?.GetValue<int?>("OpenJibo:State:PostgreSql:MaxPoolSize") ?? 8;
            var deviceCacheMaxEntries = configuration?.GetValue<int?>(
                                            "OpenJibo:State:Cache:MaxEntries") ?? 256;
            var deviceCacheTtlSeconds = configuration?.GetValue<int?>(
                                            "OpenJibo:State:Cache:TtlSeconds") ?? 30;
            var maximumActiveSessions = configuration?.GetValue<int?>(
                                            "OpenJibo:State:Sessions:MaximumActive") ?? 256;
            services.AddSingleton(provider =>
            {
                var boundedMaxPoolSize = Math.Max(1, maxPoolSize);
                provider.GetRequiredService<ITransportMetrics>()
                    .PostgreSqlPoolConfigured("cloud_state", boundedMaxPoolSize);
                return new PostgreSqlCloudStateDataSource(stateConnectionString!, boundedMaxPoolSize);
            });
            services.AddSingleton<ICloudStateSecretProtector>(provider =>
                new UserDataCloudStateSecretProtector(provider.GetRequiredService<UserDataEncryptionService>()));
            services.AddSingleton<IRobotIdentitySuggestionRepository,
                PostgreSqlRobotIdentitySuggestionRepository>();
            services.AddSingleton<IBackupPayloadStore, MediaContentBackupPayloadStore>();
            services.AddSingleton<ICloudStateStore>(provider => new PostgreSqlCloudStateStore(
                provider.GetRequiredService<PostgreSqlCloudStateDataSource>(),
                provider.GetRequiredService<ICloudStateSecretProtector>(),
                Math.Max(1, deviceCacheMaxEntries),
                TimeSpan.FromSeconds(Math.Max(1, deviceCacheTtlSeconds)),
                Math.Max(1, maximumActiveSessions),
                backupPayloadStore: provider.GetRequiredService<IBackupPayloadStore>(),
                ownerFirstName: ownerFirstName,
                ownerLastName: ownerLastName,
                transportMetrics: provider.GetRequiredService<ITransportMetrics>(),
                trustedCredentials: provider.GetRequiredService<TrustedProtocolCredentialRegistry>()));
        }
        else
        {
            services.AddSingleton<ICloudStateStore>(provider =>
            {
                var snapshotFactory = provider.GetRequiredService<IPersistenceSnapshotStoreFactory>();
                var holidayCalendarProvider = provider.GetRequiredService<IHolidayCalendarProvider>();
                return new InMemoryCloudStateStore(
                    snapshotFactory.Create(statePersistencePath, stateBackendKind, "cloud-state",
                        stateConnectionString),
                    holidayCalendarProvider,
                    ownerFirstName,
                    ownerLastName,
                    provider.GetRequiredService<ITransportMetrics>(),
                    provider.GetRequiredService<TrustedProtocolCredentialRegistry>());
            });
        }

        var replayObservationOptions = new AwsSigV4ReplayObservationOptions();
        configuration?.GetSection("OpenJibo:Security:SigV4ReplayObservation")
            .Bind(replayObservationOptions);
        replayObservationOptions.HmacKey ??=
            configuration?["OpenJibo:Security:SigV4ReplayHmacKey"];
        replayObservationOptions.ConnectionString ??=
            configuration?["OpenJibo:Security:SigV4ReplayObservation:ConnectionString"];

        AwsSigV4ReplayDigestKey? replayDigestKey = null;
        AwsSigV4ReplayDigestKey? previousReplayDigestKey = null;
        if (replayObservationOptions.Enabled &&
            !string.IsNullOrWhiteSpace(replayObservationOptions.HmacKey) &&
            !string.IsNullOrWhiteSpace(replayObservationOptions.ConnectionString))
        {
            try
            {
                replayDigestKey = AwsSigV4ReplayDigestKey.FromBase64Url(
                    replayObservationOptions.HmacKey,
                    replayObservationOptions.KeyVersion);
                var hasPreviousKey = !string.IsNullOrWhiteSpace(replayObservationOptions.PreviousHmacKey);
                if (replayObservationOptions.PreviousKeyVersion < 0)
                    throw new ArgumentOutOfRangeException(
                        nameof(replayObservationOptions.PreviousKeyVersion));
                var hasPreviousVersion = replayObservationOptions.PreviousKeyVersion > 0;
                if (hasPreviousKey != hasPreviousVersion)
                    throw new ArgumentException(
                        "Previous replay HMAC key and version must be configured together.");
                if (hasPreviousKey)
                {
                    previousReplayDigestKey = AwsSigV4ReplayDigestKey.FromBase64Url(
                        replayObservationOptions.PreviousHmacKey!,
                        replayObservationOptions.PreviousKeyVersion);
                    if (previousReplayDigestKey.Version == replayDigestKey.Version)
                        throw new ArgumentException(
                            "Current and previous replay HMAC key versions must differ.");
                    if (previousReplayDigestKey.HasSameMaterial(replayDigestKey))
                        throw new ArgumentException(
                            "Current and previous replay HMAC keys must use different material.");
                }
            }
            catch (ArgumentException)
            {
                replayDigestKey = null;
                previousReplayDigestKey = null;
            }
        }

        if (replayDigestKey is not null)
        {
            services.AddSingleton(replayDigestKey);
            services.AddSingleton(provider =>
            {
                const int boundedMaxPoolSize = 1;
                provider.GetRequiredService<ITransportMetrics>()
                    .PostgreSqlPoolConfigured("sigv4_replay_observer", boundedMaxPoolSize);
                return new PostgreSqlAwsSigV4ReplayDataSource(
                    replayObservationOptions.ConnectionString!);
            });
            services.AddSingleton<IAwsSigV4ReplayObservationStore,
                PostgreSqlAwsSigV4ReplayObservationStore>();
            services.AddSingleton(provider => new AwsSigV4ReplayObservationPublisher(
                provider.GetRequiredService<IAwsSigV4ReplayObservationStore>(),
                provider.GetRequiredService<AwsSigV4ReplayDigestKey>(),
                replayObservationOptions.Capacity,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<
                    AwsSigV4ReplayObservationPublisher>>(),
                previousReplayDigestKey));
            services.AddSingleton<IAwsSigV4ReplayObservationPublisher>(provider =>
                provider.GetRequiredService<AwsSigV4ReplayObservationPublisher>());
            services.AddHostedService(provider =>
                provider.GetRequiredService<AwsSigV4ReplayObservationPublisher>());
        }
        else
        {
            services.AddSingleton<IAwsSigV4ReplayObservationPublisher>(provider =>
            {
                if (replayObservationOptions.Enabled)
                {
                    provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
                        .CreateLogger("Jibo.Cloud.SigV4ReplayObservation")
                        .LogWarning(
                            "Legacy SigV4 replay observation is disabled because PostgreSQL, a dedicated observer connection, or a valid HMAC key is unavailable.");
                }

                return NullAwsSigV4ReplayObservationPublisher.Instance;
            });
        }

        var releaseSmokeAuthorization = new ReleaseSmokeAuthorizationOptions();
        configuration?.GetSection("OpenJibo:ReleaseSmoke").Bind(releaseSmokeAuthorization);
        services.AddSingleton(releaseSmokeAuthorization);
        if (replayDigestKey is not null)
            services.AddSingleton<AwsSigV4ReplayProbeProofService>();
        var runtimePairingAttestationOptions = new RuntimePairingAttestationIssuerOptions();
        configuration?.GetSection(RuntimePairingAttestationIssuerOptions.SectionName)
            .Bind(runtimePairingAttestationOptions);
        Es256RuntimePairingAttestationIssuer.TryCreate(
            runtimePairingAttestationOptions,
            TimeProvider.System,
            out var runtimePairingAttestationIssuer);
        // Do not make the raw private-key option object injectable or retain it in
        // the registration closure after the signer has imported the key.
        runtimePairingAttestationOptions.PrivateKeyPem = string.Empty;
        services.AddSingleton<IRuntimePairingAttestationIssuer>(
            _ => runtimePairingAttestationIssuer);
        HubTokenReplayAdmissionRegistration.Add(services, configuration);
        var pairingEvidenceOptions = new RuntimePairingCredentialEvidenceOptions();
        configuration?.GetSection(RuntimePairingCredentialEvidenceOptions.SectionName).Bind(pairingEvidenceOptions);
        var pairingEvidenceKey = RuntimePairingCredentialEvidenceKey.Create(pairingEvidenceOptions);
        pairingEvidenceOptions.HmacKey = null;
        services.AddSingleton<RuntimePairingCredentialEvidenceKey>(_ => pairingEvidenceKey);
        var trustedCredentialOptions = new TrustedProtocolCredentialRegistryOptions();
        try
        {
            configuration?.GetSection(TrustedProtocolCredentialRegistryOptions.SectionName).Bind(trustedCredentialOptions);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
        {
            // Do not echo raw option values or retain a binder exception as an inner exception.
            throw new InvalidOperationException("Trusted protocol credential configuration is incomplete or invalid.");
        }
        var trustedCredentials = new TrustedProtocolCredentialRegistry(trustedCredentialOptions, pairingEvidenceKey);
        trustedCredentialOptions.Credentials = null;
        if (trustedCredentials.Required && !trustedCredentials.IsReady)
            throw new InvalidOperationException("Trusted protocol credential configuration is incomplete or invalid.");
        services.AddSingleton<TrustedProtocolCredentialRegistry>(_ => trustedCredentials);
        services.AddSingleton<AwsSigV4RequestVerifier>();
        Jibo.Cloud.Infrastructure.ManagedPairing.RuntimePairingInitiationClientRegistration.Add(services, configuration);
        Jibo.Cloud.Infrastructure.ManagedPairing.RuntimeAccessClientRegistration.Add(services,configuration);
        services.AddSingleton<ICloudAuthProtocolHandler, CloudAuthProtocolHandler>();
        services.AddSingleton<IPersonalMemoryStore>(provider =>
        {
            if (personalMemoryBackendKind == PersistenceBackendKind.PostgreSql)
            {
                var maxPoolSize = configuration?.GetValue<int?>(
                                      "OpenJibo:PersonalMemory:PostgreSql:MaxPoolSize") ?? 4;
                var cacheMaxEntries = configuration?.GetValue<int?>(
                                          "OpenJibo:PersonalMemory:Cache:MaxEntries") ?? 256;
                var cacheTtlSeconds = configuration?.GetValue<int?>(
                                          "OpenJibo:PersonalMemory:Cache:TtlSeconds") ?? 30;
                var boundedMaxPoolSize = Math.Max(1, maxPoolSize);
                var metrics = provider.GetRequiredService<ITransportMetrics>();
                metrics.PostgreSqlPoolConfigured("personal_memory", boundedMaxPoolSize);
                return new PostgreSqlPersonalMemoryStore(
                    personalMemoryConnectionString!,
                    boundedMaxPoolSize,
                    cacheMaxEntries,
                    TimeSpan.FromSeconds(Math.Max(1, cacheTtlSeconds)),
                    metrics);
            }

            var snapshotFactory = provider.GetRequiredService<IPersistenceSnapshotStoreFactory>();
            return new InMemoryPersonalMemoryStore(snapshotFactory.Create(personalMemoryPersistencePath,
                personalMemoryBackendKind, "personal-memory", personalMemoryConnectionString));
        });
        services.AddSingleton<IJiboExperienceContentRepository, InMemoryJiboExperienceContentRepository>();
        services.AddSingleton<JiboExperienceContentCache>();
        services.AddSingleton<IJiboRandomizer, DefaultJiboRandomizer>();
        services.AddSingleton<ITurnProgressPublisher, AmbientTurnProgressPublisher>();
        services.AddSingleton<RepeatLastCommandStore>();
        services.AddSingleton<JiboInteractionService>();
        services.AddSingleton<IConversationBroker, DemoConversationBroker>();
        services.AddSingleton<IExternalProcessRunner, ExternalProcessRunner>();
        services.AddSingleton<ISttStrategy, SyntheticBufferedAudioSttStrategy>();
        services.AddHttpClient<AzureSpeechBufferedAudioSttStrategy>();
        services.AddSingleton<ISttStrategy>(provider =>
            provider.GetRequiredService<AzureSpeechBufferedAudioSttStrategy>());
        services.AddHttpClient<WhisperServerBufferedAudioSttStrategy>();
        services.AddSingleton<ISttStrategy>(provider =>
            provider.GetRequiredService<WhisperServerBufferedAudioSttStrategy>());
        services.AddSingleton<ISttStrategy, LocalWhisperCppBufferedAudioSttStrategy>();
        services.AddSingleton<ISttStrategySelector, DefaultSttStrategySelector>();
        services.AddSingleton<IWebSocketTelemetrySink, FileWebSocketTelemetrySink>();
        services.AddSingleton<ITransportMetrics, TransportMetrics>();
        services.AddSingleton<IProtocolTelemetrySink, FileProtocolTelemetrySink>();
        services.AddSingleton<ITurnTelemetrySink, FileTurnTelemetrySink>();
        services.AddSingleton<IMediaContentStore>(provider =>
        {
            var factory = provider.GetRequiredService<IMediaContentStoreFactory>();
            return factory.Create(mediaOptions.DirectoryPath, mediaOptions.Backend, mediaOptions.ContainerName,
                mediaOptions.ConnectionString);
        });
        services.AddSingleton<ProtocolToTurnContextMapper>();
        services.AddSingleton<ResponsePlanToSocketMessagesMapper>();
        services.AddSingleton<WebSocketTurnFinalizationService>();
        services.AddSingleton<ProactiveTransactionHandler>();
        services.AddSingleton<JiboCloudProtocolService>();
        services.AddSingleton<JiboWebSocketService>();
        services.AddSingleton<JiboVerificationService>();
        services.AddSingleton<PortalSessionService>();
        services.AddSingleton<HomeAssistantConnectionRegistry>();
        services.AddSingleton<HomeAssistantPendingClimateStore>();
        services.AddSingleton<RepeatLastCommandStore>();
        services.AddSingleton<RobotPendingNotificationStore>();
        services.AddSingleton<RobotNotificationRegistry>();
        services.AddSingleton<RobotPresenceRegistry>();
        services.AddSingleton(provider => new RobotIdentitySuggestionStore(
            provider.GetRequiredService<ICloudStateStore>(),
            provider.GetService<IRobotIdentitySuggestionRepository>()));
        services.AddSingleton(provider => new OpenJiboServerIdentity(
            configuration?["OpenJibo:CanonicalApiHostname"]));
        services.AddSingleton<FleetNetworkPresenceRegistry>();
        services.AddSingleton<LoopUpdatedPushService>();
        services.AddSingleton<HomeAssistantCommandService>();

        return services;
    }

    private static PersistenceBackendKind ParseBackendKind(string? value)
    {
        return Enum.TryParse<PersistenceBackendKind>(value, true, out var backendKind)
            ? backendKind
            : PersistenceBackendKind.Sqlite;
    }

    private static string BuildPostgreSqlConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_HOST") ?? "postgres",
            Port = int.TryParse(Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_PORT"), out var port)
                ? port
                : 5432,
            Database = databaseName,
            Username = Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_USER") ?? "openjibo"
        };

        var password = Environment.GetEnvironmentVariable("OPENJIBO_POSTGRES_PASSWORD");
        if (!string.IsNullOrWhiteSpace(password))
            builder.Password = password;

        return builder.ConnectionString;
    }
}
