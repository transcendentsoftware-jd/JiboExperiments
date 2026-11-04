using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Calendar;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Api.Hosting;

internal static class PortalEndpoints
{
    private static readonly TimeSpan StatusHeartbeatWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StatusActiveActivityWindow = TimeSpan.FromMinutes(2);
    private static readonly string[] RequiredLegacyHostMappings =
    [
        "api.jibo.com",
        "api-socket.jibo.com",
        "neo-hub.jibo.com"
    ];

    internal static void MapPortalEndpoints(this WebApplication app)
    {
        app.MapGet("/api/onboarding/trusted-servers", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            var servers = cloudStateStore.GetTrustedServers();
            return Results.Json(new
            {
                directoryVersion = "1",
                hostedHttpsRequired = true,
                trustedRootHost = "api.openjibo.com",
                allowCustomEntry = true,
                customEntryMode = "self-hosted",
                serverTypes = new
                {
                    managed = new
                    {
                        listed = true,
                        acceptsPublicConnections = true,
                        requiresHttps = true
                    },
                    hybrid = new
                    {
                        listed = true,
                        acceptsPublicConnections = false,
                        requiresHttps = true
                    },
                    selfHosted = new
                    {
                        listed = false,
                        acceptsPublicConnections = false,
                        requiresHttps = false
                    }
                },
                servers = servers.Select(server => new
                {
                    server.ServerId,
                    server.CanonicalHost,
                    server.DisplayName,
                    server.ServerKind,
                    server.IsListed,
                    server.AcceptsPublicConnections,
                    server.ParticipatesInCloudSync,
                    server.RequiresHttps,
                    server.IsTrustRoot,
                    server.IsActive,
                    server.Description,
                    server.RegisteredAtUtc,
                    server.UpdatedAtUtc,
                    server.LastSeenAtUtc
                })
                .Where(server => !server.IsTrustRoot || server.ServerKind is "managed" or "hybrid")
            });
        });

        app.MapPost("/api/portal/trusted-servers", (
            [FromBody] UpsertTrustedServerRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.CanonicalHost))
                return Results.BadRequest(new { error = "canonicalHost is required." });

            var serverKind = NormalizeTrustedServerKind(request.ServerKind);
            if (serverKind == "self-hosted")
                return Results.BadRequest(new { error = "self-hosted entries are separate from the trusted registry." });

            var server = cloudStateStore.UpsertTrustedServer(new TrustedServerRecord
            {
                CanonicalHost = request.CanonicalHost.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? request.CanonicalHost.Trim()
                    : request.DisplayName.Trim(),
                ServerKind = serverKind,
                IsListed = request.IsListed ?? true,
                AcceptsPublicConnections = request.AcceptsPublicConnections ?? serverKind != "hybrid",
                ParticipatesInCloudSync = request.ParticipatesInCloudSync ?? true,
                RequiresHttps = request.RequiresHttps ?? true,
                IsActive = request.IsActive ?? true,
                Description = request.Description?.Trim() ?? string.Empty
            });

            var admission = cloudStateStore.RecordTrustedServerAdmission(
                server,
                "admit",
                session.DeviceId,
                session.FriendlyId,
                request.Reason);

