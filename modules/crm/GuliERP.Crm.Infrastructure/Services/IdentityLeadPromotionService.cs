using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record PromoteIdentityLeadInput(int ExpectedVersion);
public sealed record PromotionLeadMatch(string Id, string CompanyName);
public sealed record PromotionPreview(string CandidateId, string ProposedName, string Domain,
    string Status, int Version, bool Ready, List<string> Blockers,
    PromotionLeadMatch? ExistingLead, List<PromotionLeadMatch> SameName,
    List<PromotionLeadMatch> SameDomain, int CompletedReportCount, string Note);

/// <summary>Promotes one confirmed identity through a separate, auditable CRM entry point.</summary>
public sealed class IdentityLeadPromotionService(CrmDbContext db, ILeadService leads)
{
    public async Task<PromotionPreview> PreviewAsync(long candidateId, CancellationToken ct)
    {
        var candidate = await db.IdentityCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该身份候选。");
        var blockers = new List<string>();
        if (candidate.Status != "confirmed") blockers.Add("主体身份尚未确认。");
        // Identity candidates may be wider than CRM leads. Keep the evidence record intact,
        // but stop promotion before LeadService attempts an insert that cannot fit the lead columns.
        if (candidate.ProposedName.Length > 200) blockers.Add("候选主体名称超过线索公司名称的 200 字上限，请重新建立可转入的身份候选。");
        if (candidate.Domain.Length > 200) blockers.Add("候选域名超过线索官网域名的 200 字上限，请人工处理后再转入。");
        var hasRecentEvidence = await db.IdentityEvidence.AsNoTracking()
            .AnyAsync(e => e.CandidateId == candidateId && e.SourceType != "manual_note" && e.SourceType != "third_party"
                && e.ObservedAt >= DateTime.UtcNow.AddDays(-365), ct);
        if (!hasRecentEvidence) blockers.Add("没有 365 天内的外部来源证据。");
        var prior = await db.Leads.AsNoTracking()
            .Where(l => l.SourceIdentityCandidateId == candidateId)
            .Select(l => new PromotionLeadMatch(l.Id.ToString(), l.CompanyName)).FirstOrDefaultAsync(ct);
        if (prior is not null) blockers.Add("该身份候选已转为线索。");
        var sameName = await db.Leads.AsNoTracking()
            .Where(l => l.CompanyName.Trim().ToLower() == candidate.ProposedName.Trim().ToLower())
            .Select(l => new PromotionLeadMatch(l.Id.ToString(), l.CompanyName)).Take(5).ToListAsync(ct);
        if (sameName.Count > 0 && prior is null) blockers.Add("已有同名线索，需要人工核对主体关系。");
        var sameDomain = await db.Leads.AsNoTracking()
            .Where(l => l.WebsiteDomain == candidate.Domain && l.SourceIdentityCandidateId != candidateId)
            .Select(l => new PromotionLeadMatch(l.Id.ToString(), l.CompanyName)).Take(5).ToListAsync(ct);
        if (sameDomain.Count > 0) blockers.Add("该域名已关联其他线索，需要人工核对一域多主体关系。");
        var reportCount = await db.WebsiteDiagnosticReports.AsNoTracking()
            .CountAsync(r => r.CandidateId == candidateId && r.Status == "completed", ct);
        return new PromotionPreview(candidate.Id.ToString(), candidate.ProposedName, candidate.Domain,
            candidate.Status, candidate.Version, blockers.Count == 0, blockers,
            prior, sameName, sameDomain, reportCount,
            "网站报告不是转线索的必备条件；信用代码和联系人仍为未知，不会自动推断。");
    }

    public async Task<object> PromoteAsync(long candidateId, PromoteIdentityLeadInput input, long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("操作人员身份无效。");
        var candidate = await db.IdentityCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该身份候选。");
        if (candidate.Version != input.ExpectedVersion)
            throw new DbUpdateConcurrencyException("身份审核版本已变化，请重新预检。");
        var preview = await PreviewAsync(candidateId, ct);
        // Keep the preview contract as the single source for all promotion blockers.
        if (!preview.Ready)
            throw new InvalidOperationException("预检未通过，请先处理重复线索或补充近期证据。");

        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var request = new CreateLeadRequest(
            candidate.ProposedName, null, candidate.Domain, null, null, null, null, null, null,
            null, null, null, null, null, SourceChannel.Other,
            $"https://{candidate.Domain}/", candidate.Id.ToString(), "quake_identity", null);
        var created = await leads.CreateAsync(request, actorId, ct);
        var lead = await db.Leads.FirstAsync(l => l.Id == created.Id, ct);
        if (lead.SourceIdentityCandidateId is not null || lead.ExternalSource != "quake_identity"
            || lead.ExternalKey != candidate.Id.ToString())
            throw new InvalidOperationException("线索身份冲突，请人工核对；未完成转入。");
        lead.SourceIdentityCandidateId = candidate.Id;
        candidate.Version++;
        db.IdentityReviewEvents.Add(new IdentityReviewEvent
        {
            Id = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
            CandidateId = candidate.Id, Action = "promote_lead",
            Reason = $"已从确认身份候选转入线索 {lead.Id}", Version = candidate.Version,
            ActorId = actorId, OccurredAt = DateTime.UtcNow
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) { throw new InvalidOperationException("转入时发现重复或并发冲突，请刷新预检。", ex); }
        if (tx is not null) await tx.CommitAsync(ct);
        return new { leadId = lead.Id.ToString(), candidateId = candidate.Id.ToString(), candidate.Version };
    }
}
