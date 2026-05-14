namespace Jibo.Cloud.Application.Abstractions;

public interface IPersonalMemoryStore
{
    void SetBirthday(PersonalMemoryTenantScope tenantScope, string birthdayText);
    string? GetBirthday(PersonalMemoryTenantScope tenantScope);
    void SetPreference(PersonalMemoryTenantScope tenantScope, string category, string value);
    string? GetPreference(PersonalMemoryTenantScope tenantScope, string category);
    void SetName(PersonalMemoryTenantScope tenantScope, string name);
    string? GetName(PersonalMemoryTenantScope tenantScope);
    void SetImportantDate(PersonalMemoryTenantScope tenantScope, string label, string value);
    string? GetImportantDate(PersonalMemoryTenantScope tenantScope, string label);
    void SetAffinity(PersonalMemoryTenantScope tenantScope, string item, PersonalAffinity affinity);
    PersonalAffinity? GetAffinity(PersonalMemoryTenantScope tenantScope, string item);
    IReadOnlyDictionary<string, PersonalAffinity> GetAffinities(PersonalMemoryTenantScope tenantScope);
    void AddListItem(PersonalMemoryTenantScope tenantScope, string listName, string item);
    IReadOnlyList<string> GetListItems(PersonalMemoryTenantScope tenantScope, string listName);
    void ClearListItems(PersonalMemoryTenantScope tenantScope, string listName);
}

public sealed record PersonalMemoryTenantScope(string AccountId, string LoopId, string DeviceId);

public enum PersonalAffinity
{
    Like,
    Love,
    Dislike
}
