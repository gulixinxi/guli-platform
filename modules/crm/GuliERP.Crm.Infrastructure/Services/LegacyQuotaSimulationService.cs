using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Foundation.Kernel;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record LegacyQuotaSimulationInput(string SourceCode, int PlannedAttempts,
    int DailyLimit, int MonthlyLimit);
public sealed record LegacyQuotaSimulationResult(string SourceCode, int PlannedAttempts,
    int ObservedDaily, int ObservedMonthly, int DailyLimit, int MonthlyLimit,
    bool WouldExceedDaily, bool WouldExceedMonthly, bool HistoryComplete = false,
    bool EnforcesLimit = false, bool PerformsExternalRequest = false);

/// <summary>Read-only what-if calculation. It never authorizes or reserves a supplier request.</summary>
public sealed class LegacyQuotaSimulationService(CrmDbContext db, ICurrentTenant tenant, ICurrentCompany company)
{
    public async Task<LegacyQuotaSimulationResult> PreviewAsync(LegacyQuotaSimulationInput input, CancellationToken ct)
    {
        if (tenant.Id is not > 0 || company.Id is not > 0)
            throw new InvalidOperationException("缺少租户或公司范围。");
        if (input.SourceCode is not ("quake" or "chinaz")) throw new ArgumentException("未知现有连接来源。");
        if (input.PlannedAttempts is < 1 or > 10 || input.DailyLimit is < 0 or > 100000000
            || input.MonthlyLimit is < 0 or > 100000000)
            throw new ArgumentException("模拟次数或额度无效。");
        var now = DateTime.UtcNow;
        var dayStart = now.Date;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var observed = await db.LegacyRequestObservations.AsNoTracking()
            .Where(x => x.SourceCode == input.SourceCode && x.ObservedAt >= monthStart)
            .Select(x => x.ObservedAt).ToListAsync(ct);
        var daily = observed.Count(x => x >= dayStart);
        var monthly = observed.Count;
        return new(input.SourceCode, input.PlannedAttempts, daily, monthly,
            input.DailyLimit, input.MonthlyLimit,
            (long)daily + input.PlannedAttempts > input.DailyLimit,
            (long)monthly + input.PlannedAttempts > input.MonthlyLimit);
    }
}
