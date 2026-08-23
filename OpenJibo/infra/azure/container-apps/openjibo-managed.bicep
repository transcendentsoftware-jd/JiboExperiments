targetScope = 'resourceGroup'

@description('Azure region for the managed deployment.')
param location string = resourceGroup().location

@description('Workload name segment used in Azure resource names.')
param workloadName string = 'openjibo'

@description('Deployment environment name segment used in Azure resource names.')
param environmentName string = 'managed'

@description('Name of the Container Apps environment.')
param managedEnvironmentName string = 'cae-${workloadName}-${environmentName}'

@description('Name of the Azure Container Apps resource.')
param containerAppName string = 'ca-${workloadName}-${environmentName}'

@description('Login server for Azure Container Registry, for example myregistry.azurecr.io.')
param registryLoginServer string

@description('Name of the Key Vault that stores managed secrets.')
param keyVaultName string

@description('Tag for the managed Open Jibo image.')
param imageTag string = 'managed'

@description('Canonical robot-facing hosted API hostname. This should match the hostname written by the robot conversion helpers.')
param apiHostname string = 'api.openjibo.com'

@description('Canonical robot-facing socket hostname for the notification subsystem. This should match the hostname derived by the robot conversion helpers.')
param socketHostname string = 'open-jibo-socket.openjibo.com'

@description('Canonical robot-facing neo-hub hostname for listen and proactive traffic.')
param neoHubHostname string = 'neohub.openjibo.com'

@description('Compatibility API hostname produced by the native libJiboServerService.so region suffix patch.')
param nativeCompatibilityApiHostname string = 'open-jibo.jibo.pro'

@description('Compatibility socket hostname produced when native code uses the patched socket suffix.')
param nativeCompatibilitySocketHostname string = 'open-jibo-socket.jibo.pro'

@description('Enables Azure Speech STT for hosted deployments.')
param enableAzureSpeech bool = true

@description('Azure Speech region used when Azure Speech STT is enabled.')
param azureSpeechRegion string = location

@description('Azure Speech subscription key used when Azure Speech STT is enabled.')
@secure()
param azureSpeechSubscriptionKey string = ''

@description('Managed PostgreSQL state connection string used by the runtime.')
@secure()
param stateConnectionString string = ''

@description('Managed PostgreSQL personal memory connection string used by the runtime.')
@secure()
param personalMemoryConnectionString string = ''

@description('Managed storage connection string used by the runtime media store.')
@secure()
param mediaConnectionString string = ''

@description('OpenWeather API key used by the runtime.')
@secure()
param openWeatherApiKey string = ''

@description('NewsAPI key used by the runtime.')
@secure()
param newsApiKey string = ''

@description('Knowledge search backend configuration used by the runtime. Expected format is backend!credential!model, where model is optional.')
@secure()
param searchBackend string = ''

@description('Optional fallback knowledge search backend configuration used by the runtime.')
@secure()
param searchFallback string = ''

@description('Password used to gate the OpenJibo admin status page.')
@secure()
param portalStatusPassword string = ''

@description('Shared HMAC key used by trusted OpenJibo servers to exchange fleet presence reports.')
@secure()
param peerSyncSharedKey string = ''

@secure()
@description('Passphrase used to protect normalized durable secrets.')
param userEncryptionPassphrase string = ''

@secure()
@description('Salt used to derive the normalized durable-secret encryption key.')
param userEncryptionSalt string = ''

@description('Minimum number of replicas for the runtime container.')
param minReplicas int = 1

@description('Maximum number of replicas for the runtime container.')
param maxReplicas int = 2

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' existing = {
  name: 'log-${workloadName}-${environmentName}'
}

var registryName = split(registryLoginServer, '.')[0]
var canonicalApiBaseUrl = 'https://${apiHostname}'
var canonicalSocketBaseUrl = 'https://${socketHostname}'
var canonicalNeoHubBaseUrl = 'https://${neoHubHostname}'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

var logAnalyticsWorkspaceKey = logAnalyticsWorkspace.listKeys().primarySharedKey
var registryCredentials = registry.listCredentials()

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

