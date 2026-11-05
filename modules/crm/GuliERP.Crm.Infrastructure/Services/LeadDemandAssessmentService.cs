using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed class LeadDemandAssessmentService(CrmDbContext db)
{
    private static readonly IReadOnlyList<string> Categories = AssessmentCategoryMap.Ordered;
    private static readonly HashSet<string> States = ["unknown", "pending", "potential", "confirmed", "not_applicable"];
    private static readonly HashSet<string> ConfidenceLevels = ["low", "medium", "high"];

    /// <summary>需要证据支撑的状态。</summary>
    private static readonly HashSet<string> StatesRequiringEvidence = ["potential", "confirmed"];

    public async Task<object?> GetAsync(long leadId, CancellationToken ct)
    {
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return null;

        var records = await db.LeadDemandAssessments.AsNoTracking()
            .Where(a => a.LeadId == leadId).ToListAsync(ct);

        // One round-trip for every direction of this lead. Fetching per record
        // would be N queries; the set is always ≤ 4 rows so a single load is
        // both cheaper and easier to keep consistent.
        var assessments = records.Select(r => r.Id).ToList();
        var evidence = assessments.Count == 0
            ? []
            : await db.LeadAssessmentEvidence.AsNoTracking()
                .Where(e => assessments.Contains(e.AssessmentId))
                .OrderBy(e => e.CollectedAt).ThenBy(e => e.Id)
                .ToListAsync(ct);

        return new
        {
            leadId,
            lead.SourceReverseLookupRecordId,
            items = Categories.Select(category =>
            {
                var record = records.FirstOrDefault(a => a.Category == AssessmentCategoryMap.ToBusinessCategory(category));
                var items = record is null
                    ? Enumerable.Empty<LeadAssessmentEvidence>()
                    : evidence.Where(e => e.AssessmentId == record.Id);
                return new
                {
                    id = record?.Id.ToString(),
                    category,
                    state = record?.State ?? "unknown",
                    confidence = record?.Confidence,
                    // Deprecated mirror of the legacy free-text column. Kept in
                    // the response so an old client does not crash on a missing
                    // field; new clients must read evidenceItems.
                    evidence = record?.Evidence,
                    evidenceItems = items.Select(Project).ToList(),
                    reason = record?.Reason,
                    nextAction = record?.NextAction,
                    reviewerId = record?.ReviewerId,
                    reviewedAt = record?.ReviewedAt,
                    version = record?.Version ?? 0
                };
            }).ToList()
        };
    }

    private static object Project(LeadAssessmentEvidence e) => new
    {
        id = e.Id.ToString(),
        type = e.EvidenceType,
        url = e.Url,
        collectedAt = e.CollectedAt,
        // null is meaningful here: "never verified" must not be rendered the
        // same way as an explicit verdict.
        verificationStatus = e.VerificationStatus,
        note = e.Note,
        version = e.Version
    };

    public async Task<object?> GetHistoryAsync(long leadId, string category, CancellationToken ct)
    {
        if (!Categories.Contains(category)) throw new ArgumentException("无效的评估方向。");
        var businessCategory = AssessmentCategoryMap.ToBusinessCategory(category);
        if (!await db.Leads.AnyAsync(l => l.Id == leadId, ct)) return null;

        return await db.LeadDemandAssessmentRevisions.AsNoTracking()
            .Where(r => r.LeadId == leadId && r.Category == businessCategory)
            .OrderByDescending(r => r.Version)
            .Select(r => new { r.Version, r.State, r.Confidence, r.Evidence, r.Reason,
                r.NextAction, r.ReviewerId, r.ReviewedAt })
            .ToListAsync(ct);
    }

    public async Task<object?> SaveAsync(long leadId, string category, LeadDemandAssessmentInput input,
        long reviewerId, CancellationToken ct)
    {
        if (!Categories.Contains(category)) throw new ArgumentException("无效的评估方向。");
        var businessCategory = AssessmentCategoryMap.ToBusinessCategory(category);
        if (!States.Contains(input.State)) throw new ArgumentException("无效的评估状态。");
        if (input.Confidence is not null && !ConfidenceLevels.Contains(input.Confidence))
            throw new ArgumentException("无效的置信度。");
        if (input.Reason?.Length > 1000 || input.NextAction?.Length > 500)
            throw new ArgumentException("评估说明超过长度限制。");

        var items = ValidateEvidence(input.EvidenceItems);

        // "可能 / 已确认" must rest on evidence. This used to be
        // `input.Evidence` (one free-text blob); it is now at least one
        // STRUCTURED item, so the claim carries a type, a source and a
        // verification state instead of an untraceable sentence.
        if (StatesRequiringEvidence.Contains(input.State) && items.Length == 0)
            throw new ArgumentException("可能或已确认需求必须至少填写一条结构化证据。");

        if (!await db.Leads.AnyAsync(l => l.Id == leadId, ct)) return null;

        // A new assessment needs its database-generated Id before its evidence
        // rows can be inserted. Keep both SaveChanges calls in one PostgreSQL
        // transaction so a failure cannot publish a newer assessment version
        // with stale or missing current evidence. The in-memory test provider
        // does not support transactions; production uses a relational provider.
        await using var transaction = db.Database.IsRelational()
            && db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(ct)
                : null;

        var record = await db.LeadDemandAssessments.FirstOrDefaultAsync(
            a => a.LeadId == leadId && a.Category == businessCategory, ct);
        if (record is null)
        {
            record = new LeadDemandAssessment { LeadId = leadId, Category = businessCategory };
            db.LeadDemandAssessments.Add(record);
        }
        else record.Version++;

        record.State = input.State;
        record.Confidence = input.Confidence;
        // The legacy column stops being written from here on. Existing rows
        // keep their text so the pre-migration state stays inspectable; new
        // truth lives in lead_assessment_evidence.
        if (record.Id != 0) record.Evidence = null;
        record.Reason = input.Reason?.Trim();
        record.NextAction = input.NextAction?.Trim();
        record.ReviewerId = reviewerId;
        record.ReviewedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);   // flush to obtain record.Id

        // Only the current evidence set is retained. Replacing it is the
        // accepted B1 behavior; historical assessment metadata remains in the
        // revision table, but old evidence rows are not an audit archive.
        var stale = await db.LeadAssessmentEvidence
            .Where(e => e.AssessmentId == record.Id)
            .ToListAsync(ct);
        if (stale.Count > 0) db.LeadAssessmentEvidence.RemoveRange(stale);

        foreach (var item in items)
        {
            db.LeadAssessmentEvidence.Add(new LeadAssessmentEvidence
            {
                LeadId = leadId,
                AssessmentId = record.Id,
                Category = businessCategory,
                Version = record.Version,
                EvidenceType = item.EvidenceType,
                Url = item.Url,
                CollectedAt = item.CollectedAt,
                VerificationStatus = item.VerificationStatus,
                Note = item.Note
            });
        }

        db.LeadDemandAssessmentRevisions.Add(new LeadDemandAssessmentRevision
        {
            LeadId = leadId,
            Category = businessCategory,
            State = record.State,
            Confidence = record.Confidence,
            Evidence = record.Evidence,
            Reason = record.Reason,
            NextAction = record.NextAction,
            ReviewerId = reviewerId,
            ReviewedAt = record.ReviewedAt,
            Version = record.Version
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await GetAsync(leadId, ct);
    }

    private static EvidenceItemInput[] ValidateEvidence(EvidenceItemInput[]? items)
    {
        if (items is null || items.Length == 0) return [];

        foreach (var item in items)
        {
            if (!LeadEvidenceTypes.All.Contains(item.EvidenceType))
                throw new ArgumentException(
                    $"无效的证据类型「{item.EvidenceType}」，允许值：{string.Join(" / ", LeadEvidenceTypes.All)}。");
            if (!LeadEvidenceVerifications.All.Contains(item.VerificationStatus))
                throw new ArgumentException(
                    $"无效的核验状态「{item.VerificationStatus}」，允许值：{string.Join(" / ", LeadEvidenceVerifications.All)}。");
            if (item.Url?.Length > 1000) throw new ArgumentException("证据链接超过长度限制。");
            if (item.Note?.Length > 2000) throw new ArgumentException("证据说明超过长度限制。");
            // A URL is the only thing that makes the evidence re-checkable
            // later. Text-only evidence is allowed (a phone call is evidence),
            // but then the note must say what it is — an entirely blank item
            // carries no information at all.
            if (string.IsNullOrWhiteSpace(item.Url) && string.IsNullOrWhiteSpace(item.Note))
                throw new ArgumentException("证据链接和说明至少填写一项。");
        }
        return items;
    }
}
