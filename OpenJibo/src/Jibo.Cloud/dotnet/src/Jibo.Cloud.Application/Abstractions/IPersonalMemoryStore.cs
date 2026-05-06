namespace Jibo.Cloud.Application.Abstractions;

public interface IPersonalMemoryStore
{
    void SetBirthday(PersonalMemoryTenantScope tenantScope, string birthdayText);
    string? GetBirthday(PersonalMemoryTenantScope tenantScope);
    void SetPreference(PersonalMemoryTenantScope tenantScope, string category, string value);
    string? GetPreference(PersonalMemoryTenantScope tenantScope, string category);
}

public sealed record PersonalMemoryTenantScope(string AccountId, string LoopId, string DeviceId);