var azureSpeechSecretEntries = enableAzureSpeech ? [
  {
    name: 'azure-speech-subscription-key'
    value: azureSpeechSubscriptionKey
  }
] : []
var searchSecretEntries = !empty(searchBackend) ? [
  {
    name: 'search-backend'
    value: searchBackend
  }
] : []
var searchFallbackSecretEntries = !empty(searchFallback) ? [
  {
    name: 'search-fallback'
    value: searchFallback
  }
] : []
var managedSecrets = concat([
  {
    name: 'acr-password'
    value: registryCredentials.passwords[0].value
  }
], azureSpeechSecretEntries, searchSecretEntries, searchFallbackSecretEntries, [
  {
    name: 'state-connection-string'
    value: stateConnectionString
  }
  {
    name: 'personal-memory-connection-string'
    value: personalMemoryConnectionString
  }
  {
    name: 'media-connection-string'
    value: mediaConnectionString
  }
  {
    name: 'open-weather-api-key'
    value: openWeatherApiKey
  }
  {
    name: 'news-api-key'
    value: newsApiKey
  }
  {
    name: 'portal-status-password'
    value: portalStatusPassword
  }
  {
    name: 'peer-sync-shared-key'
    value: peerSyncSharedKey
  }
  {
    name: 'user-encryption-passphrase'
    value: userEncryptionPassphrase
  }
  {
    name: 'user-encryption-salt'
    value: userEncryptionSalt
  }
])
var azureSpeechEnvEntries = enableAzureSpeech ? [
  {
    name: 'OpenJibo__Stt__EnableAzureSpeech'
    value: 'true'
  }
  {
    name: 'OpenJibo__Stt__FfmpegPath'
    value: '/usr/bin/ffmpeg'
  }
  {
    name: 'OpenJibo__Stt__AzureSpeechRegion'
    value: azureSpeechRegion
  }
  {
    name: 'OpenJibo__Stt__AzureSpeechSubscriptionKey'
    secretRef: 'azure-speech-subscription-key'
  }
] : []
var searchEnvEntries = !empty(searchBackend) ? [
  {
    name: 'OPENJIBO_SEARCH_BACKEND'
    secretRef: 'search-backend'
  }
] : []
var searchFallbackEnvEntries = !empty(searchFallback) ? [
  {
    name: 'OPENJIBO_SEARCH_FALLBACK'
    secretRef: 'search-fallback'
  }
] : []
var managedEnvVars = concat([
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'OpenJibo__Telemetry__Enabled'
    value: 'false'
  }
  {
    name: 'OpenJibo__Telemetry__ExportFixtures'
    value: 'false'
  }
  {
    name: 'OpenJibo__ProtocolTelemetry__Enabled'
    value: 'false'
  }
  {
    name: 'OpenJibo__ProtocolAuthDiagnostics__Enabled'
    value: 'false'
  }
  {
    name: 'OpenJibo__TurnTelemetry__Enabled'
    value: 'false'
  }
  {
    name: 'ASPNETCORE_URLS'
    value: 'http://+:8080'
  }
  {
    name: 'OpenJibo__CanonicalApiHostname'
    value: apiHostname
  }
  {
    name: 'OpenJibo__CanonicalApiBaseUrl'
    value: canonicalApiBaseUrl
  }
  {
    name: 'OpenJibo__CanonicalSocketHostname'
    value: socketHostname
  }
  {
    name: 'OpenJibo__CanonicalNeoHubHostname'
    value: neoHubHostname
  }
  {
    name: 'OpenJibo__NativeCompatibilityApiHostname'
    value: nativeCompatibilityApiHostname
  }
  {
    name: 'OpenJibo__NativeCompatibilitySocketHostname'
    value: nativeCompatibilitySocketHostname
  }
  {
    name: 'OpenJibo__State__Backend'
    value: 'PostgreSql'
  }
  {
    name: 'OpenJibo__PersonalMemory__Backend'
    value: 'PostgreSql'
  }
  {
    name: 'OpenJibo__Media__Backend'
    value: 'AzureBlob'
  }
  {
    name: 'OpenJibo__State__ConnectionString'
    secretRef: 'state-connection-string'
  }
  {
    name: 'OPENJIBO_STATE_STORAGE_CONNECTION_STRING'
    secretRef: 'state-connection-string'
  }
  {
    name: 'OpenJibo__PersonalMemory__ConnectionString'
    secretRef: 'personal-memory-connection-string'
  }
  {
    name: 'OPENJIBO_PERSONAL_MEMORY_STORAGE_CONNECTION_STRING'
    secretRef: 'personal-memory-connection-string'
  }
  {
    name: 'OpenJibo__Media__ConnectionString'
    secretRef: 'media-connection-string'
  }
  {
    name: 'OPENJIBO_MEDIA_STORAGE_CONNECTION_STRING'
    secretRef: 'media-connection-string'
  }
  {
    name: 'OPENWEATHER_API_KEY'
    secretRef: 'open-weather-api-key'
  }
  {
    name: 'NEWSAPI_KEY'
    secretRef: 'news-api-key'
  }
  {
    name: 'OpenJibo__Portal__StatusPassword'
    secretRef: 'portal-status-password'
  }
  {
    name: 'OpenJibo__FleetNetwork__PeerSyncSharedKey'
    secretRef: 'peer-sync-shared-key'
  }
  {
    name: 'OPENJIBO_USER_ENCRYPT'
    secretRef: 'user-encryption-passphrase'
  }
  {
    name: 'OPENJIBO_USER_SALT'
    secretRef: 'user-encryption-salt'
  }
], azureSpeechEnvEntries, searchEnvEntries, searchFallbackEnvEntries)

resource managedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: managedEnvironmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspaceKey
      }
    }
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: [
          {
            latestRevision: true
            weight: 100
          }
        ]
      }
      registries: [
        {
          server: registryLoginServer
          username: registryName
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: managedSecrets
    }
    template: {
      containers: [
        {
          name: 'openjibo-cloud'
          image: '${registryLoginServer}/openjibo-cloud:${imageTag}'
          env: managedEnvVars
          resources: {
            cpu: 1
            memory: '2Gi'
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

resource keyVaultContainerAppSecretAccessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: keyVault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: subscription().tenantId
        objectId: containerApp.identity.principalId
        permissions: {
          secrets: [
            'get'
            'list'
          ]
        }
      }
    ]
  }
}

output containerAppName string = containerApp.name
output managedEnvironmentName string = managedEnvironment.name
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
output canonicalApiHostname string = apiHostname
output canonicalApiBaseUrl string = canonicalApiBaseUrl
output canonicalSocketHostname string = socketHostname
output canonicalSocketBaseUrl string = canonicalSocketBaseUrl
output canonicalNeoHubHostname string = neoHubHostname
output canonicalNeoHubBaseUrl string = canonicalNeoHubBaseUrl
output nativeCompatibilityApiHostname string = nativeCompatibilityApiHostname
output nativeCompatibilitySocketHostname string = nativeCompatibilitySocketHostname
