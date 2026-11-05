using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>
/// Default-off, non-billing observation of physical legacy connector attempts.
/// No target, credential, response body or billing claim is retained.
/// </summary>
public sealed class LegacyRequestTelemetry(IConfiguration configuration, CrmDbContext? db = null)
{
    public sealed record Summary(string SourceCode, string Route, string Outcome, int Count);
    private readonly ConcurrentDictionary<(long TenantId, long CompanyId, string Source, string Route, string Outcome), int> _counts = new();
    public bool Enabled => bool.TryParse(configuration["Crm:LegacyRequestTelemetry:Enabled"], out var enabled) && enabled;

    public void Observe(long? tenantId, long? companyId, string source, string route, string outcome)
    {
        if (!Enabled || tenantId is not > 0 || companyId is not > 0) return;
        if (source is not ("quake" or "chinaz") || route is not ("search" or "asset_fetch" or "batch_lookup")) return;
        _counts.AddOrUpdate((tenantId.Value, companyId.Value, source, route, outcome), 1, (_, count) => count == int.MaxValue ? count : count + 1);
    }

    public async Task ObserveAsync(long? tenantId, long? companyId, string source, string route,
        string outcome, CancellationToken ct = default)
    {
        if (!Enabled || tenantId is not > 0 || companyId is not > 0) return;
        if (source is not ("quake" or "chinaz") || route is not ("search" or "asset_fetch" or "batch_lookup")) return;
        if (db is null) { Observe(tenantId, companyId, source, route, outcome); return; }
        var row = new LegacyRequestObservation
        {
            TenantId = tenantId.Value, CompanyId = companyId.Value,
            SourceCode = source, Route = route, Outcome = outcome
        };
        try
        {
            db.LegacyRequestObservations.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            // Observation is intentionally non-blocking; an unavailable telemetry table must
            // not alter the established supplier response or trigger another vendor request.
            db.Entry(row).State = EntityState.Detached;
        }
    }

    public async Task<IReadOnlyList<Summary>> SnapshotAsync(long? tenantId, long? companyId, CancellationToken ct)
    {
        if (tenantId is not > 0 || companyId is not > 0) return [];
        if (db is null) return Snapshot(tenantId, companyId);
        var rows = await db.LegacyRequestObservations.AsNoTracking()
            .GroupBy(x => new { x.SourceCode, x.Route, x.Outcome })
            .Select(g => new { g.Key.SourceCode, g.Key.Route, g.Key.Outcome, Count = g.Count() })
            .OrderBy(x => x.SourceCode).ThenBy(x => x.Route).ThenBy(x => x.Outcome)
            .ToListAsync(ct);
        return rows.Select(x => new Summary(x.SourceCode, x.Route, x.Outcome, x.Count)).ToArray();
    }

    public IReadOnlyList<Summary> Snapshot(long? tenantId, long? companyId)
    {
        if (!Enabled || tenantId is not > 0 || companyId is not > 0) return [];
        return _counts.Where(x => x.Key.TenantId == tenantId && x.Key.CompanyId == companyId)
            .OrderBy(x => x.Key.Source).ThenBy(x => x.Key.Route).ThenBy(x => x.Key.Outcome)
            .Select(x => new Summary(x.Key.Source, x.Key.Route, x.Key.Outcome, x.Value)).ToArray();
    }
}
