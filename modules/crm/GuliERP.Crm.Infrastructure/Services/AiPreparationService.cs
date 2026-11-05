using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record AiPreparationRequest(Guid RequestKey, string TaskType, int MaxCostCents);
public sealed record AiPreparationReview(string Decision, string Reason);
public sealed record AiPreparationEvidence(string Type, string Id, string Text);
public sealed record AiPreparationClaim(string Text, string EvidenceType, string EvidenceId);
public sealed record AiPreparationContext(string CompanyName, string Domain, string TaskType,
    IReadOnlyList<AiPreparationEvidence> Evidence);

/// <summary>Supplier boundary: later adapters receive only whitelisted, scoped evidence.</summary>
public interface IAiPreparationAdapter
{
    string ProviderCode { get; }
    string ContractVersion { get; }
    int EstimateCostCents(AiPreparationContext context);
    Task<IReadOnlyList<AiPreparationClaim>> GenerateAsync(AiPreparationContext context, CancellationToken ct);
}

/// <summary>Runtime gate for preparation adapters. This release admits only the local simulator.</summary>
public interface IAiPreparationExecutionGate
{
    bool CanExecute(IAiPreparationAdapter adapter);
    object Status();
}

public sealed class SimulatedOnlyAiPreparationGate : IAiPreparationExecutionGate
{
    public bool CanExecute(IAiPreparationAdapter adapter) => adapter.GetType() == typeof(SimulatedAiPreparationAdapter);

    public object Status() => new
    {
        mode = "simulated_only", realProviderEnabled = false, performsExternalRequest = false,
        credentialInputEnabled = false, authorizationVerified = false, pricePolicyConfigured = false,
        retentionPolicyConfigured = false, externalAttemptAuditConfigured = false,
        circuitState = "open",
        proposedOutboundFields = new[] { "task_type", "confirmed_company_name", "domain", "evidence_id", "approved_public_excerpt" },
        blockers = new[]
        {
            "尚无可核验的模型服务商授权与数据处理协议",
            "尚未确定密钥保管方式、真实计费与预算、保留期及失败熔断阈值",
            "真实请求审计与供应商适配器尚未实施"
        }
    };
}

/// <summary>Deterministic local simulator. It never calls a model or sends data out.</summary>
public sealed class SimulatedAiPreparationAdapter : IAiPreparationAdapter
{
    public string ProviderCode => "simulated-v1";
    public string ContractVersion => "prep-v1";
    public int EstimateCostCents(AiPreparationContext context) => 1; // virtual budget unit; ActualCostCents stays zero

    public Task<IReadOnlyList<AiPreparationClaim>> GenerateAsync(AiPreparationContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var source = context.Evidence[0];
        var text = context.TaskType switch
        {
            "company_profile" => $"模拟企业画像：已确认身份候选“{context.CompanyName}”；经营内容仍需销售核实。",
            "diagnostic_summary" => $"模拟诊断摘要：{source.Text}；仅说明技术检查，不代表 AI 推荐率。",
            "icebreaker" => $"模拟破冰话题：可围绕公开页面内容“{source.Text}”提问，发送前需人工核对。",
            _ => throw new InvalidOperationException("不支持的准备包类型。")
        };
        return Task.FromResult<IReadOnlyList<AiPreparationClaim>>([new(text, source.Type, source.Id)]);
    }
}

