using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record WebsiteRecheckBatchInput(List<string> CandidateIds, bool Enabled);
public sealed record WebsiteRecheckStatus(bool Enabled, int IntervalDays, DateTime? NextDueAt, DateTime? LastQueuedAt);
public sealed record WebsiteRecheckRow(string CandidateId, string ProposedName, string Domain,
    string CrmStage, int? CustomerStatus, WebsiteRecheckStatus Schedule,
    string? LatestReportId, string? LatestReportStatus, DateTime? LatestReportAt);

/// <summary>One explicit opt-in per confirmed website. Seven days is the fixed initial cadence.</summary>
public sealed class WebsiteRecheckService(CrmDbContext db)
{
    public async Task<object> ListAsync(string? crmStage, int page, CancellationToken ct)
    {
        if (crmStage is not null && crmStage is not ("customer" or "lead" or "identity"))
            throw new ArgumentException("CRM 阶段筛选无效。");
        page = Math.Clamp(page, 1, 10000);
        var query = db.IdentityCandidates.AsNoTracking().Where(candidate => candidate.Status == "confirmed");
        if (crmStage == "customer")
            query = query.Where(candidate => db.Leads.Any(lead => lead.SourceIdentityCandidateId == candidate.Id
                && lead.ConvertedToCompanyId != null));
        if (crmStage == "lead")
            query = query.Where(candidate => db.Leads.Any(lead => lead.SourceIdentityCandidateId == candidate.Id
                && lead.ConvertedToCompanyId == null)
                && !db.Leads.Any(lead => lead.SourceIdentityCandidateId == candidate.Id
                    && lead.ConvertedToCompanyId != null));
        if (crmStage == "identity")
            query = query.Where(candidate => !db.Leads.Any(lead => lead.SourceIdentityCandidateId == candidate.Id));
        var total = await query.CountAsync(ct);
        var candidates = await query.OrderBy(candidate => candidate.ProposedName).ThenBy(candidate => candidate.Id)
            .Skip((page - 1) * 50).Take(50).ToListAsync(ct);
        var ids = candidates.Select(candidate => candidate.Id).ToArray();
        var schedules = await db.WebsiteRecheckSchedules.AsNoTracking()
            .Where(schedule => ids.Contains(schedule.CandidateId)).ToDictionaryAsync(schedule => schedule.CandidateId, ct);
        var reports = await db.WebsiteDiagnosticReports.AsNoTracking()
            .Where(report => ids.Contains(report.CandidateId))
            .OrderByDescending(report => report.Version).ToListAsync(ct);
        var latestReportByCandidate = reports.GroupBy(report => report.CandidateId)
            .ToDictionary(group => group.Key, group => group.First());
        var leads = await db.Leads.AsNoTracking().Where(lead => lead.SourceIdentityCandidateId != null
            && ids.Contains(lead.SourceIdentityCandidateId.Value)).ToListAsync(ct);
        var leadByCandidate = leads.GroupBy(lead => lead.SourceIdentityCandidateId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(lead => lead.ConvertedToCompanyId.HasValue).First());
        var companyIds = leads.Where(lead => lead.ConvertedToCompanyId.HasValue)
            .Select(lead => lead.ConvertedToCompanyId!.Value).Distinct().ToArray();
        var companies = await db.Companies.AsNoTracking().Where(company => companyIds.Contains(company.Id))
            .ToDictionaryAsync(company => company.Id, ct);
        var rows = candidates.Select(candidate =>
        {
            schedules.TryGetValue(candidate.Id, out var schedule);
            latestReportByCandidate.TryGetValue(candidate.Id, out var latestReport);
            leadByCandidate.TryGetValue(candidate.Id, out var lead);
            var stage = lead?.ConvertedToCompanyId.HasValue == true ? "customer" : lead is null ? "identity" : "lead";
            int? customerStatus = lead?.ConvertedToCompanyId is long companyId && companies.TryGetValue(companyId, out var company)
                ? (int)company.Status : null;
            return new WebsiteRecheckRow(candidate.Id.ToString(), candidate.ProposedName,
                candidate.Domain, stage, customerStatus, Project(schedule),
                latestReport?.Id.ToString(), latestReport?.Status, latestReport?.CompletedAt ?? latestReport?.RequestedAt);
        }).ToList();
        return new { total, page, pageSize = 50, items = rows };
    }

