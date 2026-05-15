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

    public void SetName(PersonalMemoryTenantScope tenantScope, string name)
    {
        var key = BuildTenantKey(tenantScope);
        var record = _tenantMemory.GetOrAdd(key, static _ => new TenantMemoryRecord());
        record.Name = name;
    }

    public string? GetName(PersonalMemoryTenantScope tenantScope)
    {
        var key = BuildTenantKey(tenantScope);
        return _tenantMemory.TryGetValue(key, out var record) ? record.Name : null;
    }

    public void SetImportantDate(PersonalMemoryTenantScope tenantScope, string label, string value)
    {
        var key = BuildTenantKey(tenantScope);
        var record = _tenantMemory.GetOrAdd(key, static _ => new TenantMemoryRecord());
        record.ImportantDates[NormalizeCategory(label)] = value;
    }

    public string? GetImportantDate(PersonalMemoryTenantScope tenantScope, string label)
    {
        var key = BuildTenantKey(tenantScope);
        return _tenantMemory.TryGetValue(key, out var record) &&
               record.ImportantDates.TryGetValue(NormalizeCategory(label), out var value)
            ? value
            : null;
    }

    public void SetAffinity(PersonalMemoryTenantScope tenantScope, string item, PersonalAffinity affinity)
    {
        var key = BuildTenantKey(tenantScope);
        var record = _tenantMemory.GetOrAdd(key, static _ => new TenantMemoryRecord());
        record.Affinities[NormalizeCategory(item)] = affinity;
    }

    public PersonalAffinity? GetAffinity(PersonalMemoryTenantScope tenantScope, string item)
    {
        var key = BuildTenantKey(tenantScope);
        return _tenantMemory.TryGetValue(key, out var record) &&
               record.Affinities.TryGetValue(NormalizeCategory(item), out var affinity)
            ? affinity
            : null;
    }

    public IReadOnlyDictionary<string, PersonalAffinity> GetAffinities(PersonalMemoryTenantScope tenantScope)
    {
        var key = BuildTenantKey(tenantScope);
        if (!_tenantMemory.TryGetValue(key, out var record))
        {
            return new Dictionary<string, PersonalAffinity>(StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, PersonalAffinity>(record.Affinities, StringComparer.OrdinalIgnoreCase);
    }

    public void AddListItem(PersonalMemoryTenantScope tenantScope, string listName, string item)
    {
        var normalizedListName = NormalizeCategory(listName);
        var normalizedItem = item.Trim();
        if (string.IsNullOrWhiteSpace(normalizedListName) || string.IsNullOrWhiteSpace(normalizedItem))
        {
            return;
        }

        var key = BuildTenantKey(tenantScope);
        var record = _tenantMemory.GetOrAdd(key, static _ => new TenantMemoryRecord());
        lock (record.SyncRoot)
        {
            var list = record.Lists.GetOrAdd(normalizedListName, static _ => []);
            if (list.Any(value => string.Equals(value, normalizedItem, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            list.Add(normalizedItem);
        }
    }

    public IReadOnlyList<string> GetListItems(PersonalMemoryTenantScope tenantScope, string listName)
    {
        var key = BuildTenantKey(tenantScope);
        if (!_tenantMemory.TryGetValue(key, out var record))
        {
            return [];
        }

        var normalizedListName = NormalizeCategory(listName);
        lock (record.SyncRoot)
        {
            return record.Lists.TryGetValue(normalizedListName, out var list)
                ? [.. list]
                : [];
        }
    }

    public void ClearListItems(PersonalMemoryTenantScope tenantScope, string listName)
    {
        var key = BuildTenantKey(tenantScope);
        if (!_tenantMemory.TryGetValue(key, out var record))
        {
            return;
        }

        lock (record.SyncRoot)
        {
            record.Lists.TryRemove(NormalizeCategory(listName), out _);
        }
    }

    private static string BuildTenantKey(PersonalMemoryTenantScope tenantScope)
    {
        return string.IsNullOrWhiteSpace(tenantScope.PersonId)
            ? $"{tenantScope.AccountId}|{tenantScope.LoopId}|{tenantScope.DeviceId}"
            : $"{tenantScope.AccountId}|{tenantScope.LoopId}|{tenantScope.DeviceId}|{tenantScope.PersonId}";
    }

    private static string NormalizeCategory(string category)
    {
        return category.Trim().ToLowerInvariant();
    }

    private sealed class TenantMemoryRecord
    {
        public string? Birthday { get; set; }
        public string? Name { get; set; }
        public ConcurrentDictionary<string, string> Preferences { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, string> ImportantDates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, PersonalAffinity> Affinities { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, List<string>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object SyncRoot { get; } = new();
    }
}
