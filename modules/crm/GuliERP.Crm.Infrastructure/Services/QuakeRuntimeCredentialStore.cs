using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>
/// Company-scoped Quake credentials. Registered production instances persist in Windows
/// Credential Manager; direct test instances remain in memory. No read API exposes the key.
/// </summary>
public sealed class QuakeRuntimeCredentialStore(IConfiguration configuration, IQuakeCredentialPersistence? persistence = null)
{
    public sealed record QuakeKey(Guid Id, string Value, string Source, long Sequence = 0, int Slot = 0);
    private readonly ConcurrentDictionary<(long TenantId, long CompanyId), List<QuakeKey>> _keys = new();
    private readonly object _gate = new();

    public (bool Configured, string Source) GetStatus(long? tenantId, long? companyId)
    {
        if (tenantId is > 0 && companyId is > 0 && GetRuntimeCount(tenantId.Value, companyId.Value) > 0)
            return (true, persistence is null ? "runtime" : "windows_credential");

        return !EnvironmentKeyMatchesScope(tenantId, companyId)
            ? (false, "none")
            : (true, "environment");
    }

    public int GetRuntimeCount(long tenantId, long companyId)
    {
        var list = Entries(tenantId, companyId);
        lock (_gate) return list.Count;
    }

    public IReadOnlyList<QuakeKey> GetCandidates(long? tenantId, long? companyId)
    {
        var result = new List<QuakeKey>();
        if (tenantId is > 0 && companyId is > 0)
        {
            var list = Entries(tenantId.Value, companyId.Value);
            lock (_gate) result.AddRange(list);
        }
        var fallback = configuration["Quake:ApiKey"];
        if (EnvironmentKeyMatchesScope(tenantId, companyId)
            && !result.Any(k => k.Value == fallback!.Trim()))
            result.Add(new QuakeKey(Guid.Empty, fallback!.Trim(), "environment"));
        return result;
    }

    private bool EnvironmentKeyMatchesScope(long? tenantId, long? companyId)
    {
        if (tenantId is not > 0 || companyId is not > 0
            || !long.TryParse(configuration["Quake:TenantId"], out var configuredTenant)
            || !long.TryParse(configuration["Quake:CompanyId"], out var configuredCompany)
            || configuredTenant != tenantId || configuredCompany != companyId
            || string.IsNullOrWhiteSpace(configuration["Quake:ApiKey"]) 
            || !int.TryParse(configuration["Quake:Port"], out var configuredPort))
            return false;

        // The old tenant/company-only fallback could reuse a test key when a
        // new database starts with the same numeric IDs. Require an explicit
        // database target match before considering any environment key.
        var connection = configuration.GetConnectionString("GuliERP")
            ?? configuration["GULIERP_ConnectionStrings:GuliERP"];
        if (string.IsNullOrWhiteSpace(connection)) return false;
        try
        {
            var target = new NpgsqlConnectionStringBuilder(connection);
            return configuredPort == target.Port
                && string.Equals(configuration["Quake:Host"], target.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(configuration["Quake:Database"], target.Database, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    public string? GetKey(long? tenantId, long? companyId) => GetCandidates(tenantId, companyId).FirstOrDefault()?.Value;

    public Guid SetKey(long tenantId, long companyId, string? key)
    {
        if (tenantId <= 0 || companyId <= 0)
            throw new ArgumentException("A tenant and company are required.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.Any(char.IsControl))
            throw new ArgumentException("Quake key is missing or invalid.");

        var value = key.Trim();
        var list = Entries(tenantId, companyId);
        lock (_gate)
        {
            var existing = list.FirstOrDefault(k => k.Value == value);
            if (existing is not null) return existing.Id;
            if (list.Count >= 5) throw new ArgumentException("At most five runtime Quake keys are allowed.");
            var slot = Enumerable.Range(1, 5).First(i => list.All(k => k.Slot != i));
            var sequence = list.Count == 0 ? 1 : list.Max(k => k.Sequence) + 1;
            var entry = new QuakeKey(Guid.NewGuid(), value,
                persistence is null ? "runtime" : "windows_credential", sequence, slot);
            persistence?.Write(tenantId, companyId,
                new QuakeStoredCredential(entry.Id, entry.Value, entry.Sequence, entry.Slot));
            list.Add(entry);
            return entry.Id;
        }
    }

    public bool RemoveKey(long tenantId, long companyId, Guid id)
    {
        if (id == Guid.Empty) return false;
        var list = Entries(tenantId, companyId);
        lock (_gate)
        {
            var entry = list.FirstOrDefault(k => k.Id == id);
            if (entry is null) return false;
            persistence?.Delete(tenantId, companyId, entry.Slot);
            list.Remove(entry);
            return true;
        }
    }

    public IReadOnlyList<object> GetPublicEntries(long tenantId, long companyId)
    {
        var list = Entries(tenantId, companyId);
        lock (_gate) return list.Select((k, i) => (object)new { id = k.Id, order = i + 1 }).ToArray();
    }

    private List<QuakeKey> Entries(long tenantId, long companyId) =>
        _keys.GetOrAdd((tenantId, companyId), scope => persistence is null ? [] :
            persistence.Load(scope.TenantId, scope.CompanyId)
                .Select(k => new QuakeKey(k.Id, k.Value, "windows_credential", k.Sequence, k.Slot))
                .OrderBy(k => k.Sequence).ToList());
}
