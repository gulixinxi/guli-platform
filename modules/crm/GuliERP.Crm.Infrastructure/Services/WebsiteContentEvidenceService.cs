using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record WebsiteContentEvidenceInput(string ReportId, string SourceUrl, string Excerpt,
    string Observation, string Limitation, DateTime ObservedAt);
public sealed record WebsiteContentReviewInput(string Decision, string Reason);

/// <summary>Manual content evidence only. It never calls an AI provider or estimates recommendation rates.</summary>
public sealed class WebsiteContentEvidenceService(CrmDbContext db)
{
    public async Task<object> ListAsync(long candidateId, CancellationToken ct)
    {
        await RequireCandidate(candidateId, ct);
        var items = await db.WebsiteContentEvidence.AsNoTracking()
            .Where(e => e.CandidateId == candidateId).OrderByDescending(e => e.RecordedAt)
            .Take(100).ToListAsync(ct);
        string? reviewDueReportId = null;
        var latest = await db.WebsiteDiagnosticReports.AsNoTracking()
            .Where(r => r.CandidateId == candidateId).OrderByDescending(r => r.Version)
            .FirstOrDefaultAsync(ct);
        if (latest is { Trigger: "scheduled", Status: "completed" })
        {
            var priorIds = await db.WebsiteDiagnosticReports.AsNoTracking()
                .Where(r => r.CandidateId == candidateId && r.Version < latest.Version
                    && r.Status == "completed")
                .Select(r => r.Id).ToListAsync(ct);
            var acceptedPrior = priorIds.Count > 0 && await db.WebsiteContentEvidence.AsNoTracking()
                .AnyAsync(e => e.CandidateId == candidateId && e.Status == "accepted"
                    && priorIds.Contains(e.ReportId), ct);
            var acceptedCurrent = await db.WebsiteContentEvidence.AsNoTracking()
                .AnyAsync(e => e.CandidateId == candidateId && e.ReportId == latest.Id
                    && e.Status == "accepted", ct);
            if (acceptedPrior && !acceptedCurrent) reviewDueReportId = latest.Id.ToString();
        }
        return new { items = items.Select(Project).ToList(), reviewDueReportId };
    }

    public async Task<object> GetAsync(long candidateId, long evidenceId, CancellationToken ct)
    {
        await RequireCandidate(candidateId, ct);
        var evidence = await db.WebsiteContentEvidence.AsNoTracking()
            .FirstOrDefaultAsync(e => e.CandidateId == candidateId && e.Id == evidenceId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该内容证据。");
        return Project(evidence);
    }

    public async Task<object> RecordAsync(long candidateId, WebsiteContentEvidenceInput input, long actorId,
        CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("操作人员身份无效。");
        var candidate = await RequireCandidate(candidateId, ct);
        if (candidate.Status != "confirmed") throw new InvalidOperationException("请先确认企业身份。");
        if (!long.TryParse(input.ReportId, out var reportId) || reportId <= 0)
            throw new ArgumentException("请选择有效的网站报告。");
        var report = await db.WebsiteDiagnosticReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CandidateId == candidateId, ct)
            ?? throw new ArgumentException("所选报告不属于该企业身份候选。");
        if (report.Status != "completed") throw new InvalidOperationException("只能关联已完成的网站报告。");
        if (!Uri.TryCreate(input.SourceUrl, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(source.UserInfo)
            || !string.Equals(source.IdnHost, new Uri(report.Url).IdnHost, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("证据链接必须是该报告域名下的 HTTPS 页面。");
        if (source.AbsoluteUri.Length > 500)
            throw new ArgumentException("证据链接不能超过 500 字符。");
        if (string.IsNullOrWhiteSpace(input.Excerpt) || input.Excerpt.Length > 2000
            || string.IsNullOrWhiteSpace(input.Observation) || input.Observation.Length > 2000
            || string.IsNullOrWhiteSpace(input.Limitation) || input.Limitation.Length > 1000)
            throw new ArgumentException("请填写证据原文、观察结论和局限说明，并遵守长度限制。");
        var now = DateTime.UtcNow;
        if (input.ObservedAt.Kind != DateTimeKind.Utc || input.ObservedAt > now.AddMinutes(5)
            || input.ObservedAt < now.AddYears(-10))
            throw new ArgumentException("观察时间必须是有效的 UTC 时间。");
        var row = new WebsiteContentEvidence
        {
            Id = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
            CandidateId = candidateId, ReportId = report.Id,
            SourceUrl = source.AbsoluteUri, Excerpt = input.Excerpt.Trim(),
            Observation = input.Observation.Trim(), Limitation = input.Limitation.Trim(),
            ObservedAt = input.ObservedAt, RecordedAt = now, RecordedBy = actorId
        };
        db.WebsiteContentEvidence.Add(row);
        await db.SaveChangesAsync(ct);
        return Project(row);
    }

    public async Task<object> ReviewAsync(long candidateId, long evidenceId, WebsiteContentReviewInput input,
        long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("操作人员身份无效。");
        var candidate = await RequireCandidate(candidateId, ct);
        if (input.Decision == "accepted" && candidate.Status != "confirmed")
            throw new InvalidOperationException("企业身份已撤销，请先完成身份审核。");
        if (input.Decision is not ("accepted" or "rejected")
            || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length > 1000)
            throw new ArgumentException("请选择通过或驳回，并填写审核理由。");
        var row = await db.WebsiteContentEvidence
            .FirstOrDefaultAsync(e => e.Id == evidenceId && e.CandidateId == candidateId, ct);
        if (row is null || row.Status != "pending")
            throw new InvalidOperationException("证据不存在或已审核，请刷新后重试。");
        row.Status = input.Decision;
        row.ReviewedAt = DateTime.UtcNow;
        row.ReviewedBy = actorId;
        row.ReviewReason = input.Reason.Trim();
        if (input.Decision == "accepted")
        {
            var report = await db.WebsiteDiagnosticReports.AsNoTracking()
                .SingleAsync(r => r.Id == row.ReportId && r.CandidateId == candidateId, ct);
            var changeSignal = await WebsiteContentSignalService.BuildAcceptedDifferenceAsync(
                db, row, report, candidate, ct);
            if (changeSignal is not null) db.Signals.Add(changeSignal);
            var laterSignal = await WebsiteContentSignalService.BuildNextAcceptedDifferenceAsync(
                db, row, report, candidate, ct);
            if (laterSignal is not null) db.Signals.Add(laterSignal);
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            foreach (var pending in db.ChangeTracker.Entries<Signal>()
                .Where(entry => entry.State == EntityState.Added).ToList())
                pending.State = EntityState.Detached;
            throw new InvalidOperationException("证据已由其他人员审核，请刷新后重试。");
        }
        return Project(row);
    }

    private async Task<IdentityCandidate> RequireCandidate(long id, CancellationToken ct) =>
        await db.IdentityCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new KeyNotFoundException("当前公司找不到该身份候选。");

    private static object Project(WebsiteContentEvidence e) => new
    {
        id = e.Id.ToString(), candidateId = e.CandidateId.ToString(), reportId = e.ReportId.ToString(),
        e.ScenarioVersion, e.MethodVersion, platform = "manual", aiRecommendationTested = false,
        e.SourceUrl, e.Excerpt, e.Observation, e.Limitation, e.Status,
        e.ObservedAt, e.RecordedAt, recordedBy = e.RecordedBy.ToString(),
        e.ReviewedAt, reviewedBy = e.ReviewedBy?.ToString(), e.ReviewReason
    };
}
