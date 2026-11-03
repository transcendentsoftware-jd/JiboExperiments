using System.Text.Json;
using System.Security.Cryptography;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class EncryptedUserDataSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly UserDataEncryptionService _encryptionService;
    private readonly ILogger<EncryptedUserDataSnapshotStore> _logger;

    private readonly string? _persistencePath;

    public EncryptedUserDataSnapshotStore(
        string? persistencePath,
        UserDataEncryptionService encryptionService,
        ILogger<EncryptedUserDataSnapshotStore>? logger = null)
    {
        _persistencePath = persistencePath;
        _encryptionService = encryptionService;
        _logger = logger ?? NullLogger<EncryptedUserDataSnapshotStore>.Instance;
    }

    // Compatibility entry point: unreadable existing data now throws instead of resetting.
    public UserIntegrationSnapshot LoadOrReset() => Load();

    public UserIntegrationSnapshot Load()
    {
        if (string.IsNullOrWhiteSpace(_persistencePath))
            return CreateEmptySnapshot();

        try
        {
            using var stream = File.OpenRead(_persistencePath);
            var envelope = JsonSerializer.Deserialize<UserDataEncryptedEnvelope>(stream, JsonOptions);
            if (envelope is null || envelope.Version != 1)
                throw new InvalidDataException();

            var plaintext = _encryptionService.Decrypt(new UserDataEncryptionService.EncryptedPayload(
                envelope.Version,
                envelope.Nonce,
                envelope.Ciphertext));

            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException();
            var versions = document.RootElement.EnumerateObject()
                .Where(property => property.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (versions.Length != 1 || versions[0].Value.ValueKind != JsonValueKind.Number
                || !versions[0].Value.TryGetInt32(out _))
                throw new InvalidDataException();
            var snapshot = document.Deserialize<UserIntegrationSnapshot>(JsonOptions);
            if (snapshot is null ||
                snapshot.SchemaVersion < UserIntegrationSnapshot.MinimumSupportedSchemaVersion ||
                snapshot.SchemaVersion > UserIntegrationSnapshot.CurrentSchemaVersion)
                throw new InvalidDataException();

            // v1 snapshots omit MemberCalendarFeeds; normalize to current schema on load.
            if (snapshot.SchemaVersion < UserIntegrationSnapshot.CurrentSchemaVersion)
                return new UserIntegrationSnapshot
                {
                    SchemaVersion = UserIntegrationSnapshot.CurrentSchemaVersion,
                    HomeAssistantLinks = snapshot.HomeAssistantLinks ?? [],
                    MemberCalendarFeeds = snapshot.MemberCalendarFeeds ?? []
                };

            if (snapshot.HomeAssistantLinks is null || snapshot.MemberCalendarFeeds is null)
                throw new InvalidDataException();
            return snapshot;
        }
        catch (FileNotFoundException)
        {
            return CreateEmptySnapshot();
        }
        catch (DirectoryNotFoundException)
        {
            return CreateEmptySnapshot();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                         or JsonException or CryptographicException or FormatException or ArgumentException)
        {
            _logger.LogError("User integration state could not be loaded ({FailureType}). Existing data was preserved.",
                exception.GetType().Name);
            throw new InvalidDataException(
                "Existing user integration state could not be loaded. The file was preserved. " +
                "Restore the matching encryption keys or a valid backup before retrying.");
        }
    }

    public void Save(UserIntegrationSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(_persistencePath)) return;

        var directory = Path.GetDirectoryName(_persistencePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var plaintext = JsonSerializer.Serialize(snapshot, JsonOptions);
        var encrypted = _encryptionService.Encrypt(plaintext);
        var envelope = new UserDataEncryptedEnvelope
        {
            Version = encrypted.Version,
            Nonce = encrypted.Nonce,
            Ciphertext = encrypted.Ciphertext
        };

        var tempPath = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? Directory.GetCurrentDirectory() : directory,
            $".{Path.GetFileName(_persistencePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(envelope, JsonOptions));

            if (File.Exists(_persistencePath))
                File.Replace(tempPath, _persistencePath, null);
            else
                File.Move(tempPath, _persistencePath);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static UserIntegrationSnapshot CreateEmptySnapshot()
    {
        return new UserIntegrationSnapshot
        {
            SchemaVersion = UserIntegrationSnapshot.CurrentSchemaVersion,
            HomeAssistantLinks = [],
            MemberCalendarFeeds = []
        };
    }

    private sealed class UserDataEncryptedEnvelope
    {
        public int Version { get; init; } = 1;
        public string Nonce { get; init; } = string.Empty;
        public string Ciphertext { get; init; } = string.Empty;
    }
}
