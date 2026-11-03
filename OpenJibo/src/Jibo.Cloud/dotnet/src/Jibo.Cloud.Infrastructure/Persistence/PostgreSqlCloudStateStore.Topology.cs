using System.Globalization;
using System.Text;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed partial class PostgreSqlCloudStateStore
{
    public IReadOnlyList<LoopRecord> GetLoops() =>
        Sync(Require(_loops, "loop topology").ListForAccountAsync(GetAccount().AccountId, 500))
            .Select(item => item.Loop).ToArray();

    public LoopRecord AddLoop(string? name, string? ownerAccountId, string? robotId, string? robotFriendlyId)
    {
        var loops = Require(_loops, "loop topology");
        var members = Require(_members, "loop members");
        var owner = string.IsNullOrWhiteSpace(ownerAccountId) ? GetAccount().AccountId : ownerAccountId.Trim();
        var resolvedRobotId = robotId?.Trim() ?? string.Empty;
        var resolvedFriendlyId = robotFriendlyId?.Trim() ?? string.Empty;
        var existing = Sync(loops.ListForAccountAsync(owner, 500)).FirstOrDefault(item =>
            LoopMatchesRobot(item.Loop, resolvedRobotId, resolvedFriendlyId));
        if (existing is not null) return existing.Loop;

        var baseName = string.IsNullOrWhiteSpace(name)
            ? string.IsNullOrWhiteSpace(resolvedFriendlyId) ? "OpenJibo Loop" : $"{resolvedFriendlyId} Loop"
            : name.Trim();
        var baseId = $"loop-{Slugify(string.IsNullOrWhiteSpace(resolvedFriendlyId) ? baseName : resolvedFriendlyId)}";
        if (baseId == "loop-") baseId = $"loop-{Guid.NewGuid():N}";
        var existingIds = Sync(loops.ListForAccountAsync(owner, 500)).Select(item => item.Loop.LoopId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loopId = baseId;
        for (var suffix = 2; existingIds.Contains(loopId); suffix++) loopId = $"{baseId}-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var device = !string.IsNullOrWhiteSpace(resolvedFriendlyId)
            ? Sync(_devices.GetByDeviceIdAsync(resolvedFriendlyId)) ?? FindDeviceByFriendlyId(resolvedFriendlyId)
            : null;
        device ??= !string.IsNullOrWhiteSpace(resolvedRobotId)
            ? GetDevices().FirstOrDefault(candidate =>
                candidate.RobotId.Equals(resolvedRobotId, StringComparison.OrdinalIgnoreCase))
            : null;
        var loop = new LoopRecord
        {
            LoopId = loopId,
            Name = baseName,
            OwnerAccountId = owner,
            RobotId = resolvedRobotId,
            RobotFriendlyId = resolvedFriendlyId,
            CreatedUtc = now,
            UpdatedUtc = now
        };
        Sync(loops.UpsertAsync(new StoredLoopTopology(loop,
            device is null ? [] : [new LoopDeviceLink(device.DeviceId, true, now)])));
        Sync(members.UpsertAsync(owner, new LoopMemberRecord
        {
            LoopId = loopId,
            AccountId = owner,
            FirstName = GetAccount().FirstName,
            LastName = GetAccount().LastName,
            Type = "owner",
            CreatedUtc = now
        }));
        if (!string.IsNullOrWhiteSpace(resolvedRobotId))
            Sync(members.UpsertAsync(owner, new LoopMemberRecord
            {
                LoopId = loopId,
                AccountId = resolvedRobotId,
                FirstName = string.IsNullOrWhiteSpace(resolvedFriendlyId) ? resolvedRobotId : resolvedFriendlyId,
                Type = "robot",
                CreatedUtc = now
            }));
        return loop;
    }

    public IReadOnlyList<PersonRecord> GetPeople(string? loopId = null)
    {
        var people = Require(_people, "people");
        var account = GetAccount().AccountId;
        if (!string.IsNullOrWhiteSpace(loopId))
            return Sync(people.ListAsync(account, loopId.Trim(), 1000));
        return GetLoops().SelectMany(loop => Sync(people.ListAsync(account, loop.LoopId, 1000))).ToArray();
    }

    public PersonRecord UpsertPerson(PersonRecord person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var now = DateTimeOffset.UtcNow;
        var resolved = new PersonRecord
        {
            PersonId = RequireValue(person.PersonId, nameof(person.PersonId)),
            AccountId = string.IsNullOrWhiteSpace(person.AccountId) ? GetAccount().AccountId : person.AccountId.Trim(),
            LoopId = string.IsNullOrWhiteSpace(person.LoopId) ? ResolveDefaultLoopId() : person.LoopId.Trim(),
            RobotId = string.IsNullOrWhiteSpace(person.RobotId) ? GetRobot().RobotId : person.RobotId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(person.DisplayName) ? person.PersonId.Trim() : person.DisplayName.Trim(),
            Alias = string.IsNullOrWhiteSpace(person.Alias) ? null : person.Alias.Trim(),
            IsPrimary = person.IsPrimary,
            CreatedUtc = person.CreatedUtc == default ? now : person.CreatedUtc,
            UpdatedUtc = now
        };
        return Sync(Require(_people, "people").UpsertAsync(resolved));
    }

    public int SyncPeopleFromLoopUsers(string loopId, string? robotId, IReadOnlyList<LoopUserSnapshot> loopUsers,
        string? ownerAccountId = null)
    {
        if (string.IsNullOrWhiteSpace(loopId) || loopUsers is null || loopUsers.Count == 0) return 0;
        var account = string.IsNullOrWhiteSpace(ownerAccountId) ? GetAccount().AccountId : ownerAccountId.Trim();
        var resolvedLoop = loopId.Trim();
        var resolvedRobot = string.IsNullOrWhiteSpace(robotId) ? GetRobot().RobotId : robotId.Trim();
        var people = Require(_people, "people");
        var members = Require(_members, "loop members");
        var currentPeople = Sync(people.ListAsync(account, resolvedLoop, 1000));
        var currentMembers = Sync(members.ListAsync(account, resolvedLoop, 1000));
        var rosterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upserted = 0;
        foreach (var user in loopUsers.Where(user => !string.IsNullOrWhiteSpace(user.Id) &&
                                                    !string.Equals(user.Type, "robot", StringComparison.OrdinalIgnoreCase)))
        {
            var id = user.Id.Trim();
            rosterIds.Add(id);
            var existingPerson = currentPeople.FirstOrDefault(item =>
                item.PersonId.Equals(id, StringComparison.OrdinalIgnoreCase));
            var existingMember = currentMembers.FirstOrDefault(item =>
                item.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(user.AccountId) &&
                 string.Equals(item.AccountId, user.AccountId, StringComparison.OrdinalIgnoreCase)));
            var first = user.FirstName?.Trim();
            var last = user.LastName?.Trim();
            var nickname = user.Nickname?.Trim();
            var display = !string.IsNullOrWhiteSpace(nickname)
                ? nickname
                : string.Join(' ', new[] { first, last }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(display)) display = id;
            var now = DateTimeOffset.UtcNow;
            Sync(people.UpsertAsync(new PersonRecord
            {
                PersonId = id,
                // People are owned by the loop's account scope. A loop user's optional account id
                // belongs on the member link and must not escape the composite persistence key.
                AccountId = account,
                LoopId = resolvedLoop,
                RobotId = resolvedRobot,
                DisplayName = display,
                Alias = nickname ?? first ?? existingPerson?.Alias,
                IsPrimary = existingPerson?.IsPrimary ?? string.Equals(user.Type, "owner", StringComparison.OrdinalIgnoreCase),
                CreatedUtc = existingPerson?.CreatedUtc ?? now,
                UpdatedUtc = now
            }));

            var protectPortal = existingMember?.PortalEditedUtc is not null;
            var robotMatchesPortal = protectPortal && NamesEqual(first, existingMember!.FirstName) &&
                                     NamesEqual(last, existingMember.LastName);
            Sync(members.UpsertAsync(account, new LoopMemberRecord
            {
                Id = existingMember?.Id ?? id,
                LoopId = resolvedLoop,
                AccountId = string.IsNullOrWhiteSpace(user.AccountId) ? existingMember?.AccountId : user.AccountId.Trim(),
                Email = existingMember?.Email,
                FirstName = protectPortal && !robotMatchesPortal ? existingMember!.FirstName : first ?? existingMember?.FirstName,
                LastName = protectPortal && !robotMatchesPortal ? existingMember!.LastName : last ?? existingMember?.LastName,
                Gender = existingMember?.Gender ?? "unknown",
                Birthday = existingMember?.Birthday,
                IsChild = existingMember?.IsChild ?? false,
                PhoneNumber = existingMember?.PhoneNumber,
                Status = "active",
                Type = string.IsNullOrWhiteSpace(user.Type) ? existingMember?.Type ?? "member" : user.Type.Trim(),
                Nickname = protectPortal && !robotMatchesPortal ? existingMember!.Nickname ?? nickname : nickname ?? existingMember?.Nickname,
                PhoneticName = existingMember?.PhoneticName,
                FaceEnrolled = existingMember?.FaceEnrolled ?? false,
                VoiceEnrolled = existingMember?.VoiceEnrolled ?? false,
                LegalGuardianId = existingMember?.LegalGuardianId,
                AgreementId = existingMember?.AgreementId,
                CreatedUtc = existingMember?.CreatedUtc ?? now,
                PortalEditedUtc = robotMatchesPortal ? null : existingMember?.PortalEditedUtc
            }));
            upserted++;
        }

        foreach (var stale in currentPeople.Where(item => item.RobotId.Equals(resolvedRobot,
                     StringComparison.OrdinalIgnoreCase) && !rosterIds.Contains(item.PersonId)))
        {
            var member = currentMembers.FirstOrDefault(item => item.Id.Equals(stale.PersonId,
                StringComparison.OrdinalIgnoreCase));
            if (member?.PortalEditedUtc is null)
                Sync(people.DeleteAsync(account, resolvedLoop, stale.PersonId));
        }
        return upserted;
    }

    public IReadOnlyList<LoopMemberRecord> GetLoopMembers(string loopId) =>
        Sync(Require(_members, "loop members").ListAsync(GetAccount().AccountId, loopId, 1000));

    public LoopMemberRecord AddLoopMember(string loopId, string? accountId, string? email, string? firstName,
        string? lastName, string? gender, long? birthday, bool isChild, string type,
        string? legalGuardianId = null, bool markPortalEdited = false) =>
        Sync(Require(_members, "loop members").UpsertAsync(GetAccount().AccountId, new LoopMemberRecord
        {
            LoopId = RequireValue(loopId, nameof(loopId)),
            AccountId = Normalize(accountId), Email = Normalize(email), FirstName = Normalize(firstName),
            LastName = Normalize(lastName), Gender = Normalize(gender), Birthday = birthday, IsChild = isChild,
            Type = string.IsNullOrWhiteSpace(type) ? "owner" : type.Trim(), LegalGuardianId = Normalize(legalGuardianId),
            PortalEditedUtc = markPortalEdited ? DateTimeOffset.UtcNow : null
        }));

    public LoopMemberRecord UpdateLoopMember(string loopId, string memberId, string? firstName, string? lastName,
        string? gender, long? birthday, bool isChild, string? nickname, string? phoneticName,
        bool markPortalEdited = false)
    {
        var repository = Require(_members, "loop members");
        var account = GetAccount().AccountId;
        var current = Sync(repository.GetAsync(account, loopId, memberId)) ??
                      throw new KeyNotFoundException("Loop member was not found.");
        return Sync(repository.UpsertAsync(account, CopyMember(current, firstName: Normalize(firstName),
            lastName: Normalize(lastName), gender: Normalize(gender), birthday: birthday, isChild: isChild,
            nickname: Normalize(nickname), phoneticName: Normalize(phoneticName),
            portalEditedUtc: markPortalEdited ? DateTimeOffset.UtcNow : current.PortalEditedUtc)));
    }

    public bool RemoveLoopMember(string loopId, string memberId) =>
        Sync(Require(_members, "loop members").DeleteAsync(GetAccount().AccountId, loopId, memberId));

    public LoopMemberRecord SetMemberEnrollment(string loopId, string memberId, bool? face, bool? voice)
    {
        var repository = Require(_members, "loop members");
        var account = GetAccount().AccountId;
        var current = Sync(repository.GetAsync(account, loopId, memberId)) ??
                      throw new KeyNotFoundException("Loop member was not found.");
        return Sync(repository.UpsertAsync(account, CopyMember(current,
            faceEnrolled: face ?? current.FaceEnrolled, voiceEnrolled: voice ?? current.VoiceEnrolled)));
    }

    public RecognitionObservationRecord RecordRecognitionObservation(string loopId, string memberId,
        string modality, string outcome, double? confidence = null, string? source = null) =>
        Sync(Require(_recognition, "recognition observations").AddAsync(GetAccount().AccountId,
            new RecognitionObservationRecord
            {
                LoopId = RequireValue(loopId, nameof(loopId)),
                MemberId = RequireValue(memberId, nameof(memberId)),
                RobotId = GetRobot().RobotId,
                Modality = RequireValue(modality, nameof(modality)).ToLowerInvariant(),
                Outcome = RequireValue(outcome, nameof(outcome)).ToLowerInvariant(),
                Confidence = confidence,
                Source = Normalize(source)
            }));

    public IReadOnlyList<RecognitionObservationRecord> GetRecognitionObservations(string loopId) =>
        Sync(Require(_recognition, "recognition observations").ListAsync(GetAccount().AccountId, loopId, 1000));

    private string ResolveDefaultLoopId() => GetLoops().FirstOrDefault()?.LoopId ?? DefaultLoopId;
    private static bool LoopMatchesRobot(LoopRecord loop, string robotId, string friendlyId)
    {
        var key = !string.IsNullOrWhiteSpace(robotId) ? robotId : friendlyId;
        return !string.IsNullOrWhiteSpace(key) &&
               (loop.RobotId.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                loop.RobotFriendlyId.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
    private static bool NamesEqual(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static string Slugify(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else if ((char.IsWhiteSpace(character) || character is '-' or '_') &&
                     builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        return builder.ToString().Trim('-');
    }
    private static T Require<T>(T? repository, string family) where T : class => repository ??
        throw new InvalidOperationException($"The normalized PostgreSQL {family} repository is not configured.");
    private static string RequireValue(string value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value.Trim() : throw new ArgumentException("Value is required.", name);
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static LoopMemberRecord CopyMember(LoopMemberRecord item, string? firstName = null,
        string? lastName = null, string? gender = null, long? birthday = null, bool? isChild = null,
        string? nickname = null, string? phoneticName = null, bool? faceEnrolled = null,
        bool? voiceEnrolled = null, DateTimeOffset? portalEditedUtc = null) => new()
    {
        Id = item.Id, LoopId = item.LoopId, AccountId = item.AccountId, Email = item.Email,
        FirstName = firstName ?? item.FirstName, LastName = lastName ?? item.LastName,
        Gender = gender ?? item.Gender, Birthday = birthday ?? item.Birthday,
        IsChild = isChild ?? item.IsChild, PhoneNumber = item.PhoneNumber, Status = item.Status, Type = item.Type,
        Nickname = nickname ?? item.Nickname, PhoneticName = phoneticName ?? item.PhoneticName,
        FaceEnrolled = faceEnrolled ?? item.FaceEnrolled, VoiceEnrolled = voiceEnrolled ?? item.VoiceEnrolled,
        LegalGuardianId = item.LegalGuardianId, AgreementId = item.AgreementId, CreatedUtc = item.CreatedUtc,
        PortalEditedUtc = portalEditedUtc ?? item.PortalEditedUtc
    };
}
