using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

public static class PortalSessionAccessGuard
{
    public static bool CanAccess(PortalSessionService.PortalSession session, ICloudStateStore store,
        bool allowAccountSession = false)
    {
        if (session.Kind == PortalSessionService.AdministratorSessionKind) return true;
        if (session.Kind == PortalSessionService.AccountSessionKind)
            return allowAccountSession && !string.IsNullOrWhiteSpace(session.UserId);
        if (session.Kind != PortalSessionService.RobotSessionKind) return false;

        // Re-read the durable link on each request, including for code-only sessions.
        // A transfer invalidates the old owner's proof across replicas, without token blacklists.
        var ownership = store.GetUserDeviceLink(session.DeviceId);
        if (!string.Equals(ownership?.EffectiveOwnershipEpoch, session.OwnershipEpoch, StringComparison.Ordinal)) return false;
        if (!string.Equals(ownership?.UserId, session.OwnerUserId,
                StringComparison.OrdinalIgnoreCase)) return false;
        if (session.UserId is not null && !string.Equals(session.UserId, session.OwnerUserId,
                StringComparison.OrdinalIgnoreCase)) return false;

        var robot = store.FindDeviceByFriendlyId(session.DeviceId);
        return robot is not null &&
               robot.DeviceId.Equals(session.DeviceId, StringComparison.OrdinalIgnoreCase) &&
               robot.RobotId.Equals(session.FriendlyId, StringComparison.OrdinalIgnoreCase);
    }
}
