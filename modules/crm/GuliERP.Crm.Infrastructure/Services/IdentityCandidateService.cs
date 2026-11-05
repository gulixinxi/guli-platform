using System.Net;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record IdentityEvidenceInput(string SourceType, string SourceReference, string ClaimText, DateTime ObservedAt);
public sealed record CreateIdentityCandidateInput(string ProposedName, IdentityEvidenceInput Evidence);
public sealed record IdentityReviewInput(string Action, string Reason, int ExpectedVersion);

/// <summary>Creates auditable identity proposals without promoting them into CRM.</summary>
public sealed class IdentityCandidateService(CrmDbContext db)
{
    private static readonly HashSet<string> Sources = ["icp", "public_site", "registry", "third_party", "manual_note"];
    private static readonly HashSet<string> Actions = ["confirm", "reject", "reopen"];

    public async Task<object?> GetForAssetAsync(long assetId, CancellationToken ct)
    {
        if (!await db.QuakeAssetRecords.AsNoTracking().AnyAsync(a => a.Id == assetId, ct)) return null;
        var candidates = await db.IdentityCandidates.AsNoTracking()
            .Where(c => c.AssetId == assetId).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
        var ids = candidates.Select(c => c.Id).ToArray();
        var evidence = await db.IdentityEvidence.AsNoTracking()
            .Where(e => ids.Contains(e.CandidateId)).OrderBy(e => e.RecordedAt).ToListAsync(ct);
        var events = await db.IdentityReviewEvents.AsNoTracking()
            .Where(e => ids.Contains(e.CandidateId)).OrderBy(e => e.Version).ToListAsync(ct);
        return new { items = candidates.Select(c => Project(c, evidence.Where(e => e.CandidateId == c.Id), events.Where(e => e.CandidateId == c.Id))).ToList() };
    }

    public async Task<object> CreateAsync(long assetId, CreateIdentityCandidateInput input, long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("审核人员身份无效。");
        var asset = await db.QuakeAssetRecords.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该资产。");
        var domain = (asset.Domain ?? asset.Hostname ?? "").Trim().ToLowerInvariant();
        if (!IsPublicDomain(domain)) throw new ArgumentException("只有具备公开域名的资产可建立企业身份候选；IP 不能证明企业归属。");
        var name = input.ProposedName?.Trim() ?? "";
        if (name.Length is < 2 or > 300) throw new ArgumentException("候选主体名称长度须为 2–300 字。");
        var observedAt = ValidateEvidence(input.Evidence);
        var candidate = new IdentityCandidate
        {
            Id = NewId(), AssetId = assetId, Domain = domain, ProposedName = name,
            CreatedBy = actorId, CreatedAt = DateTime.UtcNow
        };
        var evidence = NewEvidence(candidate.Id, input.Evidence, observedAt, actorId);
        db.IdentityCandidates.Add(candidate);
        db.IdentityEvidence.Add(evidence);
        await db.SaveChangesAsync(ct);
        return Project(candidate, [evidence], []);
    }

    public async Task<object> AddEvidenceAsync(long candidateId, IdentityEvidenceInput input, long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("操作人员身份无效。");
        var candidate = await db.IdentityCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该身份候选。");
        if (candidate.Status != "pending")
            throw new InvalidOperationException("已审核候选须先撤销审核，再追加证据。");
        var observedAt = ValidateEvidence(input);
        var evidence = NewEvidence(candidate.Id, input, observedAt, actorId);
        db.IdentityEvidence.Add(evidence);
        await db.SaveChangesAsync(ct);
        return ProjectEvidence(evidence);
    }