    public async Task<WebsiteRecheckStatus> GetAsync(long candidateId, CancellationToken ct)
    {
        if (!await db.IdentityCandidates.AsNoTracking().AnyAsync(c => c.Id == candidateId, ct))
            throw new KeyNotFoundException("当前公司找不到该身份候选。");
        var schedule = await db.WebsiteRecheckSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CandidateId == candidateId, ct);
        return Project(schedule);
    }

    public async Task<object> HistoryAsync(long candidateId, CancellationToken ct)
    {
        if (!await db.IdentityCandidates.AsNoTracking().AnyAsync(c => c.Id == candidateId, ct))
            throw new KeyNotFoundException("当前公司找不到该身份候选。");
        var changes = await db.WebsiteRecheckChanges.AsNoTracking()
            .Where(change => change.CandidateId == candidateId)
            .OrderByDescending(change => change.ChangedAt).Take(100)
            .Select(change => new { change.Enabled, change.IntervalDays, change.ChangedAt, change.ChangedBy })
            .ToListAsync(ct);
        return new { items = changes };
    }

    public async Task<WebsiteRecheckStatus> UpdateAsync(long candidateId, bool enabled, long actorId, CancellationToken ct)
    {
        await UpdateBatchAsync(new([candidateId.ToString()], enabled), actorId, ct);
        return await GetAsync(candidateId, ct);
    }

    public async Task<int> UpdateBatchAsync(WebsiteRecheckBatchInput input, long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("操作人员身份无效。");
        if (input.CandidateIds is null || input.CandidateIds.Count is < 1 or > 50
            || input.CandidateIds.Any(id => !long.TryParse(id, out var parsed) || parsed <= 0))
            throw new ArgumentException("每次请选择 1–50 个网站。");
        var ids = input.CandidateIds.Select(long.Parse).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new ArgumentException("不能重复选择同一网站。");
        var candidates = await db.IdentityCandidates.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.Status == "confirmed").Select(c => c.Id).ToListAsync(ct);
        if (candidates.Count != ids.Length)
            throw new InvalidOperationException("所选网站包含未确认身份或不属于当前公司的记录，请刷新后重试。");
        var existing = await db.WebsiteRecheckSchedules.Where(s => ids.Contains(s.CandidateId)).ToListAsync(ct);
        var byId = existing.ToDictionary(s => s.CandidateId);
        var now = DateTime.UtcNow;
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var schedule))
            {
                schedule = new WebsiteRecheckSchedule { CandidateId = id };
                db.WebsiteRecheckSchedules.Add(schedule);
            }
            schedule.Enabled = input.Enabled;
            schedule.IntervalDays = 7;
            schedule.NextDueAt = input.Enabled ? now.AddDays(7) : null;
            schedule.UpdatedAt = now;
            schedule.UpdatedBy = actorId;
            db.WebsiteRecheckChanges.Add(new WebsiteRecheckChange
            {
                Id = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
                CandidateId = id, Enabled = input.Enabled, IntervalDays = 7,
                ChangedAt = now, ChangedBy = actorId
            });
        }
        await db.SaveChangesAsync(ct);
        return ids.Length;
    }

    private static WebsiteRecheckStatus Project(WebsiteRecheckSchedule? s) =>
        new(s?.Enabled ?? false, s?.IntervalDays ?? 7, s?.NextDueAt, s?.LastQueuedAt);
}

/// <summary>Enqueues only opted-in, confirmed websites; the existing diagnostic worker performs the requests.</summary>
public sealed class WebsiteRecheckWorker(IServiceScopeFactory scopes, ILogger<WebsiteRecheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await EnqueueNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Website recheck scheduler cycle failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    internal async Task<bool> EnqueueNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        // Serialize the daily quota and the queue claim across API processes sharing this database.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1196903250)", ct);
        var now = DateTime.UtcNow;
        var dayStart = now.Date;
        // Bound automatic requests for this database. Manual reports do not consume the quota.
        var today = await db.WebsiteDiagnosticReports.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(r => r.Trigger == "scheduled" && r.RequestedAt >= dayStart, ct);
        if (today >= 10) return false;
        var due = await db.WebsiteRecheckSchedules.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Enabled && s.NextDueAt <= now)
            .OrderBy(s => s.NextDueAt).FirstOrDefaultAsync(ct);
        if (due is null) return false;
        var candidate = await db.IdentityCandidates.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == due.CandidateId && c.TenantId == due.TenantId
                && c.CompanyId == due.CompanyId, ct);
        if (candidate?.Status != "confirmed")
        {
            await db.WebsiteRecheckSchedules.IgnoreQueryFilters()
                .Where(s => s.CandidateId == due.CandidateId && s.TenantId == due.TenantId && s.CompanyId == due.CompanyId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Enabled, false).SetProperty(s => s.NextDueAt, (DateTime?)null), ct);
            await transaction.CommitAsync(ct);
            return false;
        }
        var latest = await db.WebsiteDiagnosticReports.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.CandidateId == due.CandidateId && r.TenantId == due.TenantId && r.CompanyId == due.CompanyId)
            .OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        if (latest is not null && latest.RequestedAt > now.AddDays(-7))
        {
            await db.WebsiteRecheckSchedules.IgnoreQueryFilters()
                .Where(s => s.CandidateId == due.CandidateId && s.TenantId == due.TenantId && s.CompanyId == due.CompanyId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.NextDueAt, latest.RequestedAt.AddDays(7)), ct);
            await transaction.CommitAsync(ct);
            return false;
        }
        var claimed = await db.WebsiteRecheckSchedules.IgnoreQueryFilters()
            .Where(s => s.CandidateId == due.CandidateId && s.TenantId == due.TenantId && s.CompanyId == due.CompanyId
                && s.Enabled && s.NextDueAt == due.NextDueAt)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastQueuedAt, now)
                .SetProperty(s => s.NextDueAt, now.AddDays(7)), ct);
        if (claimed != 1) return false;
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport
        {
            Id = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
            TenantId = due.TenantId, CompanyId = due.CompanyId, CandidateId = due.CandidateId,
            Version = (latest?.Version ?? 0) + 1, Url = $"https://{candidate.Domain}/",
            RequestedBy = due.UpdatedBy, RequestedAt = now, Trigger = "scheduled", Status = "queued"
        });
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Scheduled diagnostic conflicted for candidate {CandidateId}", due.CandidateId);
            // The next cycle can retry after the seven-day hold is adjusted by an operator.
            return false;
        }
        return true;
    }
}