            return Results.Json(new
            {
                trustedServer = server,
                admissionRecord = admission
            });
        });

        app.MapPost("/api/portal/trusted-servers/lifecycle", (
            [FromBody] TrustedServerLifecycleRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.CanonicalHost))
                return Results.BadRequest(new { error = "canonicalHost is required." });

            if (string.IsNullOrWhiteSpace(request.Action))
                return Results.BadRequest(new { error = "action is required." });

            var action = request.Action.Trim().ToLowerInvariant();
            var normalizedKind = NormalizeTrustedServerKind(request.ServerKind);
            if (normalizedKind == "self-hosted")
                return Results.BadRequest(new { error = "self-hosted entries are separate from the trusted registry." });

            var host = request.CanonicalHost.Trim();
            var existing = cloudStateStore.FindTrustedServer(host);

            if (action is "revoke" or "reactivate" or "mark-seen" && existing is null)
                return Results.NotFound(new { error = "trusted server not found." });

            if (existing is not null && existing.IsTrustRoot && action == "revoke")
                return Results.BadRequest(new { error = "the trust root cannot be revoked." });

            TrustedServerRecord lifecycleUpdate = action switch
            {
                "admit" => UpsertLifecycleServer(existing, request, normalizedKind, host, isActive: true,
                    isListed: request.IsListed ?? true,
                    acceptsPublicConnections: request.AcceptsPublicConnections ?? normalizedKind != "hybrid",
                    participatesInCloudSync: request.ParticipatesInCloudSync ?? normalizedKind != "self-hosted",
                    requiresHttps: request.RequiresHttps ?? true,
                    lastSeenAtUtc: request.LastSeenAtUtc),
                "revoke" => UpsertLifecycleServer(existing!, request, existing!.ServerKind, host, isActive: false,
                    isListed: false,
                    acceptsPublicConnections: false,
                    participatesInCloudSync: false,
                    requiresHttps: existing!.RequiresHttps,
                    lastSeenAtUtc: existing!.LastSeenAtUtc),
                "reactivate" => UpsertLifecycleServer(existing!, request, existing!.ServerKind, host, isActive: true,
                    isListed: true,
                    acceptsPublicConnections: request.AcceptsPublicConnections ?? existing!.AcceptsPublicConnections,
                    participatesInCloudSync: request.ParticipatesInCloudSync ?? existing!.ParticipatesInCloudSync,
                    requiresHttps: request.RequiresHttps ?? existing!.RequiresHttps,
                    lastSeenAtUtc: request.LastSeenAtUtc ?? existing!.LastSeenAtUtc),
                "mark-seen" => UpsertLifecycleServer(existing!, request, existing!.ServerKind, host, isActive: existing!.IsActive,
                    isListed: existing!.IsListed,
                    acceptsPublicConnections: existing!.AcceptsPublicConnections,
                    participatesInCloudSync: existing!.ParticipatesInCloudSync,
                    requiresHttps: existing!.RequiresHttps,
                    lastSeenAtUtc: request.LastSeenAtUtc ?? DateTimeOffset.UtcNow),
                _ => null!
            };

            if (lifecycleUpdate is null)
                return Results.BadRequest(new { error = "action must be admit, revoke, reactivate, or mark-seen." });

            var updated = cloudStateStore.UpsertTrustedServer(lifecycleUpdate);
            var admission = cloudStateStore.RecordTrustedServerAdmission(
                updated,
                action,
                session.DeviceId,
                session.FriendlyId,
                request.Reason);

            return Results.Json(new
            {
                trustedServer = updated,
                admissionRecord = admission
            });
        });

        app.MapPost("/api/onboarding/self-hosted/validate", (
            [FromBody] ValidateSelfHostedRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService) =>
        {
            var session = ResolvePortalSession(httpRequest, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            var mode = NormalizeSelfHostedMode(request.ServerMode);
            var hostname = NormalizeOnboardingHost(request.ServerHost ?? request.ServerUrl);
            var isLocal = IsLocalSelfHostedTarget(hostname);
            var requiresHttps = mode == "self-hosted-hybrid";
            var allowsHttp = mode == "self-hosted" && isLocal;

            return Results.Json(new
            {
                serverMode = mode,
                registryBacked = false,
                canonicalHost = hostname,
                isLocalTarget = isLocal,
                requiresHttps,
                allowsHttp,
                acceptsPublicConnections = false,
                participatesInCloudSync = mode == "self-hosted-hybrid",
                trustGuidance = allowsHttp
                    ? "Use HTTP for local self-hosted operation."
                    : "Use HTTPS for self-hosted hybrid operation and keep it private from public connections.",
                notes = allowsHttp
                    ? new[]
                    {
                        "This path stays outside the trusted server registry.",
                        "Use a local hostname or IP address for the robot/app setup."
                    }
                    : new[]
                    {
                        "This path stays outside the trusted server registry.",
                        "Use a trusted HTTPS certificate even though the server is not publicly listed."
                }
            });
        });

        app.MapGet("/api/portal/trusted-servers/admissions/export", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            var admissions = cloudStateStore.GetTrustedServerAdmissions();
            var payload = JsonSerializer.Serialize(new
            {
                exportedAtUtc = DateTimeOffset.UtcNow,
                exportedBy = session.FriendlyId,
                admissions
            }, new JsonSerializerOptions { WriteIndented = true });

            var fileName = $"openjibo-trusted-server-admissions-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
            return Results.File(
                Encoding.UTF8.GetBytes(payload),
                "application/json; charset=utf-8",
                fileName);
        });

        app.MapPost("/api/portal/account/register", (
            [FromBody] PortalAccountRegisterRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            if (!IsValidEmail(request.Email))
                return Results.BadRequest(new { error = "A valid email address is required." });
            if (!IsValidPortalPassword(request.Password))
                return Results.BadRequest(new { error = "Password must be between 8 and 32 characters." });

            var user = cloudStateStore.CreateUser(request.Email!.Trim(), request.Password!,
                null, null);
            if (user is null)
                return Results.Conflict(new { error = "An account with this email already exists." });

            return Results.Json(BuildAccountSessionPayload(portalSessionService, cloudStateStore, user));
        });

        app.MapPost("/api/portal/account/login", (
            [FromBody] PortalAccountLoginRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
                return Results.BadRequest(new { error = "Email and password are required." });

            var user = cloudStateStore.AuthenticateUser(request.Email.Trim(), request.Password);
            if (user is null)
                return Results.Unauthorized();

            return Results.Json(BuildAccountSessionPayload(portalSessionService, cloudStateStore, user));
        });

        app.MapGet("/api/portal/account", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService, allowAccountSession: true);
            if (session?.UserId is null)
                return Results.Unauthorized();

            var user = cloudStateStore.GetUserById(session.UserId);
            if (user is null)
                return Results.Unauthorized();

            return Results.Json(BuildAccountPayload(user, cloudStateStore));
        });

        app.MapPost("/api/portal/robots/pair", (
            [FromBody] PairPortalRobotRequest request,
            HttpRequest httpRequest,
            JiboVerificationService verificationService,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            HomeAssistantConnectionRegistry integrationConnections) =>
        {
            var accountSession = ResolvePortalSession(httpRequest, request.PortalSessionToken,
                portalSessionService, allowAccountSession: true);
            if (accountSession?.UserId is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Code))
                return Results.BadRequest(new { error = "code is required." });

            var user = cloudStateStore.GetUserById(accountSession.UserId);
            if (user is null)
                return Results.Unauthorized();

            var result = verificationService.TryConfirmByCode(request.Code);
            if (!result.Ok)
                return Results.BadRequest(new { error = result.Error });

            RegisterVerifiedRobotIdentity(cloudStateStore, result.DeviceId!, result.FriendlyId!);
            var ownership = cloudStateStore.LinkUserToDevice(user.Id, result.DeviceId!, "portal-pairing");
            integrationConnections.RevokeInactiveConnections();
            var session = portalSessionService.CreateSession(result.DeviceId!, result.FriendlyId!, user.Id,
                ownershipEpoch: ownership.EffectiveOwnershipEpoch);

            return Results.Json(new
            {
                portalSessionToken = session.Token,
                jiboFriendlyId = result.FriendlyId,
                jiboDeviceId = result.DeviceId,
                expiresAtUtc = session.ExpiresAtUtc
            });
        });

        app.MapPost("/api/portal/robots/select", (
            [FromBody] SelectPortalRobotRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var accountSession = ResolvePortalSession(httpRequest, request.PortalSessionToken,
                portalSessionService, allowAccountSession: true);
            if (accountSession?.UserId is null)
                return Results.Unauthorized();

            var selector = request.DeviceId?.Trim();
            if (string.IsNullOrWhiteSpace(selector))
                return Results.BadRequest(new { error = "deviceId is required." });

            var robot = cloudStateStore.GetDevicesForUser(accountSession.UserId)
                .FirstOrDefault(device =>
                    device.DeviceId.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                    device.RobotId.Equals(selector, StringComparison.OrdinalIgnoreCase) ||
                    device.FriendlyName.Equals(selector, StringComparison.OrdinalIgnoreCase));
            if (robot is null)
                return Results.NotFound(new { error = "That robot is not paired to this account." });

            var session = portalSessionService.CreateSession(robot.DeviceId, robot.RobotId,
                accountSession.UserId, ownershipEpoch: cloudStateStore.GetUserDeviceLink(robot.DeviceId)?.EffectiveOwnershipEpoch);
            return Results.Json(new
            {
                portalSessionToken = session.Token,
                jiboFriendlyId = robot.RobotId,
                jiboDeviceId = robot.DeviceId,
                expiresAtUtc = session.ExpiresAtUtc
            });
        });

        app.MapPut("/api/portal/robots/{deviceId}/name", (
            string deviceId,
            [FromBody] RenamePortalRobotRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var accountSession = ResolvePortalSession(httpRequest, request.PortalSessionToken,
                portalSessionService, allowAccountSession: true);
            if (accountSession?.UserId is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "name is required." });

            var name = request.Name.Trim();
            if (name.Length > 64)
                return Results.BadRequest(new { error = "name must be 64 characters or fewer." });

            var ownsRobot = cloudStateStore.GetDevicesForUser(accountSession.UserId)
                .Any(robot => robot.DeviceId.Equals(deviceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!ownsRobot)
                return Results.NotFound(new { error = "That robot is not paired to this account." });

            try
            {
                var renamed = cloudStateStore.RenameDeviceName(deviceId, name);
                return Results.Json(new
                {
                    deviceId = renamed.DeviceId,
                    robotId = renamed.RobotId,
                    friendlyName = renamed.FriendlyName
                });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapPost("/api/portal/jibo-verification/confirm", (
            [FromBody] ConfirmJiboVerificationRequest request,
            JiboVerificationService verificationService,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code))
                return Results.BadRequest(new { error = "code is required." });

            var result = verificationService.TryConfirmByCode(request.Code);
            if (!result.Ok)
                return Results.BadRequest(new { error = result.Error });

            RegisterVerifiedRobotIdentity(cloudStateStore, result.DeviceId!, result.FriendlyId!);
            var ownership = cloudStateStore.GetUserDeviceLink(result.DeviceId!);
            var session = portalSessionService.CreateSession(result.DeviceId!, result.FriendlyId!,
                ownerUserId: ownership?.UserId, ownershipEpoch: ownership?.EffectiveOwnershipEpoch);

            return Results.Json(new
            {
                portalSessionToken = session.Token,
                jiboFriendlyId = result.FriendlyId,
                jiboDeviceId = result.DeviceId,
                expiresAtUtc = session.ExpiresAtUtc
            });
        });

        app.MapGet("/api/portal/dashboard", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            ICloudStateStore cloudStateStore,
            HomeAssistantConnectionRegistry registry) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var link = integrationStore.FindLinkForJibo(session.FriendlyId, session.FriendlyId, session.OwnershipEpoch ?? "unowned");
            return Results.Json(BuildDashboardPayload(session, link, registry, cloudStateStore, integrationStore));
        });

        app.MapGet("/api/portal/calendar-feeds", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            return Results.Json(BuildCalendarFeedsPayload(cloudStateStore, integrationStore, session));
        });

        app.MapPut("/api/portal/calendar-feeds/{memberId}", (
            string memberId,
            [FromBody] UpsertMemberCalendarFeedRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(memberId))
                return Results.BadRequest(new { error = "memberId is required." });

            if (!IcalUrlValidator.TryValidateHttpsPublicUrl(request.IcalUrl, out _, out var validationError))
                return Results.BadRequest(new { error = validationError });

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            var member = FindCalendarFeedPerson(cloudStateStore, loopId, session, memberId);
            if (member is null)
                return Results.NotFound(new { error = "Loop member not found." });

            MemberCalendarFeedRecord feed;
            try
            {
                feed = integrationStore.UpsertMemberCalendarFeed(
                    loopId, member.MemberId, request.IcalUrl!.Trim(),
                    request.IsEnabled ?? true, session.OwnershipEpoch ?? "unowned");
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }

            return Results.Json(BuildMemberCalendarFeedStatus(member, feed));
        });

        app.MapDelete("/api/portal/calendar-feeds/{memberId}", (
            string memberId,
            HttpRequest request,
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            MemberCalendarFeedRecord? removed;
            try { removed = integrationStore.ClearMemberCalendarFeed(loopId, memberId, session.OwnershipEpoch ?? "unowned"); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            if (removed is null)
                return Results.NotFound(new { error = "No calendar feed is configured for that member." });

            return Results.Json(new { cleared = true, memberId });
        });

        app.MapGet("/api/portal/loop-members", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            return Results.Json(BuildLoopMembersPayload(cloudStateStore, loopId));
        });

        app.MapPost("/api/portal/loop-members", async (
            [FromBody] AddLoopMemberRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            LoopUpdatedPushService loopUpdatedPushService,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var firstName = request.FirstName?.Trim();
            if (string.IsNullOrWhiteSpace(firstName))
                return Results.BadRequest(new { error = "firstName is required." });

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            var member = cloudStateStore.AddLoopMember(
                loopId,
                null,
                null,
                firstName,
                request.LastName?.Trim(),
                NormalizeGender(request.Gender),
                null,
                false,
                "member",
                markPortalEdited: true);

            await TryPushLoopUpdatedAsync(loopUpdatedPushService, session, loopId, cancellationToken);
            return Results.Json(BuildLoopMemberPayload(member));
        });

        app.MapPut("/api/portal/loop-members/{memberId}", async (
            string memberId,
            [FromBody] UpdateLoopMemberRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            LoopUpdatedPushService loopUpdatedPushService,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            var existing = cloudStateStore.GetLoopMembers(loopId)
                .FirstOrDefault(m => m.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return Results.NotFound(new { error = "Loop member not found." });

            var firstName = request.FirstName?.Trim();
            if (request.FirstName is not null && string.IsNullOrWhiteSpace(firstName))
                return Results.BadRequest(new { error = "firstName cannot be blank." });

            try
            {
                var updated = cloudStateStore.UpdateLoopMember(
                    loopId,
                    memberId,
                    firstName,
                    request.LastName?.Trim(),
                    request.Gender is null ? null : NormalizeGender(request.Gender),
                    null,
                    existing.IsChild,
                    null,
                    null,
                    markPortalEdited: true);

                await TryPushLoopUpdatedAsync(loopUpdatedPushService, session, loopId, cancellationToken);
                return Results.Json(BuildLoopMemberPayload(updated));
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(new { error = "Loop member not found." });
            }
        });

        app.MapDelete("/api/portal/loop-members/{memberId}", async (
            string memberId,
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            LoopUpdatedPushService loopUpdatedPushService,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            var existing = cloudStateStore.GetLoopMembers(loopId)
                .FirstOrDefault(m => m.Id.Equals(memberId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                return Results.NotFound(new { error = "Loop member not found." });

            if (existing.Type is "owner" or "robot")
                return Results.BadRequest(new { error = "The loop owner and robot cannot be removed here." });

            cloudStateStore.RemoveLoopMember(loopId, memberId);
            await TryPushLoopUpdatedAsync(loopUpdatedPushService, session, loopId, cancellationToken);
            return Results.Json(new { removed = true, memberId });
        });

        app.MapPost("/api/portal/calendar-feeds/{memberId}/test", async (
            string memberId,
            [FromBody] TestMemberCalendarFeedRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            ICloudStateStore cloudStateStore,
            IcalCalendarFeedInspector feedInspector) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            var member = FindCalendarFeedPerson(cloudStateStore, loopId, session, memberId);
            if (member is null)
                return Results.NotFound(new { error = "Loop member not found." });

            var configuredFeed = integrationStore.FindMemberCalendarFeed(loopId, member.MemberId,
                session.OwnershipEpoch ?? "unowned");
            var icalUrl = request.IcalUrl;
            if (string.IsNullOrWhiteSpace(icalUrl))
                icalUrl = configuredFeed?.IcalUrl;

            if (string.IsNullOrWhiteSpace(icalUrl))
                return Results.BadRequest(new { error = "iCal URL is required." });

            if (!IcalUrlValidator.TryValidateHttpsPublicUrl(icalUrl, out _, out var validationError))
                return Results.BadRequest(new { error = validationError });

            var probe = await feedInspector.ProbeAsync(icalUrl);
            if (!probe.Ok)
            {
                if (configuredFeed is not null)
                    integrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, member.MemberId, null, probe.Error, configuredFeed.FeedId);
                return Results.Json(new
                {
                    ok = false,
                    error = probe.Error,
                    host = IcalUrlValidator.TryGetSafeHost(icalUrl)
                });
            }

            if (configuredFeed is not null)
                integrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, member.MemberId, DateTimeOffset.UtcNow, null, configuredFeed.FeedId);
            return Results.Json(new
            {
                ok = true,
                host = IcalUrlValidator.TryGetSafeHost(icalUrl),
                todayEventCount = probe.TodayEventCount,
                tomorrowEventCount = probe.TomorrowEventCount,
                sampleSummaries = probe.SampleSummaries
            });
        });

        app.MapGet("/api/portal/identity-graph", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var graph = cloudStateStore.GetIdentityGraph(ResolvePortalLoopId(cloudStateStore, session));
            return Results.Json(new
            {
                graph.AccountId,
                graph.LoopId,
                graph.RobotId,
                graph.DeviceId,
                graph.SnapshotVersion,
                graph.ContentHash,
                graph.SignatureAlgorithm,
                graph.SignatureKeyId,
                graph.SignaturePayload,
                graph.Signature,
                graph.AdmissionAssessment,
                graph.EvidenceBundle,
                graph.People,
                graph.Members,
                graph.Relationships,
                graph.EvidenceSignals
            });
        });


        app.MapPost("/api/portal/identity-graph/revocations", (
            [FromBody] RevokeIdentityGraphAnchorRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Anchor))
                return Results.BadRequest(new { error = "anchor is required." });

            cloudStateStore.RevokeIdentityGraphAnchor(request.Anchor);
            var graph = cloudStateStore.GetIdentityGraph(ResolvePortalLoopId(cloudStateStore, session));

            return Results.Json(new
            {
                revoked = true,
                anchor = request.Anchor.Trim(),
                graph.AdmissionAssessment,
                graph.EvidenceBundle
            });
        });

        app.MapGet("/api/portal/identity-graph/evidence-bundle", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var graph = cloudStateStore.GetIdentityGraph(ResolvePortalLoopId(cloudStateStore, session));
            var fileName = $"openjibo-identity-evidence-{graph.DeviceId}-{graph.EvidenceBundle.BundleHash}.txt";
            return Results.File(
                Encoding.UTF8.GetBytes(graph.EvidenceBundle.Envelope),
                "text/plain; charset=utf-8",
                fileName);
        });


        app.MapPost("/api/portal/identity-graph/evidence-bundle/verify", (
            [FromBody] VerifyIdentityGraphEvidenceBundleRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Envelope))
                return Results.BadRequest(new { error = "envelope is required." });

            var verification = IdentityGraphEvidenceBundleVerifier.Verify(
                request.Envelope,
                request.LocalRevokedAnchors ?? []);

            return Results.Json(new
            {
                verification.IsValid,
                verification.IsLocallyAdmissible,
                verification.EffectiveAdmissionRecommendation,
                verification.AdmissionRecommendation,
                verification.AdmissionPolicyVersion,
                verification.AdmissionReasons,
                verification.RequiredEvidence,
                verification.SatisfiedEvidence,
                verification.BlockingEvidence,
                verification.RecommendedActions,
                verification.RevocationChecks,
                verification.RevocationAnchors,
                verification.RevocationListHash,
                verification.LocalRevocationMatches,
                verification.TrustPurpose,
                verification.PeerTransportStatus,
                verification.ReplicationReadiness,
                verification.SyncDirection,
                verification.PeerAdmissionMode,
                verification.RetentionPolicy,
                verification.AdmissionReviewStatus,
                verification.ExportedByCloudVersion,
                verification.ExportedByService,
                verification.DirectPeerTransportAllowed,
                verification.AccountId,
                verification.LoopId,
                verification.RobotId,
                verification.DeviceId,
                verification.PeopleCount,
                verification.MemberCount,
                verification.RelationshipCount,
                verification.EvidenceSignalCount,
                verification.RelationshipKinds,
                verification.EvidenceSignalKinds,
                verification.BundleHash,
                verification.ComputedBundleHash,
                verification.AdmissionDecisionSignatureValid,
                verification.SnapshotSignatureValid,
                verification.Errors
            });
        });

        app.MapPost("/api/portal/home-assistant/link", async (
            [FromBody] LinkHomeAssistantRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            HomeAssistantConnectionRegistry registry,
            IUserIntegrationStore integrationStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.HaCode))
                return Results.BadRequest(new { error = "haCode is required." });

            var pendingHa = registry.TryGetPendingByCode(request.HaCode);
            if (pendingHa is null)
                return Results.BadRequest(new
                    { error = "Home Assistant verification code is invalid or has expired." });

            HomeAssistantLinkRecord link;
            try
            {
                link = integrationStore.AddHomeAssistantLink(
                    session.DeviceId, session.FriendlyId,
                    pendingHa.InstanceId, session.OwnershipEpoch ?? "unowned");
            }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }

            var delivered = await registry.SendPairedAsync(
                pendingHa.InstanceId,
                session.FriendlyId,
                link.LinkId,
                link.CommandSecret);

            if (!delivered)
                return Results.BadRequest(new { error = "Home Assistant is not connected to this server right now." });

            return Results.Json(new
            {
                linkId = link.LinkId,
                jiboFriendlyId = session.FriendlyId,
                haInstanceId = pendingHa.InstanceId,
                homeAssistant = BuildHomeAssistantPayload(link, registry)
            });
        });

        app.MapDelete("/api/portal/home-assistant/link", async (
            HttpRequest request,
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            HomeAssistantConnectionRegistry registry) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var link = integrationStore.FindLinkForJibo(session.FriendlyId, session.FriendlyId, session.OwnershipEpoch ?? "unowned");
            if (link is null)
                return Results.NotFound(new { error = "No Home Assistant link exists for this Jibo." });

            integrationStore.RemoveHomeAssistantLink(link.LinkId);
            await registry.SendUnpairedAsync(link.HaInstanceId, link.LinkId);

            return Results.Json(new
            {
                unlinked = true,
                jiboFriendlyId = session.FriendlyId
            });
        });

        app.MapPut("/api/portal/home-assistant/climate-options", (
            [FromBody] UpdateHomeAssistantClimateOptionsRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            HomeAssistantConnectionRegistry registry) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var link = integrationStore.FindLinkForJibo(session.FriendlyId, session.FriendlyId, session.OwnershipEpoch ?? "unowned");
            if (link is null)
                return Results.NotFound(new { error = "No Home Assistant link exists for this Jibo." });

            var updated = integrationStore.UpdateHomeAssistantClimateBlacklist(
                link.LinkId,
                request.BlacklistHeat ?? false,
                request.BlacklistCool ?? false);

            if (updated is null)
                return Results.NotFound(new { error = "No Home Assistant link exists for this Jibo." });

            return Results.Json(new
            {
                ok = true,
                homeAssistant = BuildHomeAssistantPayload(updated, registry)
            });
        });


        app.MapGet("/api/portal/admin/summary", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            IUserIntegrationStore integrationStore,
            HomeAssistantConnectionRegistry registry) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var loopId = ResolvePortalLoopId(cloudStateStore, session);
            var robot = ResolvePortalRobot(cloudStateStore, session);
            var persistence = cloudStateStore.GetPersistenceStateInfo();
            var graph = cloudStateStore.GetIdentityGraph(loopId);
            var backups = cloudStateStore.GetBackups()
                .Where(backup => string.Equals(backup.LoopId, loopId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var updates = cloudStateStore.ListUpdates();
            var media = cloudStateStore.ListMedia([loopId]);
            var people = graph.People;
            var haLink = integrationStore.FindLinkForJibo(session.FriendlyId, session.FriendlyId, session.OwnershipEpoch ?? "unowned");
            var robotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { session.DeviceId, session.FriendlyId };
            var trustedServerAdmissions = cloudStateStore.GetTrustedServerAdmissions()
                .Where(admission =>
                    robotKeys.Contains(admission.ActorDeviceId) ||
                    robotKeys.Contains(admission.ActorFriendlyId))
                .ToArray();

            return Results.Json(new
            {
                cloudVersion = OpenJiboCloudBuildInfo.Version,
                persistence,
                robot = new
                {
                    robot.DeviceId,
                    robot.RobotId,
                    robot.FriendlyName,
                    robot.FirmwareVersion,
                    robot.ApplicationVersion,
                    robot.IsActive,
                    robot.HostMappings
                },
                counts = new
                {
                    loops = 1,
                    people = people.Count,
                    updates = updates.Count,
                    backups = backups.Length,
                    media = media.Count,
                    homeAssistantLinks = haLink is null ? 0 : 1,
                    homeAssistantConnected = haLink is not null && registry.IsInstanceConnected(haLink.HaInstanceId)
                        ? 1
                        : 0,
                    identityRelationships = graph.Relationships.Count,
                    identityEvidenceSignals = graph.EvidenceSignals.Count,
                    trustedServerAdmissions = trustedServerAdmissions.Length
                },
                conversion = new
                {
                    targetMode = robot.HostMappings.TryGetValue("api.jibo.com", out var apiHost) &&
                                 apiHost.Contains("openjibo", StringComparison.OrdinalIgnoreCase)
                        ? "open-jibo"
                        : "unconfirmed",
                    hostMappings = robot.HostMappings,
                    requiredHostMappings = RequiredLegacyHostMappings,
                    missingHostMappings = GetMissingRequiredHostMappings(robot),
                    trustedServerAdmissions = trustedServerAdmissions.Take(5).Select(admission => new
                    {
                        admission.CanonicalHost,
                        admission.ServerKind,
                        admission.Action,
                        admission.ActorFriendlyId,
                        admission.CreatedUtc,
                        admission.SignatureKeyId,
                        admission.Signature
                    }),
                    blockers = BuildAdminConversionBlockers(robot, graph),
                    operatorQuestions = new[]
                    {
                        "Which physical robot variant should be the first conversion target?",
                        "Has the latest non-destructive backup/rollback snapshot been filmed and approved?",
                        "Which safe awakening assets are approved for first-boot reuse?",
                        "Do live websocket captures expose stable face/person identifiers, or should the demo stay smoke-seeded?"
                    }
                },
                harness = new
                {
                    url = "/harness",
                    suggestedOperations = new[]
                    {
                        "OOBE_20161026.AuditConversion",
                        "OOBE_20161026.PrepareRobot",
                        "OOBE_20161026.GetStatus",
                        "OOBE_20161026.VerifyConnection",
                        "Robot_20160225.GetRobot",
                        "Update_20160225.ListUpdates"
                    }
                }
            });
        });

        app.MapPost("/api/portal/status/login", (
            [FromBody] AdminStatusLoginRequest request,
            IConfiguration configuration,
            PortalSessionService portalSessionService) =>
        {
            if (string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "password is required." });

            var configuredPassword = ResolveAdminStatusPassword(configuration);
            if (string.IsNullOrWhiteSpace(configuredPassword))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            if (!PasswordsMatch(request.Password, configuredPassword))
                return Results.Unauthorized();

            var session = portalSessionService.CreateAdministratorSession();
            return Results.Json(new
            {
                portalSessionToken = session.Token,
                expiresAtUtc = session.ExpiresAtUtc
            });
        });

        app.MapGet("/api/portal/status/summary", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            RobotPresenceRegistry robotPresenceRegistry,
            FleetNetworkPresenceRegistry fleetNetworkPresenceRegistry,
            OpenJiboServerIdentity serverIdentity,
            RobotIdentitySuggestionStore identitySuggestionStore,
            ReleaseSmokeAuthorizationOptions releaseSmokeAuthorization,
            IConfiguration configuration) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            var includeHidden = bool.TryParse(request.Query["includeHidden"], out var requestedIncludeHidden) &&
                                requestedIncludeHidden;
            return Results.Json(BuildStatusSummaryPayload(cloudStateStore, robotPresenceRegistry,
                fleetNetworkPresenceRegistry, serverIdentity, identitySuggestionStore,
                releaseSmokeAuthorization, configuration, includeHidden));
        });

        app.MapPost("/api/portal/status/robots/{deviceId}/archive", (
            string deviceId,
            [FromBody] ArchiveStatusRobotRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            var device = cloudStateStore.GetDevicesForAdministration().FirstOrDefault(candidate =>
                candidate.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null)
                return Results.NotFound(new { error = "Robot record was not found." });

            var isHidden = request.Hidden;
            var updated = CopyDevice(device, isHidden, isHidden ? DateTimeOffset.UtcNow : null);
            cloudStateStore.UpsertDeviceForAdministration(updated);
            return Results.Json(new { ok = true, deviceId = updated.DeviceId, hidden = updated.IsHidden });
        });

        app.MapGet("/api/portal/status/identity-cleanup", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            return Results.Json(cloudStateStore.PreviewRobotIdentityCleanup());
        });

        app.MapPost("/api/portal/status/identity-cleanup/reset", (
            [FromBody] ResetRobotIdentityAssociationsRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            if (!request.Confirmed)
                return Results.BadRequest(new { error = "Preview and confirm the identity cleanup before applying it." });
            return Results.Json(new { ok = true, result = cloudStateStore.ResetRobotIdentityAssociations() });
        });

        app.MapGet("/api/portal/status/robots/{deviceId}/logs", async (
            string deviceId,
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();

            var device = cloudStateStore.GetDevicesForAdministration().FirstOrDefault(candidate =>
                candidate.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null) return Results.NotFound(new { error = "Robot record was not found." });

            var robotKeys = BuildRobotArtifactKeys(device, cloudStateStore);
            var logs = (await mediaContentStore.ListAsync("logs", 200, cancellationToken))
                .Where(item =>
                {
                    var artifactDeviceId = ReadArtifactMeta(item.Meta, "deviceId");
                    return string.IsNullOrWhiteSpace(artifactDeviceId) || robotKeys.Contains(artifactDeviceId);
                })
                .OrderByDescending(item => ReadArtifactMeta(item.Meta, "storedUtc"))
                .Take(50)
                .Select(item => new
                {
                    item.Path,
                    item.ContentType,
                    category = item.Path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)
                        ? ReadArtifactMeta(item.Meta, "category")
                        : "media",
                    unassigned = string.IsNullOrWhiteSpace(ReadArtifactMeta(item.Meta, "deviceId")),
                    storedUtc = ReadArtifactMeta(item.Meta, "storedUtc"),
                    contentLength = ReadArtifactMeta(item.Meta, "contentLength"),
                    contentSha256 = ReadArtifactMeta(item.Meta, "contentSha256"),
                    identitySource = ReadArtifactMeta(item.Meta, "identitySource"),
                    observedRobotName = ReadArtifactMeta(item.Meta, "observedRobotName"),
                    observedSerialNumber = ReadArtifactMeta(item.Meta, "observedSerialNumber"),
                    mergedFromDeviceId = ReadArtifactMeta(item.Meta, "mergedFromDeviceId")
                });
            return Results.Json(new { logs });
        });

        app.MapGet("/api/portal/status/robots/{deviceId}/logs/content", async (
            string deviceId,
            string path,
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();

            var device = cloudStateStore.GetDevicesForAdministration().FirstOrDefault(candidate =>
                candidate.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null || !path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound(new { error = "Log artifact was not found." });

            var robotKeys = BuildRobotArtifactKeys(device, cloudStateStore);
            var artifact = (await mediaContentStore.ListAsync("logs", 200, cancellationToken))
                .FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                                        (string.IsNullOrWhiteSpace(ReadArtifactMeta(item.Meta, "deviceId")) ||
                                         robotKeys.Contains(ReadArtifactMeta(item.Meta, "deviceId"))));
            if (artifact is null) return Results.NotFound(new { error = "Log artifact was not found." });

            var content = await mediaContentStore.LoadAsync(path, cancellationToken);
            if (content is null) return Results.NotFound(new { error = "Log artifact content was not found." });

            return Results.Json(new
            {
                artifact.Path,
                artifact.ContentType,
                text = ReadLogText(content.Content, content.ContentType),
                contentLength = content.Content.Length
            });
        });

        app.MapGet("/api/portal/status/robots/{deviceId}/artifacts", async (
            string deviceId,
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            var device = cloudStateStore.GetDevicesForAdministration().FirstOrDefault(candidate =>
                candidate.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null) return Results.NotFound(new { error = "Robot record was not found." });

            var robotKeys = BuildRobotArtifactKeys(device, cloudStateStore);
            var listedArtifacts = await mediaContentStore.ListAsync(string.Empty, 400, cancellationToken);
            var artifacts = listedArtifacts
                .Where(item =>
                {
                    var artifactDeviceId = ReadArtifactMeta(item.Meta, "deviceId");
                    return string.IsNullOrWhiteSpace(artifactDeviceId) || robotKeys.Contains(artifactDeviceId);
                })
                .OrderByDescending(item => ReadArtifactMeta(item.Meta, "storedUtc"))
                .Take(100)
                .Select(item => new
                {
                    item.Path,
                    item.ContentType,
                    source = item.Path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ? "log" : "media",
                    artifactType = ReadArtifactMeta(item.Meta, "artifactType"),
                    category = ReadArtifactMeta(item.Meta, "category"),
                    unassigned = string.IsNullOrWhiteSpace(ReadArtifactMeta(item.Meta, "deviceId")),
                    storedUtc = ReadArtifactMeta(item.Meta, "storedUtc"),
                    contentLength = ReadArtifactMeta(item.Meta, "contentLength"),
                    contentSha256 = ReadArtifactMeta(item.Meta, "contentSha256"),
                    identitySource = ReadArtifactMeta(item.Meta, "identitySource"),
                    observedRobotName = ReadArtifactMeta(item.Meta, "observedRobotName"),
                    observedSerialNumber = ReadArtifactMeta(item.Meta, "observedSerialNumber"),
                    mergedFromDeviceId = ReadArtifactMeta(item.Meta, "mergedFromDeviceId")
                });
            var unassignedCredentials = listedArtifacts
                .Where(item => string.IsNullOrWhiteSpace(ReadArtifactMeta(item.Meta, "deviceId")))
                .Select(item => new
                {
                    fingerprint = ReadArtifactMeta(item.Meta, "awsAccessKeyFingerprint"),
                    storedUtc = ReadArtifactMeta(item.Meta, "storedUtc")
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.fingerprint))
                .GroupBy(item => item.fingerprint, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { fingerprint = group.Key, artifactCount = group.Count(), latestStoredUtc = group.Max(item => item.storedUtc) })
                .OrderByDescending(item => item.latestStoredUtc)
                .ToArray();
            return Results.Json(new { artifacts, unassignedCredentials });
        });

        app.MapGet("/api/portal/status/robots/{deviceId}/artifacts/content", async (
            string deviceId,
            string path,
            HttpRequest request,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            var device = cloudStateStore.GetDevicesForAdministration().FirstOrDefault(candidate =>
                candidate.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null || string.IsNullOrWhiteSpace(path))
                return Results.NotFound(new { error = "Artifact was not found." });

            var robotKeys = BuildRobotArtifactKeys(device, cloudStateStore);
            var artifact = (await mediaContentStore.ListAsync(string.Empty, 400, cancellationToken))
                .FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(ReadArtifactMeta(item.Meta, "deviceId")) ||
                     robotKeys.Contains(ReadArtifactMeta(item.Meta, "deviceId"))));
            if (artifact is null) return Results.NotFound(new { error = "Artifact was not found." });

            var content = await mediaContentStore.LoadAsync(path, cancellationToken);
            if (content is null) return Results.NotFound(new { error = "Artifact content was not found." });
            var previewContentType = content.ContentType;
            if (string.Equals(ReadArtifactMeta(artifact.Meta, "contentEncoding"), "ogg", StringComparison.OrdinalIgnoreCase))
                previewContentType = "audio/ogg";
            var preview = ReadArtifactPreview(content.Content, previewContentType);
            return Results.Json(new
            {
                artifact.Path,
                artifact.ContentType,
                contentLength = content.Content.Length,
                preview.Kind,
                preview.Summary,
                preview.Text,
                preview.DataUrl,
                preview.ArchiveEntries
            });
        });

        app.MapPost("/api/portal/status/robots/{deviceId}/credential-bindings", async (
            string deviceId,
            [FromBody] BindRobotCredentialRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.AccessKeyFingerprint) ||
                !System.Text.RegularExpressions.Regex.IsMatch(request.AccessKeyFingerprint, "^[a-f0-9]{16}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return Results.BadRequest(new { error = "A 16-character credential fingerprint is required." });

            try
            {
                var robot = cloudStateStore.FindDeviceByFriendlyId(deviceId);
                if (robot is null) return Results.NotFound(new { error = "Robot record was not found." });
                var binding = cloudStateStore.BindAwsCredentialFingerprint(robot.DeviceId, request.AccessKeyFingerprint,
                    "portal-admin-claim");
                var backfilledArtifacts = await BackfillArtifactsForCredentialAsync(mediaContentStore,
                    binding.AccessKeyFingerprint, binding.DeviceId, cancellationToken);
                return Results.Json(new { ok = true, binding, backfilledArtifacts });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "Robot record was not found." });
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict(new { error = "Credential fingerprint is already claimed by another robot." });
            }
        });

        app.MapPost("/api/portal/status/credential-bindings/swap", async (
            [FromBody] SwapRobotCredentialBindingsRequest request, HttpRequest httpRequest,
            PortalSessionService portalSessionService, ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore, CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            if (!request.Confirmed) return Results.BadRequest(new { error = "Confirm the credential swap before applying it." });
            try
            {
                var bindings = cloudStateStore.SwapAwsCredentialFingerprintBindings(
                    request.FirstAccessKeyFingerprint ?? string.Empty, request.SecondAccessKeyFingerprint ?? string.Empty,
                    "portal-admin-swap");
                var reassignedArtifacts = 0;
                foreach (var binding in bindings)
                    reassignedArtifacts += await ReassignCredentialBackfillArtifactsAsync(mediaContentStore,
                        binding.AccessKeyFingerprint, binding.DeviceId, cancellationToken);
                return Results.Json(new { ok = true, bindings, reassignedArtifacts });
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
        });

        app.MapGet("/api/portal/status/robots/{sourceDeviceId}/merge-preview", async (
            string sourceDeviceId, string targetDeviceId, HttpRequest request,
            PortalSessionService portalSessionService, ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore, CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            try
            {
                var preview = await BuildRobotMergePreviewAsync(sourceDeviceId, targetDeviceId, cloudStateStore,
                    mediaContentStore, cancellationToken);
                var previewToken = portalSessionService.CreateRobotMergePreviewToken(session,
                    preview.SourceDeviceId, preview.TargetDeviceId, preview.SnapshotFingerprint);
                return Results.Json(new
                {
                    sourceDeviceId = preview.SourceDeviceId,
                    targetDeviceId = preview.TargetDeviceId,
                    sessionCount = preview.SessionIds.Count,
                    credentialBindingCount = preview.CredentialFingerprints.Count,
                    artifactCount = preview.Artifacts.Count,
                    previewToken,
                    note = "Household loops and people are not merged automatically. The source robot is archived."
                });
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        app.MapPost("/api/portal/status/robots/{sourceDeviceId}/merge", async (
            string sourceDeviceId, [FromBody] MergeRobotRequest request, HttpRequest httpRequest,
            PortalSessionService portalSessionService, ICloudStateStore cloudStateStore,
            IMediaContentStore mediaContentStore, CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            try
            {
                var preview = await BuildRobotMergePreviewAsync(sourceDeviceId, request.TargetDeviceId ?? string.Empty,
                    cloudStateStore, mediaContentStore, cancellationToken);
                if (!portalSessionService.TryValidateRobotMergePreviewToken(request.PreviewToken, session,
                        preview.SourceDeviceId, preview.TargetDeviceId, preview.SnapshotFingerprint))
                    return Results.Conflict(new
                    {
                        error = "The merge preview is missing, expired, or stale. Review the merge again before applying it."
                    });
                var result = cloudStateStore.MergeRobotRecordsForAdministration(
                    preview.SourceDeviceId, preview.TargetDeviceId,
                    new RobotMergePrecondition(preview.SessionIds, preview.CredentialFingerprints));
                var artifactResult = await ReassignReviewedArtifactsAsync(mediaContentStore,
                    result.SourceDeviceId, result.TargetDeviceId, preview.Artifacts, cancellationToken);
                return Results.Json(new
                {
                    ok = true,
                    result,
                    migratedArtifacts = artifactResult.MigratedCount,
                    skippedArtifacts = artifactResult.SkippedPaths,
                    partial = artifactResult.SkippedPaths.Count > 0
                });
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Robot record was not found." }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });

        app.MapGet("/api/portal/status/robots/{deviceId}/identity-suggestion", async (
            string deviceId, HttpRequest request, PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore, IMediaContentStore mediaContentStore,
            RobotIdentitySuggestionStore identitySuggestionStore, CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            var inventory = cloudStateStore.GetDevicesForAdministration();
            var device = inventory.FirstOrDefault(item =>
                item.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null) return Results.NotFound(new { error = "Robot record was not found." });

            foreach (var robotSession in cloudStateStore.GetSessions()
                         .Where(item => SessionProvidesIdentityEvidenceForDevice(item, device)))
            {
                foreach (var key in new[] { "robotFriendlyId", "friendlyId", "registeredRobotId", "robotId" })
                {
                    if (!robotSession.Metadata.TryGetValue(key, out var value) || value is null) continue;
                    identitySuggestionStore.Observe(device.DeviceId, value.ToString(), "stored-session", key);
                }
            }
            var artifactKeys = BuildRobotArtifactKeys(device, cloudStateStore);
            var artifacts = await mediaContentStore.ListAsync(string.Empty, 1000, cancellationToken);
            var matchingArtifacts = artifacts.Where(item =>
                    artifactKeys.Contains(ReadArtifactMeta(item.Meta, "deviceId")))
                .OrderByDescending(item =>
                    item.Path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
                    item.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                    item.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                    item.ContentType.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                .Take(24)
                .ToArray();
            foreach (var artifact in matchingArtifacts)
            {
                foreach (var key in new[] { "robot_name", "robotName", "robot_id", "robotId" })
                {
                    var candidate = ReadArtifactMeta(artifact.Meta, key);
                    identitySuggestionStore.Observe(device.DeviceId, candidate, $"artifact:{artifact.Path}", key);
                }
            }

            // Upload manifests generally contain attribution metadata, while the
            // robot name is often inside the JSON/gzip payload itself. Load only the
            // newest bounded set, concurrently, so Azure latency does not accumulate.
            var artifactContents = await Task.WhenAll(matchingArtifacts.Select(async artifact => new
            {
                artifact,
                content = await TryLoadIdentityArtifactAsync(mediaContentStore, artifact.Path, cancellationToken)
            }));
            foreach (var item in artifactContents)
            {
                var artifact = item.artifact;
                var content = item.content;
                var observed = RobotLogIdentityEvidence.Extract(content?.Content ?? Array.Empty<byte>());
                if (observed.HasConflictingEvidence ||
                    (observed.HasSerialEvidence && !string.IsNullOrWhiteSpace(device.VerifiedSerialNumber) &&
                     !string.Equals(observed.SerialNumber, device.VerifiedSerialNumber, StringComparison.OrdinalIgnoreCase)))
                    continue;
                foreach (var candidate in ExtractIdentityCandidates(content))
                    identitySuggestionStore.Observe(device.DeviceId, candidate.Value,
                        $"artifact-content:{artifact.Path}", candidate.Field);
            }

            // Keep strong evidence from unassigned historical logs visible for review,
            // and suggest a name only when an exact, unique identifier links it to this robot.
            var unassignedArtifacts = artifacts
                .Where(item => string.IsNullOrWhiteSpace(ReadArtifactMeta(item.Meta, "deviceId")))
                .Where(item => item.Path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
                               item.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                               item.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                               item.ContentType.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => ReadArtifactMeta(item.Meta, "storedUtc"))
                .Take(24)
                .ToArray();
            var unassignedContents = await Task.WhenAll(unassignedArtifacts.Select(async artifact => new
            {
                artifact,
                content = await TryLoadIdentityArtifactAsync(mediaContentStore, artifact.Path, cancellationToken)
            }));
            var unassignedEvidence = new List<object>();
            foreach (var item in unassignedContents)
            {
                var observed = RobotLogIdentityEvidence.Extract(item.content?.Content ?? Array.Empty<byte>());
                var matched = observed.ResolveDevice(inventory);
                if (matched?.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(observed.RobotName))
                    identitySuggestionStore.Observe(device.DeviceId, observed.RobotName, $"artifact-content:{item.artifact.Path}", "robotName");
                if (!string.IsNullOrWhiteSpace(observed.RobotName))
                    unassignedEvidence.Add(new { path = item.artifact.Path, field = "robotName", value = observed.RobotName, storedUtc = ReadArtifactMeta(item.artifact.Meta, "storedUtc") });
                if (!string.IsNullOrWhiteSpace(observed.SerialNumber))
                    unassignedEvidence.Add(new { path = item.artifact.Path, field = "serialNumber", value = observed.SerialNumber, storedUtc = ReadArtifactMeta(item.artifact.Meta, "storedUtc") });
            }

            var suggestion = identitySuggestionStore.GetSuggestion(device.DeviceId, inventory);
            if (suggestion is null)
                return Results.Json(new { suggested = false, currentRobotId = device.RobotId, evidence = Array.Empty<object>(), unassignedEvidence });

            return Results.Json(new
            {
                suggested = true,
                suggestion.ProposedRobotId,
                currentRobotId = device.RobotId,
                suggestion.Action,
                suggestion.TargetDeviceId,
                suggestion.CandidateTargetDeviceIds,
                suggestion.ObservationCount,
                suggestion.FirstObservedUtc,
                suggestion.LastObservedUtc,
                suggestion.Evidence,
                unassignedEvidence
            });
        });

        app.MapPost("/api/portal/status/robots/{deviceId}/identity/restore-from-device-id", (
            string deviceId, [FromBody] RestoreRobotIdentityRequest request,
            HttpRequest httpRequest, PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            if (!request.Confirmed) return Results.BadRequest(new { error = "Confirm the identity correction." });
            var device = cloudStateStore.GetDevicesForAdministration().FirstOrDefault(item =>
                item.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null) return Results.NotFound(new { error = "Robot record was not found." });
            if (!IsSafeIdentityName(device.DeviceId))
                return Results.BadRequest(new { error = "This device ID is not a canonical robot-name candidate." });
            if (device.DeviceId.Equals(device.RobotId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "The robot identity already matches its device ID." });
            try
            {
                var restored = cloudStateStore.RenameDeviceForAdministration(device.DeviceId, device.DeviceId);
                return Results.Json(new { ok = true, device = restored });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        app.MapPost("/api/portal/status/robots/{deviceId}/identity-suggestion/apply", async (
            string deviceId, [FromBody] ApplyIdentitySuggestionRequest request,
            HttpRequest httpRequest, PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore, IMediaContentStore mediaContentStore,
            RobotIdentitySuggestionStore identitySuggestionStore,
            CancellationToken cancellationToken) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            if (!IsSafeIdentityName(request.ProposedRobotId))
                return Results.BadRequest(new { error = "Provide a valid robot ID." });
            var inventory = cloudStateStore.GetDevicesForAdministration();
            var source = inventory.FirstOrDefault(item =>
                item.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (source is null) return Results.NotFound(new { error = "Robot record was not found." });
            var proposedRobotId = request.ProposedRobotId!.Trim();
            var pendingSuggestion = identitySuggestionStore.GetSuggestion(source.DeviceId, inventory);
            if (pendingSuggestion is null ||
                !pendingSuggestion.ProposedRobotId.Equals(proposedRobotId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "That identity suggestion is no longer pending." });
            if (string.IsNullOrWhiteSpace(request.ExpectedAction) ||
                !pendingSuggestion.Action.Equals(request.ExpectedAction.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(pendingSuggestion.TargetDeviceId, request.ExpectedTargetDeviceId,
                    StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new
                {
                    error = "The identity suggestion changed after it was reviewed. Scan again before applying it."
                });
            if (pendingSuggestion.Action.Equals("ambiguous", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new
                {
                    error = "Multiple active robot records match this identity. Review and merge the records manually."
                });
            if (pendingSuggestion.Action.Equals("merge", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pendingSuggestion.TargetDeviceId))
            {
                RobotMergeResult result;
                try
                {
                    result = cloudStateStore.MergeRobotRecordsForAdministration(
                        source.DeviceId, pendingSuggestion.TargetDeviceId);
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Conflict(new { error = exception.Message });
                }
                var migratedArtifacts = await ReassignArtifactsAsync(mediaContentStore, result.SourceDeviceId,
                    result.TargetDeviceId, "robot-identity-suggestion-merge", cancellationToken);
                identitySuggestionStore.Dismiss(source.DeviceId, proposedRobotId);
                return Results.Json(new { ok = true, action = "merge", result, migratedArtifacts });
            }
            if (!pendingSuggestion.Action.Equals("rename", StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "The identity suggestion cannot be applied automatically." });
            var renamed = cloudStateStore.RenameDeviceForAdministration(source.DeviceId, proposedRobotId);
            identitySuggestionStore.Dismiss(source.DeviceId, proposedRobotId);
            return Results.Json(new { ok = true, action = "rename", device = renamed });
        });

        app.MapPost("/api/portal/status/sessions/{sessionId}/link", (
            string sessionId,
            [FromBody] LinkStatusSessionRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            var liveSession = cloudStateStore.GetSessions().FirstOrDefault(candidate =>
                candidate.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase));
            var observedDeviceId = liveSession?.DeviceId ?? request.ObservedDeviceId;
            var lastSeenUtc = liveSession?.LastSeenUtc ?? request.LastSeenUtc;
            if (string.IsNullOrWhiteSpace(observedDeviceId) || lastSeenUtc is null)
                return Results.NotFound(new { error = "Live session was not found on this replica and its observed identity was not supplied." });
            if (DateTimeOffset.UtcNow - lastSeenUtc.Value > StatusHeartbeatWindow ||
                lastSeenUtc.Value > DateTimeOffset.UtcNow.AddMinutes(1))
                return Results.BadRequest(new { error = "Only a currently live session can be linked." });
            if (string.IsNullOrWhiteSpace(request.DeviceId) ||
                cloudStateStore.FindDeviceByFriendlyId(request.DeviceId) is null)
                return Results.BadRequest(new { error = "Choose a registered robot record." });

            var linked = liveSession is not null
                ? cloudStateStore.BindSessionToDevice(sessionId, request.DeviceId)
                : cloudStateStore.BindObservedIdentityToDevice(observedDeviceId, request.DeviceId);
            return linked
                ? Results.Json(new
                {
                    ok = true,
                    sessionId,
                    observedDeviceId,
                    deviceId = request.DeviceId,
                    linkedAcrossReplica = liveSession is null
                })
                : Results.NotFound(new { error = "Observed identity or robot record was not found." });
        });

        app.MapDelete("/api/portal/status/sessions/{sessionId}/link", (
            string sessionId,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore) =>
        {
            var session = ResolvePortalSession(httpRequest, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            return cloudStateStore.ClearSessionDeviceBinding(sessionId)
                ? Results.Json(new { ok = true, sessionId })
                : Results.NotFound(new { error = "Session was not found." });
        });

        app.MapPost("/api/portal/status/network/reports", (
            [FromBody] FleetServerPresenceReportRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService,
            ICloudStateStore cloudStateStore,
            FleetNetworkPresenceRegistry fleetNetworkPresenceRegistry) =>
        {
            var session = ResolvePortalSession(httpRequest, request.PortalSessionToken, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.ServerId) || string.IsNullOrWhiteSpace(request.CanonicalHost))
                return Results.BadRequest(new { error = "serverId and canonicalHost are required." });

            var trustedServer = cloudStateStore.FindTrustedServer(request.CanonicalHost);
            if (trustedServer is null || !trustedServer.IsActive || !trustedServer.ParticipatesInCloudSync ||
                !trustedServer.ServerId.Equals(request.ServerId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Reports require an active trusted cloud-sync server." });

            var report = new FleetServerPresenceReport(
                trustedServer.ServerId,
                trustedServer.CanonicalHost,
                request.InstanceId?.Trim() ?? string.Empty,
                request.ConnectedRobotIds ?? [],
                request.ConnectionCount ?? 0,
                DateTimeOffset.UtcNow);
            fleetNetworkPresenceRegistry.Report(report);
            return Results.Json(new { ok = true, report });
        });

        app.MapPost("/api/network/fleet-presence", (
            [FromBody] FleetPeerPresencePayload payload,
            HttpRequest request,
            IConfiguration configuration,
            ICloudStateStore cloudStateStore,
            FleetNetworkPresenceRegistry fleetNetworkPresenceRegistry) =>
        {
            var peerSyncPolicy = FleetPeerSyncPolicy.FromConfiguration(configuration);
            if (!peerSyncPolicy.Enabled || !peerSyncPolicy.Allows(payload.CanonicalHost))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var sharedKey = configuration["OpenJibo:FleetNetwork:PeerSyncSharedKey"];
            if (string.IsNullOrWhiteSpace(sharedKey))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var senderId = request.Headers[FleetPeerSyncAuthentication.ServerIdHeader].FirstOrDefault();
            var timestamp = request.Headers[FleetPeerSyncAuthentication.TimestampHeader].FirstOrDefault();
            var suppliedHash = request.Headers[FleetPeerSyncAuthentication.PayloadHashHeader].FirstOrDefault();
            var signature = request.Headers[FleetPeerSyncAuthentication.SignatureHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(timestamp) ||
                string.IsNullOrWhiteSpace(suppliedHash) || string.IsNullOrWhiteSpace(signature) ||
                !senderId.Equals(payload.ServerId, StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(timestamp, out var timestampSeconds))
                return Results.Unauthorized();

            var signedAtUtc = DateTimeOffset.FromUnixTimeSeconds(timestampSeconds);
            if (Math.Abs((DateTimeOffset.UtcNow - signedAtUtc).TotalMinutes) > 2)
                return Results.Unauthorized();

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var computedHash = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant();
            if (!computedHash.Equals(suppliedHash, StringComparison.OrdinalIgnoreCase) ||
                !FleetPeerSyncAuthentication.Verify(senderId, timestamp, computedHash, signature, sharedKey))
                return Results.Unauthorized();

            var trustedServer = cloudStateStore.FindTrustedServer(payload.CanonicalHost);
            // Trusted-server records may have a locally generated ServerId. The canonical host is the
            // cross-deployment identity, and the signed sender id must agree with the payload.
            if (trustedServer is null || !trustedServer.IsActive || !trustedServer.ParticipatesInCloudSync)
                return Results.Forbid();

            var report = new FleetServerPresenceReport(
                trustedServer.ServerId,
                trustedServer.CanonicalHost,
                payload.InstanceId?.Trim() ?? string.Empty,
                payload.ConnectedRobotIds ?? [],
                payload.ConnectionCount,
                DateTimeOffset.UtcNow);
            fleetNetworkPresenceRegistry.Report(report);
            return Results.Json(new { ok = true, report });
        });

        app.MapPost("/api/portal/logout", (
            [FromBody] PortalLogoutRequest request,
            HttpRequest httpRequest,
            PortalSessionService portalSessionService) =>
        {
            var token = ResolvePortalSessionToken(httpRequest, request.PortalSessionToken);
            portalSessionService.RevokeSession(token);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/portal/server/logs", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            IConfiguration configuration) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            var logDirectory = ResolvePortalConfiguredPath(configuration,
                "OpenJibo:Logging:DirectoryPath",
                "captures/logs");
            var logFileName = configuration["OpenJibo:Logging:FileName"] ?? "openjibo-.log";

            // Get the most recent log file
            var logFiles = Directory.GetFiles(logDirectory, logFileName.Replace("-", "*"))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .ToArray();

            if (logFiles.Length == 0)
                return Results.Json(new { logs = "", hasLogs = false });

            var latestLogFile = logFiles[0];
            var tailLines = int.TryParse(request.Query["lines"], out var lines) && lines > 0 ? lines : 100;
            var offset = long.TryParse(request.Query["offset"], out var fileOffset) ? fileOffset : 0L;
            var requestedFileName = request.Query["fileName"].ToString();
            if (!string.IsNullOrWhiteSpace(requestedFileName) &&
                !string.Equals(requestedFileName, Path.GetFileName(latestLogFile), StringComparison.Ordinal))
            {
                // Rotation is detected atomically with the read; never apply the old file's offset.
                offset = 0;
            }

            var logContent = ReadLogFileWithOffset(latestLogFile, offset, tailLines);
            var newOffset = new FileInfo(latestLogFile).Length;

            return Results.Json(new
            {
                logs = logContent,
                offset = newOffset,
                hasLogs = true,
                fileName = Path.GetFileName(latestLogFile)
            });
        });

        // Same-origin SSE tunnel for the optional robot log streamer. The robot target is
        // restricted to private IPv4 space so this diagnostic endpoint cannot become an
        // arbitrary server-side request proxy.
        app.MapGet("/api/portal/lrd/stream", async (
            HttpContext context,
            PortalSessionService portalSessionService,
            IHttpClientFactory httpClientFactory) =>
        {
            var session = ResolvePortalSession(context.Request,
                context.Request.Query["portalSessionToken"].ToString(), portalSessionService);
            if (session is null || !IsAdminSession(session))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var ipText = context.Request.Query["ip"].ToString();
            var scheme = context.Request.Query["scheme"].ToString().ToLowerInvariant();
            var portText = context.Request.Query["port"].ToString();
            var token = context.Request.Headers["X-Robot-Stream-Token"].ToString();
            if (string.IsNullOrWhiteSpace(token)) token = context.Request.Query["token"].ToString();
            if (!IPAddress.TryParse(ipText, out var ip) || !IsPrivateIpv4(ip) ||
                scheme is not ("http" or "https") ||
                !int.TryParse(portText, out var port) || port is < 1 or > 65535)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "A private IPv4 robot target, scheme, and valid port are required." });
                return;
            }

            var client = httpClientFactory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            var upstream = new UriBuilder(scheme, ip.ToString(), port, "stream").Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, upstream);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                if (!response.IsSuccessStatusCode)
                {
                    context.Response.StatusCode = (int)response.StatusCode;
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Browser disconnects are normal for SSE.
            }
            catch
            {
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
        });

        app.MapGet("/api/portal/lrd/beacons", (
            HttpRequest request,
            PortalSessionService portalSessionService,
            RobotDiagnosticBeaconStore beaconStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            return Results.Json(new { beacons = beaconStore.GetActive(DateTimeOffset.UtcNow) });
        });

        app.MapGet("/api/portal/lrd/beacons/{robotId}/logs", (
            HttpRequest request,
            string robotId,
            PortalSessionService portalSessionService,
            RobotDiagnosticBeaconStore beaconStore) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session)) return Results.Unauthorized();
            var now = DateTimeOffset.UtcNow;
            return Results.Json(new
            {
                robotId,
                lines = beaconStore.Snapshot(robotId, now),
                active = beaconStore.GetActive(now).Any(beacon =>
                    string.Equals(beacon.RobotId, robotId, StringComparison.OrdinalIgnoreCase))
            });
        });

        app.MapPost("/api/portal/lrd/beacon", async (
            HttpRequest request,
            RobotDiagnosticBeaconStore beaconStore,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var robotId = request.Headers["X-Jibo-RobotId"].ToString().Trim();
            var suppliedToken = request.Headers["X-Jibo-Diagnostic-Token"].ToString();
            var configuredToken = configuration[$"OpenJibo:Diagnostics:BeaconTokens:{robotId}"];
            if (string.IsNullOrWhiteSpace(robotId) || string.IsNullOrWhiteSpace(configuredToken) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(suppliedToken), Encoding.UTF8.GetBytes(configuredToken)))
                return Results.Unauthorized();

            var payload = await request.ReadFromJsonAsync<RobotDiagnosticBeaconPublishRequest>(cancellationToken);
            if (payload is null || payload.Lines is null || payload.Lines.Count > 100 ||
                payload.Lines.Any(line => line is null || line.Length > 16_384))
                return Results.BadRequest(new { error = "A beacon payload with at most 100 log lines is required." });

            beaconStore.Publish(robotId, payload.Lines, DateTimeOffset.UtcNow);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/api/portal/server/logs/diagnostics-status", (
            HttpRequest request,
            PortalSessionService portalSessionService) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            return Results.Json(new { disabled = PortalLogPollingDiagnosticsState.DisableServerLogsEndpointLogging });
        });

        app.MapPost("/api/portal/server/logs/toggle-diagnostics", (
            HttpRequest request,
            PortalSessionService portalSessionService) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null || !IsAdminSession(session))
                return Results.Unauthorized();

            PortalLogPollingDiagnosticsState.DisableServerLogsEndpointLogging = !PortalLogPollingDiagnosticsState.DisableServerLogsEndpointLogging;
            return Results.Json(new { ok = true, disabled = PortalLogPollingDiagnosticsState.DisableServerLogsEndpointLogging });
        });

        app.MapGet("/api/portal/home-assistant/links", (
            PortalSessionService portalSessionService,
            IUserIntegrationStore integrationStore,
            HomeAssistantConnectionRegistry registry,
            HttpRequest request) =>
        {
            var session = ResolvePortalSession(request, null, portalSessionService);
            if (session is null)
                return Results.Unauthorized();

            var link = integrationStore.FindLinkForJibo(session.FriendlyId, session.FriendlyId, session.OwnershipEpoch ?? "unowned");
            return Results.Json(new
            {
                links = link is null
                    ? Array.Empty<object>()
                    : new[] { BuildHomeAssistantPayload(link, registry) }
            });
        });
    }

    private static object BuildStatusSummaryPayload(ICloudStateStore cloudStateStore,
        RobotPresenceRegistry robotPresenceRegistry, FleetNetworkPresenceRegistry fleetNetworkPresenceRegistry,
        OpenJiboServerIdentity serverIdentity, RobotIdentitySuggestionStore identitySuggestionStore,
        ReleaseSmokeAuthorizationOptions releaseSmokeAuthorization, IConfiguration configuration,
        bool includeHidden)
    {
        var now = DateTimeOffset.UtcNow;
        using var process = Process.GetCurrentProcess();
        var processStartUtc = process.StartTime.ToUniversalTime();
        var persistence = cloudStateStore.GetPersistenceStateInfo();
        var deploymentMode = configuration["OpenJibo:Deployment:Mode"] ?? "unspecified";
        var revision = Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ?? "local";
        var hasLocalWhisperSetting = bool.TryParse(
            configuration["OpenJibo:Stt:EnableLocalWhisperCpp"], out var localWhisperEnabled);
        var allDevices = cloudStateStore.GetDevicesForAdministration();
        var sessions = cloudStateStore.GetSessions();
        // GetSessions intentionally includes durable issued-token records. Those
        // records are authentication history, not live WebSocket sessions, and a
        // client retrying token issuance must not flood or distort fleet presence.
        var activeSessions = sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.Token) &&
                              cloudStateStore.FindActiveSessionByToken(session.Token)?.SessionId
                                  .Equals(session.SessionId, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        var recentSessions = activeSessions
            .Where(session => now - session.LastSeenUtc <= StatusHeartbeatWindow)
            .ToArray();
        var liveConnections = robotPresenceRegistry.GetLiveConnections();
        var robots = BuildReconciledRobotStatuses(allDevices, activeSessions, recentSessions, liveConnections, now, includeHidden)
            .Select(robot => robot with
            {
                IdentitySuggestion = identitySuggestionStore.GetSuggestion(robot.DeviceId, allDevices)
            })
            .ToArray();

        var localConnectedRobotIds = robots
            .Where(robot => robot.Connected)
            .Select(robot => robot.DeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        fleetNetworkPresenceRegistry.Report(new FleetServerPresenceReport(
            serverIdentity.ServerId,
            serverIdentity.CanonicalHost,
            serverIdentity.InstanceId,
            localConnectedRobotIds,
            liveConnections.Count,
            now,
            IsLocal: true));
        var serverReports = fleetNetworkPresenceRegistry.GetFreshReports(TimeSpan.FromMinutes(2), now);
        var networkConnectedRobotIds = serverReports
            .SelectMany(report => report.ConnectedRobotIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var trustedCloudSyncServers = cloudStateStore.GetTrustedServers()
            .Where(server => server.IsActive && server.ParticipatesInCloudSync)
            .ToArray();

        var latestSeenUtc = recentSessions.Length > 0 ? recentSessions.Max(session => session.LastSeenUtc) : (DateTimeOffset?)null;
        var oldestLiveSessionCreatedUtc = recentSessions.Length > 0 ? recentSessions.Min(session => session.CreatedUtc) : (DateTimeOffset?)null;

        return new
        {
            generatedAtUtc = now,
            service = new
            {
                version = OpenJiboCloudBuildInfo.Version,
                startedAtUtc = processStartUtc,
                uptimeSeconds = (long)(now - processStartUtc).TotalSeconds,
                uptimeLabel = FormatDuration(now - processStartUtc),
                deployment = new
                {
                    mode = deploymentMode,
                    revision,
                    replica = serverIdentity.InstanceId,
                    instanceId = $"{revision}/{serverIdentity.InstanceId}",
                    canonicalApiHostname = serverIdentity.CanonicalHost,
                    stateBackend = configuration["OpenJibo:State:Backend"] ?? "unspecified",
                    personalMemoryBackend = configuration["OpenJibo:PersonalMemory:Backend"] ?? "unspecified",
                    localWhisperEnabled,
                    managedConfigurationCompatible =
                        !deploymentMode.Equals("managed", StringComparison.OrdinalIgnoreCase) ||
                        hasLocalWhisperSetting && !localWhisperEnabled,
                    persistenceSchemaVersion = persistence.SchemaVersion,
                    releaseSmokeEnabled = releaseSmokeAuthorization.Enabled
                }
            },
            persistence,
            inventory = allDevices
                .OrderBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(device => new
                {
                    deviceId = device.DeviceId,
                    robotId = device.RobotId,
                    friendlyName = device.FriendlyName,
                    isHidden = device.IsHidden,
                    verifiedSerialNumber = device.VerifiedSerialNumber,
                    serialEvidenceSource = device.SerialEvidenceSource,
                    serialEvidenceVerifiedUtc = device.SerialEvidenceVerifiedUtc,
                    registrationSource = RobotRegistrationSources.Normalize(
                        device.RegistrationSource,
                        device.DeviceId)
                })
                .ToArray(),
            credentialBindings = cloudStateStore.GetRobotCredentialBindings()
                .Select(binding => new
                {
                    accessKeyFingerprint = binding.AccessKeyFingerprint,
                    binding.DeviceId,
                    binding.ClaimedUtc,
                    binding.ClaimSource
                })
                .ToArray(),
            fleet = new
            {
                registeredRobots = allDevices.Count,
                visibleRobots = robots.Length,
                hiddenRobots = allDevices.Count(device => device.IsHidden),
                syntheticRobots = allDevices.Count(device => RobotRegistrationSources.IsSynthetic(device.RegistrationSource)),
                activeRobots = robots.Count(robot => robot.IsActive),
                connectedRobots = robots.Count(robot => robot.Connected),
                sleepingRobots = robots.Count(robot => robot.Presence == "sleeping"),
                recentlySeenRobots = robots.Count(robot => robot.Presence == "recently-seen"),
                totalSessions = activeSessions.Length,
                liveSessions = recentSessions.Length,
                staleSessions = activeSessions.Count(session => now - session.LastSeenUtc > StatusHeartbeatWindow),
                latestSeenUtc,
                oldestLiveSessionCreatedUtc,
                averageHeartbeatAgeSeconds = recentSessions.Length > 0
                    ? recentSessions.Average(session => (now - session.LastSeenUtc).TotalSeconds)
                    : (double?)null
            },
            serverFleet = new
            {
                localServer = new
                {
                    serverIdentity.ServerId,
                    serverIdentity.CanonicalHost,
                    serverIdentity.InstanceId,
                    connectedRobots = localConnectedRobotIds.Length,
                    liveConnections = liveConnections.Count
                },
                network = new
                {
                    knownServers = trustedCloudSyncServers.Length,
                    reportingServers = serverReports.Count,
                    connectedRobots = networkConnectedRobotIds.Length,
                    reportFreshnessSeconds = 120
                },
                servers = serverReports.Select(report => new
                {
                    report.ServerId,
                    report.CanonicalHost,
                    report.InstanceId,
                    report.IsLocal,
                    connectedRobots = report.ConnectedRobotIds.Count,
                    report.ConnectionCount,
                    report.ReportedAtUtc,
                    reportAgeSeconds = (now - report.ReportedAtUtc).TotalSeconds
                })
            },
            robots,
            recentSessions = recentSessions
                .OrderByDescending(session => session.LastSeenUtc)
                .Take(10)
                .Select(session => new
                {
                    session.SessionId,
                    session.Kind,
                    session.DeviceId,
                    // Observed runtime IDs remain visible as session.DeviceId, but they are
                    // never silently presented as an inventory link.
                    registeredDeviceId = ReadSessionMetadata(session, "registeredDeviceId"),
                    registeredRobotId = ReadSessionMetadata(session, "registeredRobotId"),
                    sessionBindingAudit = ReadSessionMetadata(session, "sessionBindingAudit"),
                    session.HostName,
                    session.Path,
                    session.CreatedUtc,
                    session.LastSeenUtc,
                    heartbeatAgeSeconds = (now - session.LastSeenUtc).TotalSeconds
                })
                .ToArray(),
            explicitSessionBindings = sessions
                .Where(session => !string.IsNullOrWhiteSpace(ReadSessionMetadata(session, "registeredDeviceId")))
                .OrderByDescending(session => session.LastSeenUtc)
                .Select(session => new
                {
                    session.SessionId,
                    session.Kind,
                    session.DeviceId,
                    registeredDeviceId = ReadSessionMetadata(session, "registeredDeviceId"),
                    registeredRobotId = ReadSessionMetadata(session, "registeredRobotId"),
                    session.HostName,
                    session.Path,
                    session.CreatedUtc,
                    session.LastSeenUtc,
                    heartbeatAgeSeconds = (now - session.LastSeenUtc).TotalSeconds,
                    isStale = now - session.LastSeenUtc > StatusHeartbeatWindow
                })
                .ToArray()
        };
    }

    private static string ReadArtifactMeta(IReadOnlyDictionary<string, object?> meta, string key)
    {
        if (!meta.TryGetValue(key, out var value) || value is null) return string.Empty;
        return value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : value.ToString() ?? string.Empty;
    }

    private static HashSet<string> BuildRobotArtifactKeys(DeviceRegistration device, ICloudStateStore cloudStateStore)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            device.DeviceId,
            device.RobotId,
            device.FriendlyName
        };

        // Merged/grouped robots retain their observed runtime IDs on sessions.
        // Include those aliases when querying artifacts, while requiring the
        // explicit admin binding that grouped the session to this inventory row.
        foreach (var session in cloudStateStore.GetSessions().Where(item => SessionMatchesDevice(item, device)))
        {
            foreach (var identity in GetSessionIdentityValues(session))
                keys.Add(identity);
        }

        return keys;
    }

    private static async Task<int> BackfillArtifactsForCredentialAsync(IMediaContentStore mediaContentStore,
        string accessKeyFingerprint, string deviceId, CancellationToken cancellationToken)
    {
        var updated = 0;
        await foreach (var artifact in mediaContentStore.EnumerateAsync(string.Empty, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(ReadArtifactMeta(artifact.Meta, "deviceId")) ||
                !ReadArtifactMeta(artifact.Meta, "awsAccessKeyFingerprint")
                    .Equals(accessKeyFingerprint, StringComparison.OrdinalIgnoreCase))
                continue;
            var content = await mediaContentStore.LoadAsync(artifact.Path, cancellationToken);
            if (content is null) continue;
            var meta = new Dictionary<string, object?>(content.Meta, StringComparer.OrdinalIgnoreCase)
            {
                ["deviceId"] = deviceId,
                ["identitySource"] = "aws-credential-binding-backfill",
                ["credentialClaimedUtc"] = DateTimeOffset.UtcNow
            };
            await mediaContentStore.StoreAsync(artifact.Path, content.ContentType, content.Content, meta, cancellationToken);
            updated++;
        }
        return updated;
    }

    private static async Task<int> ReassignArtifactsAsync(IMediaContentStore mediaContentStore, string sourceDeviceId,
        string targetDeviceId, string identitySource, CancellationToken cancellationToken)
    {
        var updated = 0;
        await foreach (var artifact in mediaContentStore.EnumerateAsync(string.Empty, cancellationToken))
        {
            if (!ReadArtifactMeta(artifact.Meta, "deviceId").Equals(sourceDeviceId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            var content = await mediaContentStore.LoadAsync(artifact.Path, cancellationToken);
            if (content is null) continue;
            var meta = new Dictionary<string, object?>(content.Meta, StringComparer.OrdinalIgnoreCase)
            {
                ["deviceId"] = targetDeviceId,
                ["identitySource"] = identitySource,
                ["mergedFromDeviceId"] = sourceDeviceId,
                ["mergedUtc"] = DateTimeOffset.UtcNow
            };
            await mediaContentStore.StoreAsync(artifact.Path, content.ContentType, content.Content, meta, cancellationToken);
            updated++;
        }
        return updated;
    }

    private static async Task<RobotMergePreview> BuildRobotMergePreviewAsync(
        string sourceDeviceId,
        string targetDeviceId,
        ICloudStateStore cloudStateStore,
        IMediaContentStore mediaContentStore,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceDeviceId) || string.IsNullOrWhiteSpace(targetDeviceId) ||
            sourceDeviceId.Equals(targetDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose two different registered robots.");

        var adminDevices = cloudStateStore.GetDevicesForAdministration();
        var source = adminDevices.FirstOrDefault(device =>
                         device.DeviceId.Equals(sourceDeviceId.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                     throw new KeyNotFoundException("Source robot record was not found.");
        var target = adminDevices.FirstOrDefault(device =>
                         device.DeviceId.Equals(targetDeviceId.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                     throw new KeyNotFoundException("Target robot record was not found.");
        if (source.IsHidden || source.ArchivedUtc is not null || target.IsHidden || target.ArchivedUtc is not null)
            throw new InvalidOperationException("Manual merge requires two visible, unarchived robot records.");
        if (source.DeviceId.Equals(cloudStateStore.GetRobot().DeviceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The active robot record must be the canonical target, not the merge source.");

        var sessionIds = cloudStateStore.GetSessions()
            .Where(item => source.DeviceId.Equals(item.DeviceId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.SessionId)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var credentialFingerprints = cloudStateStore.GetRobotCredentialBindings()
            .Where(item => source.DeviceId.Equals(item.DeviceId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.AccessKeyFingerprint)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var artifacts = new List<RobotMergeArtifactReference>();
        await foreach (var item in mediaContentStore.EnumerateAsync(string.Empty, cancellationToken))
        {
            if (!ReadArtifactMeta(item.Meta, "deviceId")
                    .Equals(source.DeviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            var content = await mediaContentStore.LoadAsync(item.Path, cancellationToken);
            if (content is not null)
                artifacts.Add(new RobotMergeArtifactReference(item.Path,
                    Convert.ToHexString(SHA256.HashData(content.Content)).ToLowerInvariant()));
        }
        artifacts = artifacts.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();

        var snapshotJson = JsonSerializer.Serialize(new
        {
            source = BuildRobotMergeDeviceSnapshot(source),
            target = BuildRobotMergeDeviceSnapshot(target),
            sessionIds,
            credentialFingerprints,
            artifacts
        });
        var snapshotFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson)))
            .ToLowerInvariant();
        return new RobotMergePreview(source.DeviceId, target.DeviceId, sessionIds, credentialFingerprints,
            artifacts, snapshotFingerprint);
    }

    private static object BuildRobotMergeDeviceSnapshot(DeviceRegistration device) => new
    {
        device.DeviceId,
        device.RobotId,
        device.FriendlyName,
        device.IsActive,
        device.IsHidden,
        archivedUtc = device.ArchivedUtc?.ToUnixTimeMilliseconds(),
        device.VerifiedSerialNumber,
        device.RegistrationSource,
        hostMappings = device.HostMappings
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new { item.Key, item.Value })
            .ToArray()
    };

    private static async Task<RobotMergeArtifactResult> ReassignReviewedArtifactsAsync(
        IMediaContentStore mediaContentStore,
        string sourceDeviceId,
        string targetDeviceId,
        IReadOnlyList<RobotMergeArtifactReference> reviewedArtifacts,
        CancellationToken cancellationToken)
    {
        var updated = 0;
        var skipped = new List<string>();
        foreach (var artifact in reviewedArtifacts)
        {
            var content = await mediaContentStore.LoadAsync(artifact.Path, cancellationToken);
            if (content is null ||
                !ReadArtifactMeta(content.Meta, "deviceId").Equals(sourceDeviceId,
                    StringComparison.OrdinalIgnoreCase) ||
                !Convert.ToHexString(SHA256.HashData(content.Content)).Equals(artifact.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(artifact.Path);
                continue;
            }
            var meta = new Dictionary<string, object?>(content.Meta, StringComparer.OrdinalIgnoreCase)
            {
                ["deviceId"] = targetDeviceId,
                ["identitySource"] = "robot-merge",
                ["mergedFromDeviceId"] = sourceDeviceId,
                ["mergedUtc"] = DateTimeOffset.UtcNow
            };
            await mediaContentStore.StoreAsync(artifact.Path, content.ContentType, content.Content, meta,
                cancellationToken);
            updated++;
        }

        return new RobotMergeArtifactResult(updated, skipped);
    }

    private static async Task<int> ReassignCredentialBackfillArtifactsAsync(IMediaContentStore mediaContentStore,
        string accessKeyFingerprint, string deviceId, CancellationToken cancellationToken)
    {
        var updated = 0;
        await foreach (var artifact in mediaContentStore.EnumerateAsync(string.Empty, cancellationToken))
        {
            if (!ReadArtifactMeta(artifact.Meta, "awsAccessKeyFingerprint")
                    .Equals(accessKeyFingerprint, StringComparison.OrdinalIgnoreCase) ||
                ReadArtifactMeta(artifact.Meta, "identitySource") is not ("aws-credential-binding-backfill" or
                    "aws-credential-binding-swap"))
                continue;
            var content = await mediaContentStore.LoadAsync(artifact.Path, cancellationToken);
            if (content is null) continue;
            var meta = new Dictionary<string, object?>(content.Meta, StringComparer.OrdinalIgnoreCase)
            {
                ["deviceId"] = deviceId,
                ["identitySource"] = "aws-credential-binding-swap",
                ["credentialSwappedUtc"] = DateTimeOffset.UtcNow
            };
            await mediaContentStore.StoreAsync(artifact.Path, content.ContentType, content.Content, meta, cancellationToken);
            updated++;
        }
        return updated;
    }

    private static string ReadLogText(byte[] content, string contentType)
    {
        const int maxPreviewCharacters = 32_000;
        try
        {
            using var raw = new MemoryStream(content, writable: false);
            using var gzip = contentType.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                ? new GZipStream(raw, CompressionMode.Decompress)
                : null;
            var source = (Stream?)gzip ?? raw;
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return text.Length <= maxPreviewCharacters ? text : text[..maxPreviewCharacters] + "\n\n[preview truncated]";
        }
        catch (InvalidDataException)
        {
            return $"Binary log artifact ({content.Length:N0} bytes); preview is unavailable.";
        }
    }

    private static ArtifactPreview ReadArtifactPreview(byte[] content, string contentType)
    {
        if (HasPrefix(content, 0x89, 0x50, 0x4E, 0x47))
            return BinaryPreview("image", "PNG image", content, "image/png");
        if (HasPrefix(content, 0x4F, 0x67, 0x67, 0x53))
            return BinaryPreview("audio", "Ogg audio", content, "audio/ogg");
        if (HasPrefix(content, 0x50, 0x4B, 0x03, 0x04) || HasPrefix(content, 0x50, 0x4B, 0x05, 0x06))
            return ZipPreview(content);
        if (HasPrefix(content, 0x1F, 0x8B))
        {
            try
            {
                using var input = new MemoryStream(content, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                gzip.CopyTo(output);
                var decompressed = output.ToArray();
                return IsLikelyText(decompressed)
                    ? new ArtifactPreview("text", "Gzip-compressed text", ReadTextPreview(decompressed), null, [])
                    : new ArtifactPreview("gzip", "Gzip-compressed binary data", null, null, []);
            }
            catch (InvalidDataException)
            {
                return new ArtifactPreview("binary", "Invalid gzip data", null, null, []);
            }
        }

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || IsLikelyText(content))
            return new ArtifactPreview("text", "Text", ReadTextPreview(content), null, []);
        return new ArtifactPreview("binary", "Unknown binary data", null, null, []);
    }

    private static ArtifactPreview BinaryPreview(string kind, string summary, byte[] content, string contentType) =>
        content.Length <= 4 * 1024 * 1024
            ? new ArtifactPreview(kind, summary, null, $"data:{contentType};base64,{Convert.ToBase64String(content)}", [])
            : new ArtifactPreview(kind, $"{summary} ({content.Length:N0} bytes; preview exceeds 4 MB)", null, null, []);

    private static ArtifactPreview ZipPreview(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries.Take(100).Select(entry => new ArtifactArchiveEntry(entry.FullName, entry.Length)).ToArray();
            return new ArtifactPreview("zip", $"ZIP archive with {archive.Entries.Count} entries", null, null, entries);
        }
        catch (InvalidDataException)
        {
            return new ArtifactPreview("binary", "Invalid ZIP data", null, null, []);
        }
    }

    private static bool HasPrefix(byte[] content, params byte[] prefix) =>
        content.Length >= prefix.Length && content.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool IsLikelyText(byte[] content) =>
        content.Length == 0 || (!content.Take(Math.Min(content.Length, 4096)).Contains((byte)0) &&
                               content.Take(Math.Min(content.Length, 4096)).Count(value => value < 0x09) < 8);

    private static string ReadTextPreview(byte[] content)
    {
        const int maxPreviewCharacters = 32_000;
        var text = Encoding.UTF8.GetString(content);
        return text.Length <= maxPreviewCharacters ? text : text[..maxPreviewCharacters] + "\n\n[preview truncated]";
    }

    private sealed record ArtifactPreview(string Kind, string Summary, string? Text, string? DataUrl,
        IReadOnlyList<ArtifactArchiveEntry> ArchiveEntries);
    private sealed record ArtifactArchiveEntry(string Name, long Length);

    private static IReadOnlyList<RobotStatusRow> BuildReconciledRobotStatuses(
        IReadOnlyList<DeviceRegistration> devices,
        IReadOnlyList<CloudSession> sessions,
        IReadOnlyList<CloudSession> recentSessions,
        IReadOnlyList<RobotPresenceConnection> liveConnections,
        DateTimeOffset now,
        bool includeHidden)
    {
        if (devices.Count == 0)
            return [];

        var disjointSet = new DisjointSet(devices.Count);

        for (var sessionIndex = 0; sessionIndex < sessions.Count; sessionIndex++)
        {
            var matchingDeviceIndices = new List<int>();
            for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
            {
                if (SessionMatchesDevice(sessions[sessionIndex], devices[deviceIndex]))
                    matchingDeviceIndices.Add(deviceIndex);
            }

            if (matchingDeviceIndices.Count < 2)
                continue;

            var firstMatch = matchingDeviceIndices[0];
            for (var i = 1; i < matchingDeviceIndices.Count; i++)
                disjointSet.Union(firstMatch, matchingDeviceIndices[i]);
        }

        for (var connectionIndex = 0; connectionIndex < liveConnections.Count; connectionIndex++)
        {
            var matchingDeviceIndices = new List<int>();
            for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
            {
                if (ConnectionMatchesDevice(liveConnections[connectionIndex], devices[deviceIndex]))
                    matchingDeviceIndices.Add(deviceIndex);
            }

            if (matchingDeviceIndices.Count < 2)
                continue;

            var firstMatch = matchingDeviceIndices[0];
            for (var i = 1; i < matchingDeviceIndices.Count; i++)
                disjointSet.Union(firstMatch, matchingDeviceIndices[i]);
        }

        var visibleRows = new List<RobotStatusRow>();
        foreach (var group in Enumerable.Range(0, devices.Count).GroupBy(disjointSet.Find))
        {
            var groupDevices = group.Select(index => devices[index]).ToArray();
            var groupSessions = sessions
                .Where(session => groupDevices.Any(device => SessionMatchesDevice(session, device)))
                .GroupBy(session => session.SessionId, StringComparer.OrdinalIgnoreCase)
                .Select(sessionGroup => sessionGroup
                    .OrderByDescending(session => session.LastSeenUtc)
                    .ThenByDescending(session => session.CreatedUtc)
                    .First())
                .OrderByDescending(session => session.LastSeenUtc)
                .ThenByDescending(session => session.CreatedUtc)
                .ToArray();
            var groupRecentSessions = recentSessions
                .Where(session => groupDevices.Any(device => SessionMatchesDevice(session, device)))
                .ToArray();
            var groupConnections = liveConnections
                .Where(connection => groupDevices.Any(device => ConnectionMatchesDevice(connection, device)))
                .ToArray();

            var groupIsSynthetic = groupDevices.All(IsSyntheticDevice);
            var hasVisibleSignal = groupRecentSessions.Length > 0 || groupConnections.Length > 0;
            var groupIsVisible = includeHidden ||
                                 groupDevices.Any(device => !device.IsHidden) ||
                                 (!groupIsSynthetic && hasVisibleSignal);
            if (!groupIsVisible)
                continue;

            var primaryDevice = SelectPreferredRobotDevice(groupDevices, groupSessions, groupConnections);
            var effectiveActive = groupDevices.Any(device => device.IsActive);
            var mergedDevice = new DeviceRegistration
            {
                DeviceId = primaryDevice.DeviceId,
                RobotId = primaryDevice.RobotId,
                FriendlyName = primaryDevice.FriendlyName,
                FirmwareVersion = primaryDevice.FirmwareVersion,
                ApplicationVersion = primaryDevice.ApplicationVersion,
                IsActive = effectiveActive
            };

            var lastSession = groupSessions.FirstOrDefault();
            var lastSeenUtc = lastSession?.LastSeenUtc;
            var firstSeenUtc = groupSessions.Length > 0
                ? groupSessions.Min(session => session.CreatedUtc)
                : (DateTimeOffset?)null;
            // Proactive/listen sockets often reconnect without carrying the dialog
            // sleepState metadata. Do not let that metadata-less socket erase the
            // robot's most recent explicit sleeping/awake state.
            var sleepState = groupSessions
                .Select(session => ReadSessionMetadata(session, "sleepState"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var presence = ResolveRobotPresence(mergedDevice, groupConnections, lastSeenUtc, sleepState, now);

            visibleRows.Add(new RobotStatusRow(
                primaryDevice.DeviceId,
                primaryDevice.RobotId,
                primaryDevice.FriendlyName,
                FirstNonEmpty(primaryDevice.FirmwareVersion, groupDevices.Select(device => device.FirmwareVersion)),
                FirstNonEmpty(primaryDevice.ApplicationVersion, groupDevices.Select(device => device.ApplicationVersion)),
                effectiveActive,
                groupDevices.All(device => device.IsHidden),
                groupDevices.Select(device => device.ArchivedUtc)
                    .Where(value => value.HasValue)
                    .Cast<DateTimeOffset?>()
                    .OrderByDescending(value => value)
                    .FirstOrDefault(),
                SelectPreferredRegistrationSource(primaryDevice, groupDevices),
                primaryDevice.VerifiedSerialNumber,
                primaryDevice.SerialEvidenceSource,
                primaryDevice.SerialEvidenceVerifiedUtc,
                groupIsSynthetic,
                presence,
                presence is "online" or "sleeping",
                groupConnections.Length > 0,
                groupSessions.Length,
                groupConnections.Length,
                firstSeenUtc,
                lastSeenUtc,
                lastSeenUtc is null ? null : (now - lastSeenUtc.Value).TotalSeconds,
                groupSessions
                    .Select(session => session.Kind)
                    .Where(kind => !string.IsNullOrWhiteSpace(kind))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                groupConnections
                    .Select(connection => connection.Kind)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                sleepState));
        }

        return visibleRows
            .OrderByDescending(robot => robot.Connected)
            .ThenBy(robot => robot.Presence == "recently-seen" ? 0 : 1)
            .ThenBy(robot => robot.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(robot => robot.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FirstNonEmpty(string? seed, IEnumerable<string?> candidates)
    {
        if (!string.IsNullOrWhiteSpace(seed))
            return seed;

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsSafeIdentityName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= 120 &&
        System.Text.RegularExpressions.Regex.IsMatch(value.Trim(),
            "^[A-Za-z0-9]+(?:-[A-Za-z0-9]+){2,}$");

    private static IReadOnlyList<RobotIdentityCandidate> ExtractIdentityCandidates(MediaContentSnapshot? content)
    {
        if (content is null || content.Content.Length == 0) return [];
        const int maxEvidenceBytes = 256 * 1024;
        try
        {
            byte[] bytes = content.Content;
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[16 * 1024];
                while (output.Length < maxEvidenceBytes)
                {
                    var remaining = maxEvidenceBytes - checked((int)output.Length);
                    var read = gzip.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                }
                bytes = output.ToArray();
            }
            else if (bytes.Length > maxEvidenceBytes)
            {
                bytes = bytes[..maxEvidenceBytes];
            }

            return RobotIdentityCandidateExtractor.Extract(Encoding.UTF8.GetString(bytes));
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private static async Task<MediaContentSnapshot?> TryLoadIdentityArtifactAsync(
        IMediaContentStore mediaContentStore, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await mediaContentStore.LoadAsync(path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // One inaccessible historical artifact should not prevent the remaining
            // evidence from producing an admin-reviewable suggestion.
            return null;
        }
    }

    private static string SelectPreferredRegistrationSource(
        DeviceRegistration primaryDevice,
        IEnumerable<DeviceRegistration> groupDevices)
    {
        var primarySource = RobotRegistrationSources.Normalize(primaryDevice.RegistrationSource, primaryDevice.DeviceId);
        if (!string.Equals(primarySource, RobotRegistrationSources.Unknown, StringComparison.OrdinalIgnoreCase))
            return primarySource;

        var nonSyntheticSource = groupDevices
            .Select(device => RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId))
            .FirstOrDefault(source =>
                !string.Equals(source, RobotRegistrationSources.Unknown, StringComparison.OrdinalIgnoreCase) &&
                !RobotRegistrationSources.IsSynthetic(source));

        return nonSyntheticSource ?? RobotRegistrationSources.Unknown;
    }

    private static DeviceRegistration SelectPreferredRobotDevice(
        IReadOnlyList<DeviceRegistration> devices,
        IReadOnlyList<CloudSession> sessions,
        IReadOnlyList<RobotPresenceConnection> connections)
    {
        return devices
            .OrderByDescending(device => !IsGenericRobotDisplayName(device.FriendlyName))
            .ThenByDescending(device => !IsPlaceholderRobotIdentity(device))
            .ThenByDescending(device => !device.IsHidden)
            .ThenByDescending(device => device.IsActive)
            .ThenByDescending(device => !string.Equals(
                RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId),
                RobotRegistrationSources.Unknown,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(device => connections.Any(connection => ConnectionMatchesDevice(connection, device)))
            .ThenByDescending(device => GetLatestMatchedSessionUtc(device, sessions) ?? DateTimeOffset.MinValue)
            .ThenBy(device => device.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static DateTimeOffset? GetLatestMatchedSessionUtc(
        DeviceRegistration device,
        IReadOnlyList<CloudSession> sessions)
    {
        var latest = sessions
            .Where(session => SessionMatchesDevice(session, device))
            .Select(session => session.LastSeenUtc)
            .ToArray();

        return latest.Length > 0 ? latest.Max() : null;
    }

    private static bool IsGenericRobotDisplayName(string? friendlyName)
    {
        return string.IsNullOrWhiteSpace(friendlyName) ||
               friendlyName.Equals("OpenJibo Registered Robot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlaceholderRobotIdentity(DeviceRegistration device)
    {
        var deviceId = device.DeviceId?.Trim();
        return !string.IsNullOrWhiteSpace(deviceId) &&
               deviceId.Length == 24 &&
               deviceId.All(Uri.IsHexDigit) &&
               IdentityMatches(device.RobotId, $"robot-{deviceId}");
    }

    private static bool SessionMatchesDevice(CloudSession session, DeviceRegistration device)
    {
        // Runtime identity values can be shared by cloned robots.  The portal only
        // reconciles sessions using an administrator-created binding.
        return IdentityMatches(ReadSessionMetadata(session, "registeredDeviceId"), device.DeviceId) ||
               IdentityMatches(ReadSessionMetadata(session, "registeredRobotId"), device.RobotId);
    }

    private static bool SessionProvidesIdentityEvidenceForDevice(CloudSession session, DeviceRegistration device) =>
        SessionMatchesDevice(session, device) ||
        IdentityMatches(ReadSessionMetadata(session, "identitySuggestionDeviceId"), device.DeviceId);

    private static IEnumerable<string> GetSessionIdentityValues(CloudSession session)
    {
        var values = new[]
        {
            session.DeviceId,
            ReadSessionMetadata(session, "registeredDeviceId"),
            ReadSessionMetadata(session, "registeredRobotId"),
            ReadSessionMetadata(session, "robotID"),
            ReadSessionMetadata(session, "robotId"),
            ReadSessionMetadata(session, "robotFriendlyId"),
            ReadSessionMetadata(session, "friendlyId"),
            ReadSessionMetadata(session, "deviceId")
        };

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                yield return value.Trim();
        }
    }

    private static bool ConnectionMatchesDevice(RobotPresenceConnection connection, DeviceRegistration device) =>
        connection.RobotKeys.Contains(device.DeviceId) || connection.RobotKeys.Contains(device.RobotId);

    private static bool IsSyntheticDevice(DeviceRegistration device) =>
        RobotRegistrationSources.IsSynthetic(
            RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId));

    private static string? ReadSessionMetadata(CloudSession session, string key) =>
        session.Metadata.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool IdentityMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        if (left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        return GetIdentityAliases(left).Any(alias => alias.Equals(right.Trim(), StringComparison.OrdinalIgnoreCase)) ||
               GetIdentityAliases(right).Any(alias => alias.Equals(left.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetIdentityAliases(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var trimmed = value.Trim();
        yield return trimmed;

        if (trimmed.StartsWith("robot-", StringComparison.OrdinalIgnoreCase) && trimmed.Length > "robot-".Length)
            yield return trimmed["robot-".Length..];

        if (trimmed.StartsWith("hub-", StringComparison.OrdinalIgnoreCase) && trimmed.Length > "hub-".Length)
            yield return trimmed["hub-".Length..];
    }

    private static string ResolveRobotPresence(DeviceRegistration device,
        IReadOnlyCollection<RobotPresenceConnection> liveConnections, DateTimeOffset? lastSeenUtc, string? sleepState,
        DateTimeOffset now)
    {
        if (!device.IsActive) return "inactive";
        // A live interactive socket means the robot is awake, even if an older
        // proactive/session record still carries sleepState= sleeping.
        if (liveConnections.Any(connection =>
                connection.Kind.Equals("neo-hub-listen", StringComparison.OrdinalIgnoreCase) ||
                connection.Kind.Equals("api-socket", StringComparison.OrdinalIgnoreCase)))
            return "online";
        if (string.Equals(sleepState, "sleeping", StringComparison.OrdinalIgnoreCase) &&
            (liveConnections.Count > 0 || lastSeenUtc >= now.AddHours(-24))) return "sleeping";
        if (liveConnections.Count > 0 || lastSeenUtc >= now - StatusActiveActivityWindow) return "online";
        if (lastSeenUtc >= now.AddMinutes(-30)) return "recently-seen";
        return lastSeenUtc is null ? "never-connected" : "offline";
    }

    private sealed record RobotStatusRow(
        string DeviceId,
        string RobotId,
        string FriendlyName,
        string? FirmwareVersion,
        string? ApplicationVersion,
        bool IsActive,
        bool IsHidden,
        DateTimeOffset? ArchivedUtc,
        string RegistrationSource,
        string? VerifiedSerialNumber,
        string? SerialEvidenceSource,
        DateTimeOffset? SerialEvidenceVerifiedUtc,
        bool IsSynthetic,
        string Presence,
        bool Connected,
        bool HasOpenSocket,
        int SessionCount,
        int LiveConnectionCount,
        DateTimeOffset? FirstSeenUtc,
        DateTimeOffset? LastSeenUtc,
        double? LastHeartbeatAgeSeconds,
        IReadOnlyList<string> SessionKinds,
        IReadOnlyList<string> ConnectionKinds,
        string? SleepState)
    {
        public RobotIdentitySuggestion? IdentitySuggestion { get; init; }
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly int[] _rank;

        public DisjointSet(int size)
        {
            _parent = Enumerable.Range(0, size).ToArray();
            _rank = new int[size];
        }

        public int Find(int index)
        {
            if (_parent[index] != index)
                _parent[index] = Find(_parent[index]);

            return _parent[index];
        }

        public void Union(int left, int right)
        {
            var rootLeft = Find(left);
            var rootRight = Find(right);
            if (rootLeft == rootRight)
                return;

            if (_rank[rootLeft] < _rank[rootRight])
            {
                _parent[rootLeft] = rootRight;
                return;
            }

            if (_rank[rootLeft] > _rank[rootRight])
            {
                _parent[rootRight] = rootLeft;
                return;
            }

            _parent[rootRight] = rootLeft;
            _rank[rootLeft]++;
        }
    }

    private static DeviceRegistration CopyDevice(DeviceRegistration device, bool isHidden, DateTimeOffset? archivedUtc) =>
        new()
        {
            DeviceId = device.DeviceId,
            RobotId = device.RobotId,
            FriendlyName = device.FriendlyName,
            FirmwareVersion = device.FirmwareVersion,
            ApplicationVersion = device.ApplicationVersion,
            IsActive = device.IsActive,
            CertificateThumbprint = device.CertificateThumbprint,
            IssuedIdentityId = device.IssuedIdentityId,
            BuildHash = device.BuildHash,
            ConfigHash = device.ConfigHash,
            VerifiedSerialNumber = device.VerifiedSerialNumber,
            SerialEvidenceSource = device.SerialEvidenceSource,
            SerialEvidenceVerifiedUtc = device.SerialEvidenceVerifiedUtc,
            RegistrationSource = RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId),
            IsHidden = isHidden,
            ArchivedUtc = archivedUtc,
            HostMappings = new Dictionary<string, string>(device.HostMappings, StringComparer.OrdinalIgnoreCase)
        };


    private static IReadOnlyList<string> BuildAdminConversionBlockers(
        DeviceRegistration robot,
        IdentityGraphSnapshot graph)
    {
        var blockers = new List<string>();

        if (!robot.IsActive)
            blockers.Add("robot-not-active");

        if (robot.HostMappings.Count == 0)
            blockers.Add("missing-host-mappings");

        if (graph.AdmissionAssessment.BlockingEvidence.Count > 0)
            blockers.AddRange(graph.AdmissionAssessment.BlockingEvidence.Select(evidence => $"identity-{evidence}"));

        foreach (var missingHost in GetMissingRequiredHostMappings(robot))
            blockers.Add($"missing-host-mapping:{missingHost}");

        return blockers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> GetMissingRequiredHostMappings(DeviceRegistration robot)
    {
        return RequiredLegacyHostMappings
            .Where(host => !robot.HostMappings.TryGetValue(host, out var mappedHost) ||
                           string.IsNullOrWhiteSpace(mappedHost))
            .ToArray();
    }

    private static PortalSessionService.PortalSession? ResolvePortalSession(
        HttpRequest request,
        string? portalSessionToken,
        PortalSessionService portalSessionService,
        bool allowAccountSession = false)
    {
        var session = portalSessionService.TryGetSession(ResolvePortalSessionToken(request, portalSessionToken));
        if (session is null) return null;
        var store = request.HttpContext.RequestServices.GetRequiredService<ICloudStateStore>();
        return PortalSessionAccessGuard.CanAccess(session, store, allowAccountSession) ? session : null;
    }

    private static object BuildAccountSessionPayload(
        PortalSessionService portalSessionService,
        ICloudStateStore cloudStateStore,
        UserRecord user)
    {
        var session = portalSessionService.CreateAccountSession(user.Email, user.Id);
        return new
        {
            portalSessionToken = session.Token,
            userId = user.Id,
            email = user.Email,
            expiresAtUtc = session.ExpiresAtUtc,
            robots = BuildUserRobotPayload(cloudStateStore, user.Id)
        };
    }

    private static object BuildAccountPayload(UserRecord user, ICloudStateStore cloudStateStore) =>
        new
        {
            userId = user.Id,
            email = user.Email,
            robots = BuildUserRobotPayload(cloudStateStore, user.Id)
        };

    private static object[] BuildUserRobotPayload(ICloudStateStore cloudStateStore, string userId) =>
        cloudStateStore.GetDevicesForUser(userId)
            .Select(robot => new
            {
                deviceId = robot.DeviceId,
                robotId = robot.RobotId,
                friendlyName = robot.FriendlyName,
                isActive = robot.IsActive,
                isHidden = robot.IsHidden,
                archivedUtc = robot.ArchivedUtc
            })
            .Cast<object>()
            .ToArray();

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        try
        {
            var parsed = new MailAddress(email.Trim());
            return string.Equals(parsed.Address, email.Trim(), StringComparison.OrdinalIgnoreCase) &&
                   parsed.User.Length > 0 &&
                   parsed.Host.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidPortalPassword(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length is >= 8 and <= 32;

    private static bool IsAdminSession(PortalSessionService.PortalSession session)
    {
        return session.Kind == PortalSessionService.AdministratorSessionKind;
    }

    private static string? ResolvePortalSessionToken(
        HttpRequest request,
        string? portalSessionToken)
    {
        var token = request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(token) &&
            token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim();

        token ??= request.Query["portalSessionToken"].FirstOrDefault();
        token ??= portalSessionToken;
        return token;
    }

    private static string? ResolveAdminStatusPassword(IConfiguration configuration)
    {
        return configuration["OpenJibo:Portal:StatusPassword"]
               ?? Environment.GetEnvironmentVariable("OPENJIBO_PORTAL_STATUS_PASSWORD");
    }

    private static bool PasswordsMatch(string suppliedPassword, string configuredPassword)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(suppliedPassword);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredPassword);
        return suppliedBytes.Length == configuredBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h";

        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";

        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";

        return $"{duration.Seconds}s";
    }

    internal static void RegisterVerifiedRobotIdentity(ICloudStateStore cloudStateStore, string deviceId,
        string friendlyId)
    {
        var resolvedFriendlyId = !string.IsNullOrWhiteSpace(friendlyId)
            ? friendlyId.Trim()
            : deviceId.Trim();
        if (string.IsNullOrWhiteSpace(resolvedFriendlyId))
            return;

        var existing = cloudStateStore.FindDeviceByFriendlyId(resolvedFriendlyId);
        var singleton = cloudStateStore.GetRobot();
        var candidateDeviceId = string.IsNullOrWhiteSpace(deviceId) ? resolvedFriendlyId : deviceId.Trim();

        // Never key a different robot's registration by the process-wide singleton DeviceId.
        var isSharedSingletonDeviceId =
            !string.IsNullOrWhiteSpace(singleton.DeviceId) &&
            candidateDeviceId.Equals(singleton.DeviceId, StringComparison.OrdinalIgnoreCase) &&
            !resolvedFriendlyId.Equals(singleton.RobotId, StringComparison.OrdinalIgnoreCase);

        var resolvedDeviceId = existing?.DeviceId
            ?? (isSharedSingletonDeviceId ? resolvedFriendlyId : candidateDeviceId);

        var conflict = cloudStateStore.FindDeviceByFriendlyId(resolvedDeviceId);
        if (conflict is not null &&
            !conflict.RobotId.Equals(resolvedFriendlyId, StringComparison.OrdinalIgnoreCase) &&
            !conflict.DeviceId.Equals(resolvedFriendlyId, StringComparison.OrdinalIgnoreCase))
            resolvedDeviceId = resolvedFriendlyId;

        cloudStateStore.UpsertDevice(new DeviceRegistration
        {
            DeviceId = resolvedDeviceId,
            RobotId = resolvedFriendlyId,
            FriendlyName = existing?.FriendlyName ?? resolvedFriendlyId,
            FirmwareVersion = existing?.FirmwareVersion,
            ApplicationVersion = existing?.ApplicationVersion,
            IsActive = existing?.IsActive ?? true,
            CertificateThumbprint = existing?.CertificateThumbprint,
            IssuedIdentityId = existing?.IssuedIdentityId,
            BuildHash = existing?.BuildHash,
            ConfigHash = existing?.ConfigHash,
            VerifiedSerialNumber = existing?.VerifiedSerialNumber,
            SerialEvidenceSource = existing?.SerialEvidenceSource,
            SerialEvidenceVerifiedUtc = existing?.SerialEvidenceVerifiedUtc,
            RegistrationSource = existing?.RegistrationSource ?? RobotRegistrationSources.Portal,
            IsHidden = existing?.IsHidden ?? false,
            ArchivedUtc = existing?.ArchivedUtc,
            HostMappings = existing is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(existing.HostMappings, StringComparer.OrdinalIgnoreCase)
        });

        ArchiveSupersededRobotPlaceholders(cloudStateStore, resolvedDeviceId, candidateDeviceId, resolvedFriendlyId,
            singleton.DeviceId);
    }

    internal static int ArchiveSupersededRobotPlaceholders(ICloudStateStore cloudStateStore, string resolvedDeviceId,
        string candidateDeviceId, string resolvedFriendlyId, string? singletonDeviceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var supersededDevices = cloudStateStore.GetDevices()
            .Where(device =>
                !string.Equals(device.DeviceId, resolvedDeviceId, StringComparison.OrdinalIgnoreCase) &&
                (
                    IdentityMatches(device.DeviceId, candidateDeviceId) ||
                    IdentityMatches(device.RobotId, candidateDeviceId) ||
                    IdentityMatches(device.DeviceId, resolvedFriendlyId) ||
                    IdentityMatches(device.RobotId, resolvedFriendlyId) ||
                    (!string.IsNullOrWhiteSpace(singletonDeviceId) &&
                     IdentityMatches(device.DeviceId, singletonDeviceId))
                ) &&
                IsPlaceholderRobotRecord(device))
            .ToArray();

        foreach (var device in supersededDevices)
        {
            cloudStateStore.UpsertDevice(CopyDevice(device, true, device.ArchivedUtc ?? now));
        }

        return supersededDevices.Length;
    }

    private static bool IsPlaceholderRobotRecord(DeviceRegistration device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.RobotId))
            return false;

        var expectedRobotId = $"robot-{device.DeviceId.Trim()}";
        return string.Equals(device.RobotId.Trim(), expectedRobotId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(device.FriendlyName, "OpenJibo Registered Robot", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(RobotRegistrationSources.Normalize(device.RegistrationSource, device.DeviceId),
                   RobotRegistrationSources.Unknown, StringComparison.OrdinalIgnoreCase);
    }

    private static object BuildDashboardPayload(
        PortalSessionService.PortalSession session,
        HomeAssistantLinkRecord? link,
        HomeAssistantConnectionRegistry registry,
        ICloudStateStore cloudStateStore,
        IUserIntegrationStore integrationStore)
    {
        var robot = cloudStateStore.FindDeviceByFriendlyId(session.DeviceId);
        return new
        {
            jiboFriendlyId = session.FriendlyId,
            jiboDeviceId = session.DeviceId,
            jiboName = robot?.FriendlyName ?? session.FriendlyId,
            accountUserId = session.UserId,
            sessionExpiresAtUtc = session.ExpiresAtUtc,
            homeAssistant = link is null
                ? new
                {
                    linked = false,
                    connected = false
                }
                : BuildHomeAssistantPayload(link, registry),
            calendarFeeds = BuildCalendarFeedsPayload(cloudStateStore, integrationStore, session),
            loopMembers = BuildLoopMembersPayload(cloudStateStore, ResolvePortalLoopId(cloudStateStore, session))
        };
    }

    private static object BuildCalendarFeedsPayload(
        ICloudStateStore cloudStateStore,
        IUserIntegrationStore integrationStore,
        PortalSessionService.PortalSession session)
    {
        var loopId = ResolvePortalLoopId(cloudStateStore, session);
        var feeds = integrationStore.GetMemberCalendarFeeds(loopId, session.OwnershipEpoch ?? "unowned");
        // Query GetPeople(loopId) for this robot's loop only — never merge another Jibo's household.
        var members = EnumerateCalendarFeedPeople(cloudStateStore, loopId, session)
            .Select(person =>
            {
                var feed = feeds.FirstOrDefault(item =>
                    item.MemberId.Equals(person.MemberId, StringComparison.OrdinalIgnoreCase));
                return BuildMemberCalendarFeedStatus(person, feed);
            })
            .ToArray();

        return new
        {
            loopId,
            members
        };
    }

    private static IEnumerable<CalendarFeedPerson> EnumerateCalendarFeedPeople(
        ICloudStateStore cloudStateStore,
        string loopId,
        PortalSessionService.PortalSession session)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var robotKeys = BuildPortalRobotKeys(session, cloudStateStore, loopId);

        foreach (var person in cloudStateStore.GetPeople(loopId)
                     .Where(item => PersonBelongsToPortalRobot(item, loopId, robotKeys))
                     .OrderBy(item => item.IsPrimary ? 0 : 1)
                     .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(person.PersonId)) continue;
            yield return new CalendarFeedPerson(
                person.PersonId,
                person.DisplayName,
                person.Alias,
                null,
                person.Alias);
        }

        foreach (var member in cloudStateStore.GetLoopMembers(loopId)
                     .Where(static member =>
                         !string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(member.Status, "removed", StringComparison.OrdinalIgnoreCase)))
        {
            if (!seen.Add(member.Id)) continue;
            var displayName = !string.IsNullOrWhiteSpace(member.Nickname)
                ? member.Nickname
                : string.Join(' ', new[] { member.FirstName, member.LastName }
                    .Where(static part => !string.IsNullOrWhiteSpace(part)));
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = member.Email ?? member.Id;

            yield return new CalendarFeedPerson(
                member.Id,
                displayName,
                member.FirstName,
                member.LastName,
                member.Nickname);
        }
    }

    private static CalendarFeedPerson? FindCalendarFeedPerson(
        ICloudStateStore cloudStateStore,
        string loopId,
        PortalSessionService.PortalSession session,
        string memberId)
    {
        return EnumerateCalendarFeedPeople(cloudStateStore, loopId, session)
            .FirstOrDefault(person => person.MemberId.Equals(memberId, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<string> BuildPortalRobotKeys(
        PortalSessionService.PortalSession session,
        ICloudStateStore cloudStateStore,
        string loopId)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(session.FriendlyId))
            keys.Add(session.FriendlyId.Trim());

        // Only include DeviceId when it is the same robot key (friendlyId). A shared singleton
        // serial must not widen the key set and pull another Jibo's people into this portal.
        if (!string.IsNullOrWhiteSpace(session.DeviceId) &&
            (string.IsNullOrWhiteSpace(session.FriendlyId) ||
             session.DeviceId.Equals(session.FriendlyId, StringComparison.OrdinalIgnoreCase)))
            keys.Add(session.DeviceId.Trim());

        var loop = cloudStateStore.GetLoops()
            .FirstOrDefault(item => item.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase));
        if (loop is not null)
        {
            if (!string.IsNullOrWhiteSpace(loop.RobotId) &&
                (keys.Count == 0 || keys.Contains(loop.RobotId)))
                keys.Add(loop.RobotId.Trim());
            if (!string.IsNullOrWhiteSpace(loop.RobotFriendlyId) &&
                (keys.Count == 0 || keys.Contains(loop.RobotFriendlyId)))
                keys.Add(loop.RobotFriendlyId.Trim());
        }

        return keys;
    }

    private static HashSet<string> BuildPortalLoopUpdatedSeedKeys(PortalSessionService.PortalSession session)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(session.FriendlyId))
            keys.Add(session.FriendlyId.Trim());
        if (!string.IsNullOrWhiteSpace(session.DeviceId))
            keys.Add(session.DeviceId.Trim());
        return keys;
    }

    private static bool PersonBelongsToPortalRobot(
        PersonRecord person,
        string loopId,
        IReadOnlySet<string> robotKeys)
    {
        // Prefer explicit robot ownership so a shared/default loop cannot leak another Jibo's people.
        if (!string.IsNullOrWhiteSpace(person.RobotId))
            return robotKeys.Contains(person.RobotId);

        // Legacy rows without RobotId are only shown when they already live on this robot's loop.
        return string.Equals(person.LoopId, loopId, StringComparison.OrdinalIgnoreCase);
    }

    private static object BuildMemberCalendarFeedStatus(
        CalendarFeedPerson member,
        MemberCalendarFeedRecord? feed)
    {
        return new
        {
            memberId = member.MemberId,
            displayName = member.DisplayName,
            firstName = member.FirstName,
            lastName = member.LastName,
            nickname = member.Nickname,
            configured = feed is not null && !string.IsNullOrWhiteSpace(feed.IcalUrl),
            isEnabled = feed?.IsEnabled ?? false,
            host = feed is null ? null : IcalUrlValidator.TryGetSafeHost(feed.IcalUrl),
            lastSuccessUtc = feed?.LastSuccessUtc,
            lastError = feed?.LastError,
            updatedUtc = feed?.UpdatedUtc
        };
    }

    private sealed record CalendarFeedPerson(
        string MemberId,
        string DisplayName,
        string? FirstName,
        string? LastName,
        string? Nickname);

    private static object BuildLoopMembersPayload(ICloudStateStore cloudStateStore, string loopId)
    {
        var members = cloudStateStore.GetLoopMembers(loopId)
            .Where(static member => !string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static member => member.Type.Equals("owner", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(static member => member.FirstName, StringComparer.OrdinalIgnoreCase)
            .Select(BuildLoopMemberPayload)
            .ToArray();

        return new { loopId, members };
    }

    private static object BuildLoopMemberPayload(LoopMemberRecord member)
    {
        var displayName = !string.IsNullOrWhiteSpace(member.Nickname)
            ? member.Nickname
            : string.Join(' ', new[] { member.FirstName, member.LastName }
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = member.Id;

        return new
        {
            id = member.Id,
            firstName = member.FirstName,
            lastName = member.LastName,
            displayName,
            gender = member.Gender,
            type = member.Type,
            canRemove = member.Type is not "owner" and not "robot"
        };
    }

    private static string NormalizeGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender)) return "unknown";
        var normalized = gender.Trim().ToLowerInvariant();
        return normalized is "male" or "female" ? normalized : "unknown";
    }

    private static Task TryPushLoopUpdatedAsync(
        LoopUpdatedPushService loopUpdatedPushService,
        PortalSessionService.PortalSession session,
        string loopId,
        CancellationToken cancellationToken)
    {
        return loopUpdatedPushService.PushForLoopIdAsync(
            loopId,
            BuildPortalLoopUpdatedSeedKeys(session),
            cancellationToken);
    }

    private static string ResolvePortalLoopId(
        ICloudStateStore cloudStateStore,
        PortalSessionService.PortalSession session)
    {
        // One loop per friendlyId (Pegasus robotID). Do not pass DeviceId — a shared serial
        // singleton would OR-match and merge households.
        var friendlyId = session.FriendlyId;
        var loop = cloudStateStore.AddLoop(null, null, friendlyId, friendlyId);
        return loop.LoopId;
    }

    private static DeviceRegistration ResolvePortalRobot(
        ICloudStateStore cloudStateStore,
        PortalSessionService.PortalSession session)
    {
        return cloudStateStore.FindDeviceByFriendlyId(session.DeviceId) ??
               cloudStateStore.FindDeviceByFriendlyId(session.FriendlyId) ??
               new DeviceRegistration
               {
                   DeviceId = session.DeviceId,
                   RobotId = session.FriendlyId,
                   FriendlyName = session.FriendlyId
               };
    }

    private static object BuildHomeAssistantPayload(
        HomeAssistantLinkRecord link,
        HomeAssistantConnectionRegistry registry)
    {
        return new
        {
            linked = true,
            connected = registry.IsInstanceConnected(link.HaInstanceId),
            linkId = link.LinkId,
            jiboFriendlyId = link.JiboFriendlyName,
            jiboDeviceId = link.JiboDeviceId,
            haInstanceId = link.HaInstanceId,
            blacklistHeat = link.BlacklistHeat,
            blacklistCool = link.BlacklistCool,
            pairedAtUtc = link.PairedAtUtc,
            lastSeenUtc = link.LastSeenUtc
        };
    }

    private sealed record ConfirmJiboVerificationRequest(string? Code);

    private sealed record PortalAccountRegisterRequest(string? Email, string? Password);
    private sealed record PortalAccountLoginRequest(string? Email, string? Password);
    private sealed record PairPortalRobotRequest(string? PortalSessionToken, string? Code);
    private sealed record SelectPortalRobotRequest(string? PortalSessionToken, string? DeviceId);
    private sealed record RenamePortalRobotRequest(string? PortalSessionToken, string? Name);

    private sealed record LinkHomeAssistantRequest(string? PortalSessionToken, string? HaCode);

    private sealed record UpdateHomeAssistantClimateOptionsRequest(
        string? PortalSessionToken,
        bool? BlacklistHeat,
        bool? BlacklistCool);

    private sealed record UpsertMemberCalendarFeedRequest(
        string? PortalSessionToken,
        string? IcalUrl,
        bool? IsEnabled);

    private sealed record TestMemberCalendarFeedRequest(
        string? PortalSessionToken,
        string? IcalUrl);

    private sealed record AddLoopMemberRequest(
        string? PortalSessionToken,
        string? FirstName,
        string? LastName,
        string? Gender);

    private sealed record UpdateLoopMemberRequest(
        string? PortalSessionToken,
        string? FirstName,
        string? LastName,
        string? Gender);

    private sealed record PortalLogoutRequest(string? PortalSessionToken);

    private sealed record RevokeIdentityGraphAnchorRequest(string? Anchor, string? PortalSessionToken);

    private sealed record AdminStatusLoginRequest(string? Password);

    private sealed record ArchiveStatusRobotRequest(string? PortalSessionToken, bool Hidden);
    private sealed record ResetRobotIdentityAssociationsRequest(string? PortalSessionToken, bool Confirmed);
    private sealed record RestoreRobotIdentityRequest(string? PortalSessionToken, bool Confirmed);
    private sealed record LinkStatusSessionRequest(
        string? PortalSessionToken,
        string? DeviceId,
        string? ObservedDeviceId,
        DateTimeOffset? LastSeenUtc);
    private sealed record BindRobotCredentialRequest(string? PortalSessionToken, string? AccessKeyFingerprint);
    private sealed record SwapRobotCredentialBindingsRequest(string? PortalSessionToken,
        string? FirstAccessKeyFingerprint, string? SecondAccessKeyFingerprint, bool Confirmed);
    private sealed record MergeRobotRequest(string? PortalSessionToken, string? TargetDeviceId, string? PreviewToken);
    private sealed record ApplyIdentitySuggestionRequest(
        string? PortalSessionToken,
        string? ProposedRobotId,
        string? ExpectedAction,
        string? ExpectedTargetDeviceId);

    private sealed record RobotMergePreview(
        string SourceDeviceId,
        string TargetDeviceId,
        IReadOnlyList<string> SessionIds,
        IReadOnlyList<string> CredentialFingerprints,
        IReadOnlyList<RobotMergeArtifactReference> Artifacts,
        string SnapshotFingerprint);

    private sealed record RobotMergeArtifactReference(string Path, string ContentSha256);

    private sealed record RobotMergeArtifactResult(int MigratedCount, IReadOnlyList<string> SkippedPaths);

    private sealed record FleetServerPresenceReportRequest(
        string? PortalSessionToken,
        string? ServerId,
        string? CanonicalHost,
        string? InstanceId,
        string[]? ConnectedRobotIds,
        int? ConnectionCount);

    private sealed record UpsertTrustedServerRequest(
        string? PortalSessionToken,
        string? CanonicalHost,
        string? DisplayName,
        string? ServerKind,
        bool? IsListed,
        bool? AcceptsPublicConnections,
        bool? ParticipatesInCloudSync,
        bool? RequiresHttps,
        bool? IsActive,
        string? Reason,
        string? Description);

    private sealed record TrustedServerLifecycleRequest(
        string? PortalSessionToken,
        string? CanonicalHost,
        string? Action,
        string? ServerKind,
        string? DisplayName,
        bool? IsListed,
        bool? AcceptsPublicConnections,
        bool? ParticipatesInCloudSync,
        bool? RequiresHttps,
        bool? IsActive,
        string? Reason,
        string? Description,
        DateTimeOffset? LastSeenAtUtc);

    private sealed record ValidateSelfHostedRequest(
        string? ServerMode,
        string? ServerHost,
        string? ServerUrl);

    private sealed record VerifyIdentityGraphEvidenceBundleRequest(
        string? Envelope,
        string? PortalSessionToken,
        string[]? LocalRevokedAnchors);

    private static string NormalizeTrustedServerKind(string? serverKind)
    {
        var normalized = string.IsNullOrWhiteSpace(serverKind) ? "managed" : serverKind.Trim();
        normalized = normalized.Equals("hosted", StringComparison.OrdinalIgnoreCase) ? "managed" : normalized;
        normalized = normalized.Equals("self-hosted-hybrid", StringComparison.OrdinalIgnoreCase) ? "hybrid" : normalized;
        normalized = normalized.ToLowerInvariant();
        return normalized is "managed" or "hybrid" or "self-hosted" ? normalized : "managed";
    }

    private static string NormalizeSelfHostedMode(string? serverMode)
    {
        var normalized = string.IsNullOrWhiteSpace(serverMode) ? "self-hosted" : serverMode.Trim();
        normalized = normalized.Equals("local", StringComparison.OrdinalIgnoreCase) ? "self-hosted" : normalized;
        normalized = normalized.Equals("self-hosted-hybrid", StringComparison.OrdinalIgnoreCase)
            ? "self-hosted-hybrid"
            : normalized;
        normalized = normalized.ToLowerInvariant();
        return normalized is "self-hosted" or "self-hosted-hybrid" ? normalized : "self-hosted";
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string NormalizeOnboardingHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return uri.Host;

        return trimmed.TrimEnd('/');
    }

    private static string ReadLogFileWithOffset(string filePath, long offset, int tailLines)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        var fileInfo = new FileInfo(filePath);
        if (offset >= fileInfo.Length)
            return string.Empty;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // If offset is 0, we want to tail the file (read last N lines)
            if (offset == 0)
            {
                // Read backwards to get last N lines efficiently
                return ReadLastLines(stream, tailLines);
            }
            else
            {
                // Read from offset forward, limited to tailLines
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                var lineCount = 0;

                while (!reader.EndOfStream && lineCount < tailLines)
                {
                    var line = reader.ReadLine();
                    if (line != null)
                    {
                        lines.Add(line);
                        lineCount++;
                    }
                }

                return string.Join("\n", lines);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadLastLines(FileStream stream, int lineCount)
    {
        if (lineCount <= 0)
            return string.Empty;

        stream.Seek(0, SeekOrigin.End);
        var position = stream.Length;
        var lines = new List<string>();
        var buffer = new byte[4096];
        var byteLines = new List<byte[]>();
        var current = new List<byte>();
        var linesFound = 0;

        while (position > 0 && linesFound < lineCount)
        {
            var bytesToRead = (int)Math.Min(buffer.Length, position);
            position -= bytesToRead;
            stream.Seek(position, SeekOrigin.Begin);
            var bytesRead = stream.Read(buffer, 0, bytesToRead);

            // Process buffer backwards. Decode complete UTF-8 text after selecting lines;
            // treating each byte as a char corrupts non-ASCII log entries.
            for (int i = bytesRead - 1; i >= 0; i--)
            {
                var value = buffer[i];
                if (value == (byte)'\n')
                {
                    if (current.Count > 0)
                    {
                        current.Reverse();
                        byteLines.Insert(0, current.ToArray());
                        current.Clear();
                        linesFound++;
                        if (linesFound >= lineCount)
                            break;
                    }
                }
                else if (value != (byte)'\r')
                {
                    current.Add(value);
                }
            }
        }

        // Add remaining buffer content
        if (current.Count > 0 && linesFound < lineCount)
        {
            current.Reverse();
            byteLines.Insert(0, current.ToArray());
        }

        lines.AddRange(byteLines.Select(bytes => Encoding.UTF8.GetString(bytes)));
        return string.Join("\n", lines);
    }

    private static string ResolvePortalConfiguredPath(IConfiguration configuration, string key, string defaultPath)
    {
        var configuredPath = configuration[key];
        if (string.IsNullOrWhiteSpace(configuredPath)) configuredPath = defaultPath;

        if (Path.IsPathRooted(configuredPath)) return Path.GetFullPath(configuredPath);

        var repoRoot = FindOpenJiboRepoRoot(Directory.GetCurrentDirectory()) ??
                       FindOpenJiboRepoRoot(AppContext.BaseDirectory) ??
                       Directory.GetCurrentDirectory();

        return Path.GetFullPath(configuredPath, repoRoot);
    }

    private static string? FindOpenJiboRepoRoot(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath)) return null;

        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        if (directory is { Exists: false, Parent: not null }) directory = directory.Parent;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenJibo.slnx"))) return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsLocalSelfHostedTarget(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || IsPrivateIpv4(address)) return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;

        var bytes = address.GetAddressBytes();
        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    private static TrustedServerRecord UpsertLifecycleServer(
        TrustedServerRecord? current,
        TrustedServerLifecycleRequest request,
        string serverKind,
        string canonicalHost,
        bool isActive,
        bool isListed,
        bool acceptsPublicConnections,
        bool participatesInCloudSync,
        bool requiresHttps,
        DateTimeOffset? lastSeenAtUtc)
    {
        return new TrustedServerRecord
        {
            ServerId = current?.ServerId ?? Guid.NewGuid().ToString("N"),
            CanonicalHost = canonicalHost,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? current?.DisplayName ?? canonicalHost
                : request.DisplayName.Trim(),
            ServerKind = serverKind,
            IsListed = isListed,
            AcceptsPublicConnections = acceptsPublicConnections,
            ParticipatesInCloudSync = participatesInCloudSync,
            RequiresHttps = requiresHttps,
            IsTrustRoot = current?.IsTrustRoot == true,
            IsActive = isActive,
            Description = request.Description?.Trim() ?? current?.Description ?? string.Empty,
            RegisteredAtUtc = current?.RegisteredAtUtc ?? DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastSeenAtUtc = lastSeenAtUtc ?? current?.LastSeenAtUtc
        };
    }
}
