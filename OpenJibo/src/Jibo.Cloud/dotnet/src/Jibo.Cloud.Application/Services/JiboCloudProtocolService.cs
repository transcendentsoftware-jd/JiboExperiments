using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed class JiboCloudProtocolService(ICloudStateStore stateStore)
{
    private static readonly string[] AcceptedHosts =
    [
        "api.jibo.com",
        "openjibo.com",
        "openjibo.ai",
        "localhost"
    ];

    public Task<ProtocolDispatchResult> DispatchAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path == "/" &&
            string.IsNullOrWhiteSpace(envelope.ServicePrefix))
        {
            return Task.FromResult(ProtocolDispatchResult.NoContent());
        }

        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ProtocolDispatchResult.Ok(new { ok = true, host = envelope.HostName }));
        }

        if (envelope.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            envelope.Path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleMediaContent(envelope));
        }

        if (envelope.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) &&
            (envelope.Path.Equals("/upload/asr-binary", StringComparison.OrdinalIgnoreCase) ||
             envelope.Path.Equals("/upload/log-events", StringComparison.OrdinalIgnoreCase) ||
             envelope.Path.Equals("/upload/log-binary", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(ProtocolDispatchResult.Raw(200, string.Empty));
        }

        if (!AcceptedHosts.Contains(envelope.HostName, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(ProtocolDispatchResult.Ok(new
            {
                ok = true,
                accepted = false,
                host = envelope.HostName
            }));
        }

        var servicePrefix = envelope.ServicePrefix ?? string.Empty;
        var operation = envelope.Operation ?? string.Empty;

        if (servicePrefix.StartsWith("Log_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleLog(operation, envelope));
        }

        if (servicePrefix.StartsWith("Backup_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleBackup(operation));
        }

        if (servicePrefix.StartsWith("Account_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleAccount(operation, envelope));
        }

        if (servicePrefix.StartsWith("Notification_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleNotification(operation, envelope));
        }

        if (servicePrefix.StartsWith("Loop_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleLoop(operation));
        }

        if (servicePrefix.Equals("Media_20160725", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleMedia(operation, envelope));
        }

        if (servicePrefix.StartsWith("Key_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleKey(operation, envelope));
        }

        if (servicePrefix.StartsWith("Person_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandlePerson(operation));
        }

        if (servicePrefix.StartsWith("Robot_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleRobot(operation, envelope));
        }

        if (servicePrefix.StartsWith("Update_", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HandleUpdate(operation, envelope));
        }

        return Task.FromResult(ProtocolDispatchResult.Ok(new
        {
            ok = true,
            host = envelope.HostName,
            target = $"{servicePrefix}.{operation}".Trim('.'),
            operation,
            note = "unknown target default response"
        }));
    }

    private ProtocolDispatchResult HandleAccount(string operation, ProtocolEnvelope envelope)
    {
        var account = stateStore.GetAccount();
        var body = envelope.TryParseBody();

        if (operation.Equals("CreateHubToken", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new
            {
                token = stateStore.IssueHubToken(),
                expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            });
        }

        if (operation.Equals("CreateAccessToken", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new
            {
                token = $"access-{account.AccountId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            });
        }

        if (operation.Equals("CheckEmail", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            return ProtocolDispatchResult.Ok(new
            {
                exists = email.Equals(account.Email, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (operation is "Create" or "Login")
        {
            return ProtocolDispatchResult.Ok(new
            {
                id = account.AccountId,
                email = ReadString(body, "email") ?? account.Email,
                firstName = ReadString(body, "firstName") ?? account.FirstName,
                lastName = ReadString(body, "lastName") ?? account.LastName,
                gender = "unknown",
                birthday = 631152000000L,
                phoneNumber = "+10000000000",
                photoUrl = string.Empty,
                isActive = true,
                messagingAllowed = true,
                accessKeyId = account.AccessKeyId,
                secretAccessKey = account.SecretAccessKey,
                roles = Array.Empty<object>(),
                facebookConnected = false,
                termsAccepted = true
            });
        }

        if (operation.Equals("Get", StringComparison.OrdinalIgnoreCase))
        {
            var ids = ReadStringArray(body, "ids");
            var matches = ids.Count == 0 || ids.Contains(account.AccountId, StringComparer.OrdinalIgnoreCase);

            if (!matches)
            {
                return ProtocolDispatchResult.Ok(Array.Empty<object>());
            }

            return ProtocolDispatchResult.Ok(new[]
            {
                new
                {
                    id = account.AccountId,
                    email = account.Email,
                    firstName = account.FirstName,
                    lastName = account.LastName,
                    accessKeyId = account.AccessKeyId,
                    secretAccessKey = account.SecretAccessKey
                }
            });
        }

        if (operation is "Update" or "ResetKeys" or "Remove" or "ActivateByCode" or "ResendActivationCode" or
            "ChangePassword" or "SendPasswordReset" or "PasswordResetByCode" or "UpdatePhoto" or "RemovePhoto" or
            "VerifyPhoneByCode" or "AcceptTerms" or "FacebookConnect" or "FacebookMobileConnect")
        {
            return ProtocolDispatchResult.Ok(new
            {
                id = account.AccountId,
                email = account.Email,
                firstName = account.FirstName,
                lastName = account.LastName,
                accessKeyId = account.AccessKeyId,
                secretAccessKey = account.SecretAccessKey
            });
        }

        if (operation is "ChangeEmail" or "SendPhoneVerificationCode")
        {
            return ProtocolDispatchResult.Ok(new
            {
                id = account.AccountId
            });
        }

        if (operation.Equals("GetAccountByAccessToken", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new
            {
                id = account.AccountId,
                accessKeyId = account.AccessKeyId,
                secretAccessKey = account.SecretAccessKey,
                email = account.Email,
                friendlyId = stateStore.GetRobot().RobotId,
                payload = ReadObject(body, "payload")
            });
        }

        if (operation.Equals("Search", StringComparison.OrdinalIgnoreCase))
        {
            var query = (ReadString(body, "query") ?? string.Empty).ToLowerInvariant();
            var haystack = $"{account.Email} {account.FirstName} {account.LastName} {account.AccountId}".ToLowerInvariant();

            return ProtocolDispatchResult.Ok(query.Length > 0 && haystack.Contains(query)
                ? new[]
                {
                    new
                    {
                        id = account.AccountId,
                        email = account.Email,
                        firstName = account.FirstName,
                        lastName = account.LastName
                    }
                }
                : Array.Empty<object>());
        }

        if (operation.Equals("FacebookPrepareLogin", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new
            {
                url = "https://example.com/facebook-login",
                client_id = "fake-client-id",
                scope = "email",
                response_type = "token",
                state = $"fb-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                redirect_uri = "https://api.jibo.com/facebook/callback"
            });
        }

        if (operation.Equals("ConfirmEmailReset", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new { });
        }

        return ProtocolDispatchResult.Ok(new
        {
            id = account.AccountId,
            email = account.Email,
            firstName = account.FirstName,
            lastName = account.LastName
        });
    }

    private ProtocolDispatchResult HandleNotification(string operation, ProtocolEnvelope envelope)
    {
        if (!operation.Equals("NewRobotToken", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new { ok = true, operation });
        }

        var body = envelope.TryParseBody();
        var deviceId = !string.IsNullOrWhiteSpace(envelope.DeviceId)
            ? envelope.DeviceId!
            : ReadString(body, "deviceId")
              ?? ReadString(body, "serial_number")
              ?? ReadString(body, "serialNumber")
              ?? ReadString(body, "cpuid")
              ?? ReadString(body, "cpuId")
              ?? ReadString(body, "robotId")
              ?? "unknown-device";

        stateStore.GetOrCreateDevice(deviceId, envelope.FirmwareVersion, envelope.ApplicationVersion);

        return ProtocolDispatchResult.Ok(new
        {
            token = stateStore.IssueRobotToken(deviceId)
        });
    }

    private ProtocolDispatchResult HandleLoop(string operation)
    {
        if (operation is not ("List" or "ListLoops"))
        {
            return ProtocolDispatchResult.Ok(Array.Empty<object>());
        }

        return ProtocolDispatchResult.Ok(stateStore.GetLoops().Select(loop => new
        {
            id = loop.LoopId,
            name = loop.Name,
            owner = loop.OwnerAccountId,
            robot = loop.RobotId,
            robotFriendlyId = loop.RobotFriendlyId,
            members = Array.Empty<object>(),
            isSuspended = loop.IsSuspended,
            created = loop.CreatedUtc.ToUnixTimeMilliseconds(),
            updated = loop.UpdatedUtc.ToUnixTimeMilliseconds()
        }).ToArray());
    }

    private static ProtocolDispatchResult HandleLog(string operation, ProtocolEnvelope envelope)
    {
        return operation switch
        {
            "PutEventsAsync" => ProtocolDispatchResult.Ok(new
            {
                contentEncoding = "gzip",
                uploadUrl = "https://api.jibo.com/upload/log-events"
            }),
            "PutEvents" => ProtocolDispatchResult.Ok(new { }),
            "PutBinaryAsync" => ProtocolDispatchResult.Ok(new
            {
                url = "https://api.jibo.com/log/binary/fake-id",
                uploadUrl = "https://api.jibo.com/upload/log-binary"
            }),
            "PutAsrBinary" => ProtocolDispatchResult.Ok(new
            {
                bucketName = "openjibo-test",
                key = "asr/fake-key",
                uploadUrl = "https://api.jibo.com/upload/asr-binary"
            }),
            "NewKinesisCredentials" => ProtocolDispatchResult.Ok(new
            {
                credentials = new
                {
                    AccessKeyId = "fake-access-key",
                    Expiration = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
                    SecretAccessKey = "fake-secret",
                    SessionToken = "fake-session"
                },
                region = "us-east-1",
                streamName = "openjibo-log-stream"
            }),
            _ => ProtocolDispatchResult.Ok(new { })
        };
    }

    private ProtocolDispatchResult HandleMedia(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();

        if (operation.Equals("List", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(stateStore.ListMedia(
                ReadStringArray(body, "loopIds"),
                ReadLong(body, "after"),
                ReadLong(body, "before")).Select(MapMedia).ToArray());
        }

        if (operation.Equals("Get", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(stateStore.GetMedia(ReadStringArray(body, "paths")).Select(MapMedia).ToArray());
        }

        if (operation.Equals("Remove", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(stateStore.RemoveMedia(ReadStringArray(body, "paths")).Select(MapMedia).ToArray());
        }

        if (operation.Equals("Create", StringComparison.OrdinalIgnoreCase))
        {
            var loopId = ReadHeader(envelope, "x-loop-id") ?? ReadString(body, "loopId") ?? stateStore.GetLoops()[0].LoopId;
            var path = ReadHeader(envelope, "x-path") ?? ReadString(body, "path") ?? $"/media/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            var type = ReadHeader(envelope, "x-type") ?? ReadString(body, "type") ?? "unknown";
            var reference = ReadHeader(envelope, "x-reference") ?? ReadString(body, "reference") ?? string.Empty;
            var isEncrypted = ReadBooleanHeader(envelope, "x-encrypted") || ReadBool(body, "isEncrypted");
            var meta = ReadObject(body, "meta") ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var contentType = ReadHeader(envelope, "Content-Type") ?? "application/octet-stream";
            meta["contentType"] = contentType;
            if (!string.IsNullOrWhiteSpace(envelope.BodyText))
            {
                meta["bodyText"] = envelope.BodyText;
            }

            return ProtocolDispatchResult.Ok(MapMedia(stateStore.CreateMedia(loopId, path, type, reference, isEncrypted, meta)));
        }

        return ProtocolDispatchResult.Ok(Array.Empty<object>());
    }

    private ProtocolDispatchResult HandlePerson(string operation)
    {
        return ProtocolDispatchResult.Ok(operation.Equals("ListHolidays", StringComparison.OrdinalIgnoreCase)
            ? stateStore.GetHolidays()
            : []);
    }

    private ProtocolDispatchResult HandleBackup(string operation)
    {
        return operation.Equals("List", StringComparison.OrdinalIgnoreCase)
            ? ProtocolDispatchResult.Ok(stateStore.GetBackups())
            : ProtocolDispatchResult.Ok(Array.Empty<object>());
    }

    private ProtocolDispatchResult HandleKey(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();
        var loopId = ReadString(body, "loopId") ?? ReadString(body, "id") ?? stateStore.GetLoops()[0].LoopId;

        if (operation.Equals("ShouldCreate", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(new
            {
                shouldCreate = stateStore.ShouldCreateSymmetricKey(loopId)
            });
        }

        if (operation.Equals("CreateSymmetricKey", StringComparison.OrdinalIgnoreCase))
        {
            var symmetricKey = stateStore.GetOrCreateSymmetricKey(loopId);
            return ProtocolDispatchResult.Ok(new
            {
                loopId,
                key = symmetricKey,
                symmetricKey,
                created = true
            });
        }

        if (operation is "CreateRequest" or "RequestSymmetricKey")
        {
            var record = stateStore.CreateKeyRequest(loopId, ReadString(body, "publicKey") ?? string.Empty);
            return ProtocolDispatchResult.Ok(new
            {
                id = record.RequestId,
                loopId = record.LoopId
            });
        }

        if (operation.Equals("GetRequest", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(stateStore.GetKeyRequest(loopId, ReadString(body, "id"), ReadString(body, "publicKey")));
        }

        if (operation.Equals("ListIncomingRequests", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(stateStore.GetIncomingKeyRequests());
        }

        if (operation.Equals("ListBinaryRequests", StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolDispatchResult.Ok(stateStore.GetBinaryRequests());
        }

        if (operation is "Share" or "ShareSymmetricKey" or "ShareBinary")
        {
            return ProtocolDispatchResult.Ok(new { ok = true });
        }

        if (operation.Equals("LoadSymmetricKey", StringComparison.OrdinalIgnoreCase))
        {
            var symmetricKey = stateStore.GetOrCreateSymmetricKey(loopId);
            return ProtocolDispatchResult.Ok(new
            {
                loopId,
                key = symmetricKey,
                symmetricKey
            });
        }

        return ProtocolDispatchResult.Ok(new { ok = true, operation });
    }

    private ProtocolDispatchResult HandleRobot(string operation, ProtocolEnvelope envelope)
    {
        var robot = stateStore.GetRobot();

        if (operation.Equals("UpdateRobot", StringComparison.OrdinalIgnoreCase))
        {
            var updated = new DeviceRegistration
            {
                DeviceId = robot.DeviceId,
                RobotId = robot.RobotId,
                FriendlyName = robot.FriendlyName,
                FirmwareVersion = envelope.FirmwareVersion ?? robot.FirmwareVersion,
                ApplicationVersion = envelope.ApplicationVersion ?? robot.ApplicationVersion,
                HostMappings = robot.HostMappings
            };

            stateStore.UpdateRobot(updated);
            return ProtocolDispatchResult.Ok(new
            {
                result = "ok"
            });
        }

        if (operation.Equals("GetRobot", StringComparison.OrdinalIgnoreCase))
        {
            var profile = stateStore.GetRobotProfile();
            return ProtocolDispatchResult.Ok(new
            {
                id = ReadString(envelope.TryParseBody(), "id") ?? profile.RobotId,
                payload = profile.Payload,
                calibrationPayload = profile.CalibrationPayload,
                updated = profile.UpdatedUtc.ToUnixTimeMilliseconds(),
                created = profile.CreatedUtc.ToUnixTimeMilliseconds()
            });
        }

        return ProtocolDispatchResult.Ok(new
        {
            result = "ok"
        });
    }

    private ProtocolDispatchResult HandleUpdate(string operation, ProtocolEnvelope envelope)
    {
        var body = envelope.TryParseBody();
        var subsystem = ReadString(body, "subsystem");
        var filter = ReadString(body, "filter");
        var fromVersion = ReadString(body, "fromVersion");

        return operation switch
        {
            "ListUpdates" => ProtocolDispatchResult.Ok(stateStore.ListUpdates(subsystem, filter).Select(MapUpdate).ToArray()),
            "ListUpdatesFrom" => ProtocolDispatchResult.Ok(stateStore.ListUpdates(subsystem, filter)
                .Where(update => fromVersion is null || update.FromVersion.Equals(fromVersion, StringComparison.OrdinalIgnoreCase))
                .Select(MapUpdate)
                .ToArray()),
            "GetUpdateFrom" => HandleGetUpdateFrom(subsystem, fromVersion, filter),
            "CreateUpdate" => ProtocolDispatchResult.Ok(MapUpdate(stateStore.CreateUpdate(
                fromVersion,
                ReadString(body, "toVersion"),
                ReadString(body, "changes"),
                ReadString(body, "shaHash"),
                ReadLong(body, "length"),
                subsystem,
                filter,
                ReadObject(body, "dependencies")))),
            "RemoveUpdate" => ProtocolDispatchResult.Ok(MapUpdate(stateStore.RemoveUpdate(ReadString(body, "id")))),
            _ => ProtocolDispatchResult.Ok(Array.Empty<object>())
        };
    }

    private ProtocolDispatchResult HandleMediaContent(ProtocolEnvelope envelope)
    {
        var path = Uri.UnescapeDataString(envelope.Path["/media/".Length..]);
        var candidatePaths = new[] { path, $"/{path}" };
        var media = stateStore.GetMedia(candidatePaths).FirstOrDefault();
        if (media is null || media.IsDeleted)
        {
            return ProtocolDispatchResult.Raw(404, string.Empty);
        }

        var contentType = TryReadMetaString(media.Meta, "contentType") ?? "application/octet-stream";
        var bodyText = TryReadMetaString(media.Meta, "bodyText") ?? string.Empty;
        return ProtocolDispatchResult.Raw(200, bodyText, contentType);
    }

    private ProtocolDispatchResult HandleGetUpdateFrom(string? subsystem, string? fromVersion, string? filter)
    {
        var update = stateStore.GetUpdateFrom(subsystem, fromVersion, filter);
        return update is null
            ? ProtocolDispatchResult.Ok(new { })
            : ProtocolDispatchResult.Ok(MapUpdate(update));
    }

    private static object MapUpdate(UpdateManifest update)
    {
        return new
        {
            _id = update.UpdateId,
            created = update.CreatedUtc.ToUnixTimeMilliseconds(),
            accountId = "usr_openjibo_owner",
            fromVersion = update.FromVersion,
            toVersion = update.ToVersion,
            changes = update.Changes,
            url = update.Url,
            shaHash = update.ShaHash,
            length = update.Length,
            subsystem = update.Subsystem,
            filter = update.Filter,
            dependencies = new Dictionary<string, object?>()
        };
    }

    private static object MapMedia(MediaRecord item)
    {
        return new
        {
            path = item.Path,
            created = item.CreatedUtc.ToUnixTimeMilliseconds(),
            type = item.MediaType,
            reference = item.Reference,
            accountId = item.AccountId,
            loopId = item.LoopId,
            url = item.Url,
            thumbnailUrl = item.Url,
            originalUrl = item.Url,
            isEncrypted = item.IsEncrypted,
            isDeleted = item.IsDeleted,
            meta = item.Meta
        };
    }

    private static string? TryReadMetaString(IDictionary<string, object?> meta, string key)
    {
        return meta.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static string? ReadString(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static long? ReadLong(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return long.TryParse(property.ToString(), out var parsed) ? parsed : null;
    }

    private static bool ReadBool(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(property.ToString(), out var parsed) && parsed
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IDictionary<string, object?>? ReadObject(JsonElement? element, string propertyName)
    {
        if (element is null || !element.Value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in property.EnumerateObject())
        {
            result[child.Name] = child.Value.ValueKind switch
            {
                JsonValueKind.String => child.Value.GetString(),
                JsonValueKind.Number when child.Value.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when child.Value.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => child.Value.ToString()
            };
        }

        return result;
    }

    private static string? ReadHeader(ProtocolEnvelope envelope, string headerName)
    {
        return envelope.Headers.TryGetValue(headerName, out var value) ? value : null;
    }

    private static bool ReadBooleanHeader(ProtocolEnvelope envelope, string headerName)
    {
        return envelope.Headers.TryGetValue(headerName, out var value) &&
               bool.TryParse(value, out var parsed) &&
               parsed;
    }
}
