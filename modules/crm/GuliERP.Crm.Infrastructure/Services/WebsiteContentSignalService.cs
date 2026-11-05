using System.Text.Json;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>Evidence-linked reminders. A technical recheck never claims that webpage content changed.</summary>
public static class WebsiteContentSignalService
{
    public const string ReviewSource = "website-content-review";
    public const string ChangeSource = "website-content-change";

    public static async Task<Signal?> BuildScheduledReviewReminderAsync(CrmDbContext db,
        WebsiteDiagnosticReport report, IdentityCandidate candidate, CancellationToken ct)
    {
        if (report.Trigger != "scheduled" || report.Status != "completed") return null;
        var previousReportIds = await db.WebsiteDiagnosticReports.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TenantId == report.TenantId && r.CompanyId == report.CompanyId
                && r.CandidateId == report.CandidateId && r.Version < report.Version
                && r.Status == "completed")
            .Select(r => r.Id).ToListAsync(ct);
        if (previousReportIds.Count == 0) return null;
        var hasBaseline = await db.WebsiteContentEvidence.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(e => e.TenantId == report.TenantId && e.CompanyId == report.CompanyId
                && e.CandidateId == report.CandidateId && e.Status == "accepted"
                && previousReportIds.Contains(e.ReportId), ct);
        if (!hasBaseline) return null;
        var key = $"report_id={report.Id}";
        if (await db.Signals.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(s => s.TenantId == report.TenantId && s.CompanyId == report.CompanyId
                && s.Source == ReviewSource && s.QueryParams == key, ct)) return null;
        return NewSignal(report, candidate, ReviewSource, key, "网站复检后请核对内容证据",
            "新的定期复检报告已完成；此前有已审核的内容证据。请人工核对页面并录入新证据，当前尚不能判断内容是否变化。",
            "website_diagnostic", JsonSerializer.Serialize(new
            {
                reportId = report.Id.ToString(), report.Version, report.MethodVersion,
                reason = "accepted_content_baseline_requires_manual_recheck", aiRecommendationTested = false
            }));
    }

    public static async Task<Signal?> BuildAcceptedDifferenceAsync(CrmDbContext db,
        WebsiteContentEvidence current, WebsiteDiagnosticReport currentReport,
        IdentityCandidate candidate, CancellationToken ct)
    {
        if (current.Status != "accepted" || currentReport.Status != "completed") return null;
        var previous = await (from evidence in db.WebsiteContentEvidence.AsNoTracking()
            join report in db.WebsiteDiagnosticReports.AsNoTracking() on evidence.ReportId equals report.Id
            where evidence.TenantId == current.TenantId && evidence.CompanyId == current.CompanyId
                && evidence.CandidateId == current.CandidateId && evidence.Status == "accepted"
                && evidence.SourceUrl == current.SourceUrl && evidence.ScenarioVersion == current.ScenarioVersion
                && report.TenantId == current.TenantId && report.CompanyId == current.CompanyId
                && report.CandidateId == current.CandidateId && report.Status == "completed"
                && report.MethodVersion == currentReport.MethodVersion && report.Version < currentReport.Version
            orderby report.Version descending, evidence.ReviewedAt descending
            select new { Evidence = evidence, Report = report }).FirstOrDefaultAsync(ct);
        if (previous is null || Normalize(previous.Evidence.Excerpt) == Normalize(current.Excerpt)) return null;
        var key = $"evidence_id={current.Id}";
        if (await db.Signals.AsNoTracking().AnyAsync(s => s.Source == ChangeSource && s.QueryParams == key, ct))
            return null;
        return NewSignal(currentReport, candidate, ChangeSource, key,
            "人工核验的网站内容出现差异",
            "同一页面的两份已审核摘录不同。请核对实际网页和业务影响；这不是 AI 平台推荐变化。",
            "website_content_evidence", JsonSerializer.Serialize(new
            {
                currentEvidenceId = current.Id.ToString(), currentReportId = currentReport.Id.ToString(),
                previousEvidenceId = previous.Evidence.Id.ToString(),
                previousReportId = previous.Report.Id.ToString(),
                scenarioVersion = current.ScenarioVersion, currentReport.MethodVersion,
                comparison = "normalized_excerpt_difference", aiRecommendationTested = false
            }), current.SourceUrl);
    }

    /// <summary>If an older observation is approved late, compare it to the next approved report too.</summary>
    public static async Task<Signal?> BuildNextAcceptedDifferenceAsync(CrmDbContext db,
        WebsiteContentEvidence older, WebsiteDiagnosticReport olderReport,
        IdentityCandidate candidate, CancellationToken ct)
    {
        if (older.Status != "accepted" || olderReport.Status != "completed") return null;
        var next = await (from evidence in db.WebsiteContentEvidence.AsNoTracking()
            join report in db.WebsiteDiagnosticReports.AsNoTracking() on evidence.ReportId equals report.Id
            where evidence.TenantId == older.TenantId && evidence.CompanyId == older.CompanyId
                && evidence.CandidateId == older.CandidateId && evidence.Status == "accepted"
                && evidence.SourceUrl == older.SourceUrl && evidence.ScenarioVersion == older.ScenarioVersion
                && report.TenantId == older.TenantId && report.CompanyId == older.CompanyId
                && report.CandidateId == older.CandidateId && report.Status == "completed"
                && report.MethodVersion == olderReport.MethodVersion && report.Version > olderReport.Version
            orderby report.Version, evidence.ReviewedAt
            select new { Evidence = evidence, Report = report }).FirstOrDefaultAsync(ct);
        if (next is null || Normalize(next.Evidence.Excerpt) == Normalize(older.Excerpt)) return null;
        var key = $"evidence_id={next.Evidence.Id}";
        if (await db.Signals.AsNoTracking().AnyAsync(s => s.Source == ChangeSource && s.QueryParams == key, ct))
            return null;
        return NewSignal(next.Report, candidate, ChangeSource, key,
            "人工核验的网站内容出现差异",
            "同一页面的两份已审核摘录不同。请核对实际网页和业务影响；这不是 AI 平台推荐变化。",
            "website_content_evidence", JsonSerializer.Serialize(new
            {
                currentEvidenceId = next.Evidence.Id.ToString(), currentReportId = next.Report.Id.ToString(),
                previousEvidenceId = older.Id.ToString(), previousReportId = olderReport.Id.ToString(),
                scenarioVersion = older.ScenarioVersion, olderReport.MethodVersion,
                comparison = "normalized_excerpt_difference", aiRecommendationTested = false
            }), older.SourceUrl);
    }

    internal static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static Signal NewSignal(WebsiteDiagnosticReport report, IdentityCandidate candidate,
        string source, string key, string title, string detail, string evidenceType,
        string rawContent, string? sourceUrl = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Signal
        {
            Id = (now.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
            TenantId = report.TenantId, CompanyId = report.CompanyId,
            CompanyName = candidate.ProposedName.Length > 200 ? candidate.ProposedName[..200] : candidate.ProposedName,
            Type = SignalType.Other, Title = title, Detail = detail, Source = source,
            SourceUrl = sourceUrl ?? report.Url, EventAt = now, CollectedAt = now, CreatedAt = now,
            Confidence = 50, Status = SignalStatus.Pending, EvidenceType = evidenceType,
            QueryParams = key, RawContent = rawContent
        };
    }
}