public sealed class AiPreparationService(CrmDbContext db, IAiPreparationAdapter adapter,
    IAiPreparationExecutionGate gate)
{
    private static readonly Regex PrivateContact = new(
        @"(?i)\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b|(?<!\d)\d{7,}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] Types = ["company_profile", "diagnostic_summary", "icebreaker"];

    public async Task<object> ListAsync(long candidateId, CancellationToken ct)
    {
        await RequireCandidate(candidateId, ct);
        var query = db.AiPreparationDrafts.AsNoTracking().Where(d => d.CandidateId == candidateId);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Take(50).ToListAsync(ct);
        return new { total, items = rows.Select(Project).ToList() };
    }

    public async Task<object> CreateAsync(long candidateId, AiPreparationRequest input,
        long actorId, CancellationToken ct)
    {
        if (actorId <= 0 || input.RequestKey == Guid.Empty || input.MaxCostCents is < 0 or > 100000
            || !Types.Contains(input.TaskType))
            throw new ArgumentException("请选择有效类型、请求编号和费用上限。", nameof(input));
        var candidate = await RequireCandidate(candidateId, ct);
        var existing = await db.AiPreparationDrafts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.RequestKey == input.RequestKey, ct);
        if (existing is not null)
        {
            if (existing.CandidateId != candidateId || existing.TaskType != input.TaskType)
                throw new InvalidOperationException("请求编号已用于另一项准备任务。");
            return Project(existing);
        }

        var version = (await db.AiPreparationDrafts.Where(d => d.CandidateId == candidateId
            && d.TaskType == input.TaskType).MaxAsync(d => (int?)d.Version, ct) ?? 0) + 1;
        var evidence = candidate.Status == "confirmed"
            ? await CollectEvidence(candidate, input.TaskType, ct) : [];
        var context = new AiPreparationContext(candidate.ProposedName, candidate.Domain, input.TaskType, evidence);
        var draft = new AiPreparationDraft
        {
            Id = NewId(), CandidateId = candidateId, RequestKey = input.RequestKey,
            Version = version, TaskType = input.TaskType,
            ProviderCode = gate.CanExecute(adapter) ? adapter.ProviderCode : "blocked-provider",
            ContractVersion = gate.CanExecute(adapter) ? adapter.ContractVersion : "blocked",
            MaxCostCents = input.MaxCostCents,
            InputEvidenceJson = JsonSerializer.Serialize(evidence), CreatedBy = actorId
        };
        if (candidate.Status != "confirmed" || evidence.Count == 0)
        {
            draft.Status = "unsupported";
            draft.FailureReason = candidate.Status != "confirmed"
                ? "企业身份尚未确认。" : "没有适用于此任务的已核验、无个人联系方式的证据。";
        }
        else
        {
            if (!gate.CanExecute(adapter))
            {
                draft.Status = "failed";
                draft.FailureReason = "真实服务商通道未启用；未执行生成或扣费。";
            }
            else
            {
                draft.EstimatedCostCents = adapter.EstimateCostCents(context);
                if (draft.EstimatedCostCents < 0) throw new InvalidOperationException("供应商费用估算无效。");
                if (draft.EstimatedCostCents > input.MaxCostCents)
                {
                    draft.Status = "cost_limit";
                    draft.FailureReason = "模拟预算不足，未运行生成器，也没有实际扣费。";
                }
                else
                {
                    try
                    {
                        var claims = await adapter.GenerateAsync(context, ct);
                        if (claims.Count == 0 || claims.Count > 10 || claims.Any(c =>
                            string.IsNullOrWhiteSpace(c.Text) || c.Text.Length > 1000 || PrivateContact.IsMatch(c.Text)
                            || !evidence.Any(e => e.Type == c.EvidenceType && e.Id == c.EvidenceId)))
                            throw new InvalidOperationException("模拟结果缺少有效证据引用或含个人联系方式。");
                        draft.ClaimsJson = JsonSerializer.Serialize(claims);
                        draft.Status = "draft";
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        draft.Status = "failed";
                        draft.FailureReason = "模拟生成失败或输出不符合证据与隐私契约；可用新请求编号重试。";
                    }
                }
            }
        }
        db.AiPreparationDrafts.Add(draft);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(draft).State = EntityState.Detached;
            var duplicate = await db.AiPreparationDrafts.AsNoTracking()
                .FirstOrDefaultAsync(d => d.RequestKey == input.RequestKey, ct);
            if (duplicate is not null && duplicate.CandidateId == candidateId
                && duplicate.TaskType == input.TaskType) return Project(duplicate);
            throw new InvalidOperationException("版本或请求编号发生并发冲突，请刷新后重试。");
        }
        return Project(draft);
    }

    public async Task<object> ReviewAsync(long candidateId, long draftId, AiPreparationReview input,
        long actorId, CancellationToken ct)
    {
        if (actorId <= 0 || input.Decision is not ("approved" or "rejected")
            || string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length is < 10 or > 1000)
            throw new ArgumentException("请选择审核决定并填写 10–1000 字理由。", nameof(input));
        var draft = await db.AiPreparationDrafts.FirstOrDefaultAsync(d =>
            d.Id == draftId && d.CandidateId == candidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该准备包版本。");
        if (draft.Status != "draft") throw new InvalidOperationException("只有待审草稿可以审核。");
        var candidate = await RequireCandidate(candidateId, ct);
        if (input.Decision == "approved")
        {
            if (candidate.Status != "confirmed") throw new InvalidOperationException("企业身份已不再确认。");
            var claims = JsonSerializer.Deserialize<List<AiPreparationClaim>>(draft.ClaimsJson) ?? [];
            if (claims.Count == 0) throw new InvalidOperationException("草稿没有可核验结论。");
            foreach (var claim in claims)
                if (!await EvidenceExists(candidateId, claim, ct))
                    throw new InvalidOperationException("草稿引用的证据已失效，不能批准。请重新生成版本。");
        }
        draft.Status = input.Decision;
        draft.ReviewedBy = actorId;
        draft.ReviewedAt = DateTime.UtcNow;
        draft.ReviewReason = input.Reason.Trim();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new InvalidOperationException("草稿已由其他人员审核，请刷新后查看。"); }
        return Project(draft);
    }

    private async Task<List<AiPreparationEvidence>> CollectEvidence(IdentityCandidate candidate,
        string taskType, CancellationToken ct)
    {
        if (taskType == "company_profile")
        {
            var row = await db.IdentityEvidence.AsNoTracking()
                .Where(e => e.CandidateId == candidate.Id && (e.SourceType == "icp"
                    || e.SourceType == "registry" || e.SourceType == "public_site"))
                .OrderByDescending(e => e.RecordedAt).FirstOrDefaultAsync(ct);
            return row is null || PrivateContact.IsMatch(candidate.ProposedName)
                ? [] : [new("identity_evidence", row.Id.ToString(CultureInfo.InvariantCulture), candidate.ProposedName)];
        }
        if (taskType == "diagnostic_summary")
        {
            var report = await db.WebsiteDiagnosticReports.AsNoTracking()
                .Where(r => r.CandidateId == candidate.Id && r.Status == "completed")
                .OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
            return report is null ? [] : [new("website_report", report.Id.ToString(CultureInfo.InvariantCulture),
                $"网站技术报告版本 {report.Version} 已完成")];
        }
        var content = await db.WebsiteContentEvidence.AsNoTracking()
            .Where(e => e.CandidateId == candidate.Id && e.Status == "accepted")
            .OrderByDescending(e => e.ReviewedAt).Take(10).ToListAsync(ct);
        return content.Where(e => !PrivateContact.IsMatch(e.Excerpt))
            .Take(1).Select(e => new AiPreparationEvidence("website_content",
                e.Id.ToString(CultureInfo.InvariantCulture), e.Excerpt.Length > 120 ? e.Excerpt[..120] : e.Excerpt)).ToList();
    }

    private async Task<bool> EvidenceExists(long candidateId, AiPreparationClaim claim, CancellationToken ct)
    {
        if (!long.TryParse(claim.EvidenceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
        return claim.EvidenceType switch
        {
            "identity_evidence" => await db.IdentityEvidence.AsNoTracking().AnyAsync(e => e.Id == id && e.CandidateId == candidateId, ct),
            "website_report" => await db.WebsiteDiagnosticReports.AsNoTracking().AnyAsync(r => r.Id == id && r.CandidateId == candidateId && r.Status == "completed", ct),
            "website_content" => await db.WebsiteContentEvidence.AsNoTracking().AnyAsync(e => e.Id == id && e.CandidateId == candidateId && e.Status == "accepted", ct),
            _ => false
        };
    }

    private async Task<IdentityCandidate> RequireCandidate(long id, CancellationToken ct) =>
        await db.IdentityCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
        ?? throw new KeyNotFoundException("当前公司找不到该企业身份候选。");

    private static object Project(AiPreparationDraft d) => new
    {
        id = d.Id.ToString(CultureInfo.InvariantCulture), candidateId = d.CandidateId.ToString(CultureInfo.InvariantCulture),
        requestKey = d.RequestKey.ToString(), d.Version, d.TaskType, d.ProviderCode, d.ContractVersion,
        d.Status, evidence = JsonSerializer.Deserialize<List<AiPreparationEvidence>>(d.InputEvidenceJson) ?? [],
        claims = JsonSerializer.Deserialize<List<AiPreparationClaim>>(d.ClaimsJson) ?? [],
        d.EstimatedCostCents, d.MaxCostCents, d.ActualCostCents, d.FailureReason,
        d.CreatedAt, createdBy = d.CreatedBy.ToString(CultureInfo.InvariantCulture),
        d.ReviewedAt, reviewedBy = d.ReviewedBy?.ToString(CultureInfo.InvariantCulture), d.ReviewReason
    };

    private static long NewId() => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20);
}
