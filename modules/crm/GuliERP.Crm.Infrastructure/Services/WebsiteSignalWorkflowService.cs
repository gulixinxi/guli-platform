using System.Globalization;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Application;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record WebsiteSignalDecisionInput(string Action, string Reason);
public sealed record WebsiteSignalAsset(string IdText, string BatchId, string? Domain,
    string? Hostname, string? IpAddress, int? Port, string? Title, string CleanVerdict,
    string? CleanReasons, DateTime ObservedAt, string? Server);
public sealed record WebsiteSignalTarget(string SignalId, string CandidateId, string ReportId,
    string? EvidenceId, WebsiteSignalAsset Asset);
public sealed record WebsitePendingSignal(string Id, string CompanyName, string Title, string Source,
    DateTimeOffset CollectedAt);
public sealed record WebsitePendingSummary(int Total, int Technical, int ContentReview, int ContentChange,
    IReadOnlyList<WebsitePendingSignal> Items);

/// <summary>Scoped navigation and audited closure for website signals only.</summary>
public sealed class WebsiteSignalWorkflowService(CrmDbContext db)
{
    public async Task<WebsitePendingSummary> PendingSummaryAsync(CancellationToken ct)
    {
        var pending = db.Signals.AsNoTracking().Where(s => s.Status == SignalStatus.Pending
            && (s.Source == "website-diagnostic" || s.Source == WebsiteContentSignalService.ReviewSource
                || s.Source == WebsiteContentSignalService.ChangeSource));
        var counts = await pending.GroupBy(s => s.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() }).ToListAsync(ct);
        var rows = await pending.OrderByDescending(s => s.CollectedAt).ThenByDescending(s => s.Id)
            .Take(5).Select(s => new { s.Id, s.CompanyName, s.Title, s.Source, s.CollectedAt })
            .ToListAsync(ct);
        int Count(string source) => counts.Where(x => x.Source == source).Sum(x => x.Count);
        return new WebsitePendingSummary(counts.Sum(x => x.Count), Count("website-diagnostic"),
            Count(WebsiteContentSignalService.ReviewSource), Count(WebsiteContentSignalService.ChangeSource),
            rows.Select(s => new WebsitePendingSignal(s.Id.ToString(CultureInfo.InvariantCulture),
                s.CompanyName, s.Title, s.Source, s.CollectedAt)).ToList());
    }

    public async Task<SignalDto> GetSignalAsync(long signalId, CancellationToken ct)
    {
        var signal = await db.Signals.AsNoTracking().FirstOrDefaultAsync(s => s.Id == signalId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该提醒。");
        if (signal.Source is not ("website-diagnostic" or WebsiteContentSignalService.ReviewSource
            or WebsiteContentSignalService.ChangeSource))
            throw new KeyNotFoundException("该提醒不是网站提醒。");
        return SignalService.ToDto(signal);
    }

    public async Task<WebsiteSignalTarget> TargetAsync(long signalId, CancellationToken ct)
    {
        var signal = await db.Signals.AsNoTracking().FirstOrDefaultAsync(s => s.Id == signalId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该提醒。");
        long reportId;
        string? evidenceId = null;
        if (signal.Source is "website-diagnostic" or WebsiteContentSignalService.ReviewSource)
            reportId = ParseId(signal.QueryParams, "report_id=");
        else if (signal.Source == WebsiteContentSignalService.ChangeSource)
        {
            var id = ParseId(signal.QueryParams, "evidence_id=");
            var evidence = await db.WebsiteContentEvidence.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id, ct)
                ?? throw new KeyNotFoundException("当前公司找不到对应内容证据。");
            evidenceId = evidence.Id.ToString(CultureInfo.InvariantCulture);
            reportId = evidence.ReportId;
        }
        else throw new KeyNotFoundException("该信号没有网站报告定位信息。");
        var report = await db.WebsiteDiagnosticReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到对应网站报告。");
        if (evidenceId is not null && !await db.WebsiteContentEvidence.AsNoTracking()
            .AnyAsync(e => e.Id == long.Parse(evidenceId, CultureInfo.InvariantCulture)
                && e.ReportId == report.Id && e.CandidateId == report.CandidateId, ct))
            throw new KeyNotFoundException("内容证据与网站报告不匹配。");
        var candidate = await db.IdentityCandidates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == report.CandidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到对应企业身份。");
        var asset = await db.QuakeAssetRecords.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == candidate.AssetId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到对应待审资产。");
        return new WebsiteSignalTarget(signal.Id.ToString(CultureInfo.InvariantCulture),
            candidate.Id.ToString(CultureInfo.InvariantCulture), report.Id.ToString(CultureInfo.InvariantCulture),
            evidenceId, new WebsiteSignalAsset(asset.Id.ToString(CultureInfo.InvariantCulture),
                asset.BatchId.ToString(), asset.Domain, asset.Hostname, asset.IpAddress,
                asset.Port, asset.Title, asset.CleanVerdict, asset.CleanReasons,
                asset.ObservedAt, asset.Source));
    }

    public async Task<object> DecideAsync(long signalId, WebsiteSignalDecisionInput input,
        long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("处理人身份无效。");
        if (input.Action is not ("reviewed" or "ignored")
            || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 10 or > 1000)
            throw new ArgumentException("请选择已核对或忽略，并填写 10–1000 字理由。");
        var target = await TargetAsync(signalId, ct);
        var signal = await db.Signals.AsNoTracking().SingleAsync(s => s.Id == signalId, ct);
        if (signal.Status != SignalStatus.Pending)
            throw new InvalidOperationException("提醒已处理，请刷新后查看。");
        if (input.Action == "reviewed" && signal.Source == WebsiteContentSignalService.ReviewSource)
        {
            var reportId = long.Parse(target.ReportId, CultureInfo.InvariantCulture);
            if (!await db.WebsiteContentEvidence.AsNoTracking()
                .AnyAsync(e => e.ReportId == reportId && e.Status == "accepted", ct))
                throw new InvalidOperationException("这份复检报告尚无已审核内容证据，不能标记已核对。");
        }
        if (input.Action == "reviewed" && signal.Source == WebsiteContentSignalService.ChangeSource)
        {
            var evidenceId = long.Parse(target.EvidenceId!, CultureInfo.InvariantCulture);
            if (!await db.WebsiteContentEvidence.AsNoTracking()
                .AnyAsync(e => e.Id == evidenceId && e.Status == "accepted", ct))
                throw new InvalidOperationException("对应内容证据尚未审核通过，不能标记已核对。");
        }
        var status = input.Action == "reviewed" ? SignalStatus.Confirmed : SignalStatus.Ignored;
        var now = DateTimeOffset.UtcNow;
        var updated = await db.Signals.Where(s => s.Id == signalId && s.Status == SignalStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, status)
                .SetProperty(s => s.HandledBy, actorId)
                .SetProperty(s => s.HandledAt, now)
                .SetProperty(s => s.HandleNote, input.Reason.Trim()), ct);
        if (updated != 1) throw new InvalidOperationException("提醒已由其他人员处理，请刷新后查看。");
        return new { id = target.SignalId, status = (int)status,
            handledBy = actorId.ToString(CultureInfo.InvariantCulture), handledAt = now,
            handleNote = input.Reason.Trim() };
    }

    private static long ParseId(string? query, string prefix)
    {
        if (query is null || !query.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(query.AsSpan(prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new KeyNotFoundException("提醒缺少有效的报告或证据编号。");
        return id;
    }
}
