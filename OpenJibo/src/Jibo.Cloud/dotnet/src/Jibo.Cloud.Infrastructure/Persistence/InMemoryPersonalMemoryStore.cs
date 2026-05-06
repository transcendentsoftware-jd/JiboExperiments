using System.Collections.Concurrent;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed class InMemoryPersonalMemoryStore : IPersonalMemoryStore
{
    private readonly ConcurrentDictionary<string, TenantMemoryRecord> _tenantMemory = new(StringComparer.OrdinalIgnoreCase);

    public void SetBirthday(PersonalMemoryTenantScope tenantScope, string birthdayText)
    {
        var key = BuildTenantKey(tenantScope);
        var record = _tenantMemory.GetOrAdd(key, static _ => new TenantMemoryRecord());
        record.Birthday = birthdayText;
    }

    public string? GetBirthday(PersonalMemoryTenantScope tenantScope)
    {
        var key = BuildTenantKey(tenantScope);
        return _tenantMemory.TryGetValue(key, out var record) ? record.Birthday : null;
    }

    public void SetPreference(PersonalMemoryTenantScope tenantScope, string category, string value)
    {
        var key = BuildTenantKey(tenantScope);
        var record = _tenantMemory.GetOrAdd(key, static _ => new TenantMemoryRecord());
        record.Preferences[NormalizeCategory(category)] = value;
    }

    public string? GetPreference(PersonalMemoryTenantScope tenantScope, string category)
    {
        var key = BuildTenantKey(tenantScope);
        return _tenantMemory.TryGetValue(key, out var record) &&
               record.Preferences.TryGetValue(NormalizeCategory(category), out var value)
            ? value
            : null;
    }

    private static string BuildTenantKey(PersonalMemoryTenantScope tenantScope)
    {
        return $"{tenantScope.AccountId}|{tenantScope.LoopId}|{tenantScope.DeviceId}";
    }

    private static string NormalizeCategory(string category)
    {
        return category.Trim().ToLowerInvariant();
    }

    private sealed class TenantMemoryRecord
    {
        public string? Birthday { get; set; }
        public ConcurrentDictionary<string, string> Preferences { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