    public async Task<object> ReviewAsync(long candidateId, IdentityReviewInput input, long reviewerId, CancellationToken ct)
    {
        if (reviewerId <= 0) throw new ArgumentException("审核人员身份无效。");
        var action = input.Action?.Trim().ToLowerInvariant() ?? "";
        if (!Actions.Contains(action)) throw new ArgumentException("无效的审核动作。");
        var reason = input.Reason?.Trim() ?? "";
        if (reason.Length is < 10 or > 1000) throw new ArgumentException("审核理由须为 10–1000 字。");
        var candidate = await db.IdentityCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该身份候选。");
        if (candidate.Version != input.ExpectedVersion)
            throw new DbUpdateConcurrencyException("身份候选已由他人更新，请刷新后重试。");
        if (action == "reopen" && candidate.Status == "pending")
            throw new InvalidOperationException("待审候选无需撤销审核。");
        if (action != "reopen" && candidate.Status != "pending")
            throw new InvalidOperationException("已审核候选须先撤销审核，再重新决定。");
        if (action == "confirm")
        {
            var external = await db.IdentityEvidence.AsNoTracking()
                .Where(e => e.CandidateId == candidateId && e.SourceType != "manual_note" && e.SourceType != "third_party")
                .Select(e => e.ObservedAt).ToListAsync(ct);
            if (external.Count == 0)
                throw new InvalidOperationException("确认身份至少需要一条非第三方的外部来源证据；第三方页面和人工备注不能单独作为依据。");
            // A dated observation is evidence of what was seen then, not proof of current ownership.
            if (external.All(at => at < DateTime.UtcNow.AddDays(-365)))
                throw new InvalidOperationException("外部证据均已超过 365 天，请补充近期来源后再确认身份。");
        }

        candidate.Status = action switch { "confirm" => "confirmed", "reject" => "rejected", _ => "pending" };
        candidate.Version++;
        candidate.ReviewedAt = DateTime.UtcNow;
        candidate.ReviewedBy = reviewerId;
        candidate.ReviewReason = reason;
        var audit = new IdentityReviewEvent
        {
            Id = NewId(), CandidateId = candidateId, Action = action, Reason = reason,
            Version = candidate.Version, ActorId = reviewerId, OccurredAt = candidate.ReviewedAt.Value
        };
        db.IdentityReviewEvents.Add(audit);
        await db.SaveChangesAsync(ct);
        return Project(candidate, [], [audit]);
    }

    private static DateTime ValidateEvidence(IdentityEvidenceInput input)
    {
        if (input is null || !Sources.Contains(input.SourceType ?? ""))
            throw new ArgumentException("证据来源类型须为 icp、public_site、registry、third_party 或 manual_note。");
        if (string.IsNullOrWhiteSpace(input.SourceReference) || input.SourceReference.Length > 1000
            || string.IsNullOrWhiteSpace(input.ClaimText) || input.ClaimText.Length > 2000)
            throw new ArgumentException("证据来源和原文必须填写且不能超长。");
        var at = input.ObservedAt.Kind == DateTimeKind.Utc ? input.ObservedAt : input.ObservedAt.ToUniversalTime();
        if (at < DateTime.UtcNow.AddYears(-10) || at > DateTime.UtcNow.AddDays(1))
            throw new ArgumentException("证据采集时间不在允许范围内。");
        return at;
    }

    private static bool IsPublicDomain(string value) =>
        value.Contains('.') && value.Length <= 253 && Uri.CheckHostName(value) == UriHostNameType.Dns
        && !IPAddress.TryParse(value, out _)
        && !value.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && !value.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);

    private static IdentityEvidence NewEvidence(long candidateId, IdentityEvidenceInput input, DateTime at, long actorId) => new()
    {
        Id = NewId(), CandidateId = candidateId, SourceType = input.SourceType,
        SourceReference = input.SourceReference.Trim(), ClaimText = input.ClaimText.Trim(),
        ObservedAt = at, RecordedAt = DateTime.UtcNow, RecordedBy = actorId
    };

    private static object Project(IdentityCandidate c, IEnumerable<IdentityEvidence> evidence, IEnumerable<IdentityReviewEvent> events) => new
    {
        id = c.Id.ToString(), assetId = c.AssetId.ToString(), c.Domain, c.ProposedName, c.Status,
        c.Version, c.CreatedAt, createdBy = c.CreatedBy.ToString(), c.ReviewedAt,
        reviewedBy = c.ReviewedBy?.ToString(), c.ReviewReason,
        evidence = evidence.Select(ProjectEvidence).ToList(),
        reviewEvents = events.Select(e => new { id = e.Id.ToString(), e.Action, e.Reason, e.Version, actorId = e.ActorId.ToString(), e.OccurredAt }).ToList()
    };

    private static object ProjectEvidence(IdentityEvidence e) => new
    {
        id = e.Id.ToString(), e.SourceType, e.SourceReference, e.ClaimText,
        e.ObservedAt, e.RecordedAt, recordedBy = e.RecordedBy.ToString()
    };

    private static long NewId() => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20);
}
