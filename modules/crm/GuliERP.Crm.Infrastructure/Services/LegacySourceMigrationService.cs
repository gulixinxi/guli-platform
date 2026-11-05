using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Foundation.Kernel;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record LegacySourceMigrationStatus(
    string SourceCode, string ExistingMode, bool ExistingConfigured,
    int MaximumPhysicalAttemptsPerAction, string ExistingAccounting,
    string RegistrationStatus, bool ConfigurationPrecheckPassed,
    bool CanSwitchToUnifiedMetering, IReadOnlyList<string> Blockers);

public sealed record SourcePolicyPreviewInput(string SourceCode, string TargetKind,
    IReadOnlyList<string> RequestedFields);
public sealed record SourcePolicyPreviewResult(string SourceCode, string Decision,
    int DailyUsed, int? DailyQuota, int MonthlyUsed, int? MonthlyQuota,
    bool PerformsExternalRequest);

/// <summary>
/// Read-only migration assessment for the two existing live paths. It never changes their
/// execution mode, makes a vendor request, or claims to verify supplier agreements.
/// </summary>
public sealed class LegacySourceMigrationService(CrmDbContext db, ICurrentTenant tenant,
    ICurrentCompany company, QuakeRuntimeCredentialStore quake, ChinazConnectorSettings chinaz)
{
    public async Task<IReadOnlyList<LegacySourceMigrationStatus>> ListAsync(CancellationToken ct)
    {
        var (tenantId, companyId) = RequireScope();
        var records = await db.DataSourceAuthorizations.AsNoTracking()
            .Where(x => x.SourceCode == "quake" || x.SourceCode == "chinaz").ToListAsync(ct);
        var quakeKeys = quake.GetCandidates(tenantId, companyId).Count;
        var chinazSetting = chinaz.GetStatus(tenantId, companyId);
        return [
            Build("quake", quakeKeys > 0, quakeKeys,
                "搜索入口有逻辑查询历史；资产抓取入口和多 Key 回退没有逐次请求计数。",
                records.SingleOrDefault(x => x.SourceCode == "quake"),
                ["需核对每个 Key 的账户额度、回退请求是否分别计费。",
                 "需让搜索和资产抓取两个入口逐次计数，并确定历史用量起算日。"]),
            Build("chinaz", chinazSetting.Enabled, 10,
                "批量上限 10、域名间隔至少 3 秒；没有持久化的逐次请求计数。",
                records.SingleOrDefault(x => x.SourceCode == "chinaz"),
                ["需核对自动访问许可及额度；当前开关按公司隔离，但未接入统一额度控制。",
                 "需确定批内每个域名的计费规则和历史用量起算日。"])
        ];
    }

    public async Task<SourcePolicyPreviewResult> PreviewAsync(SourcePolicyPreviewInput input, CancellationToken ct)
    {
        RequireScope();
        ArgumentNullException.ThrowIfNull(input);
        if (input.SourceCode is not ("quake" or "chinaz"))
            throw new ArgumentException("仅支持现有 Quake 和站长之家迁移试算。");
        if (input.TargetKind != (input.SourceCode == "quake" ? "query" : "domain") ||
            input.RequestedFields is null || input.RequestedFields.Count is < 1 or > 20 ||
            input.RequestedFields.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64) ||
            input.RequestedFields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != input.RequestedFields.Count)
            throw new ArgumentException("目标类型或请求字段无效。");

        var row = await db.DataSourceAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SourceCode == input.SourceCode, ct);
        var now = DateTime.UtcNow;
        var decision = row is null || row.Status != "reviewed" ? "registration_missing"
            : row.ValidFrom > now ? "not_yet_valid"
            : row.ValidUntil <= now ? "expired"
            : "candidate";
        if (decision == "candidate")
        {
            var allowed = row!.PermittedFields.Split([',', '，', ';', '；', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!input.RequestedFields.All(allowed.Contains)) decision = "field_denied";
        }
        var dailyUsed = 0;
        var monthlyUsed = 0;
        if (row is not null)
        {
            var dayStart = now.Date;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            dailyUsed = await db.DataSourceExecutionAttempts.CountAsync(x =>
                x.AuthorizationId == row.Id && x.ReservedAt >= dayStart, ct);
            monthlyUsed = await db.DataSourceExecutionAttempts.CountAsync(x =>
                x.AuthorizationId == row.Id && x.ReservedAt >= monthStart, ct);
            if (decision == "candidate" &&
                (row.DailyQuota is int daily && dailyUsed >= daily ||
                 row.MonthlyQuota is int monthly && monthlyUsed >= monthly))
                decision = "quota_exhausted";
        }
        if (decision == "candidate" &&
            (input.SourceCode == "quake" ? quake.GetCandidates(tenant.Id, company.Id).Count == 0
                : !chinaz.GetStatus(tenant.Id!.Value, company.Id!.Value).Enabled))
            decision = "connection_not_configured";
        // A policy match is still not a verified supplier permission or a live-mode switch.
        if (decision == "candidate") decision = "policy_match_live_blocked";
        return new(input.SourceCode, decision, dailyUsed, row?.DailyQuota,
            monthlyUsed, row?.MonthlyQuota, false);
    }

    private static LegacySourceMigrationStatus Build(string code, bool configured, int attempts,
        string accounting, DataSourceAuthorization? record, string[] sourceBlockers)
    {
        var now = DateTime.UtcNow;
        var registrationStatus = record is null ? "missing" : record.Status != "reviewed" ? record.Status
            : record.ValidFrom > now ? "not_yet_valid" : record.ValidUntil <= now ? "expired" : "reviewed";
        var precheck = configured && registrationStatus == "reviewed" &&
            (record!.DailyQuota is > 0 || record.MonthlyQuota is > 0) &&
            !string.IsNullOrWhiteSpace(record.PermittedFields);
        var blockers = new List<string>();
        if (!configured) blockers.Add("现有连接未配置或开关关闭。");
        if (registrationStatus != "reviewed") blockers.Add("当前公司缺少有效的内部授权登记。");
        blockers.Add("供应商许可范围和真实额度尚未由系统核实。");
        blockers.AddRange(sourceBlockers);
        return new(code, "legacy_unmetered", configured, attempts, accounting,
            registrationStatus, precheck, false, blockers);
    }

    private (long TenantId, long CompanyId) RequireScope()
    {
        if (tenant.Id is not long tenantId || tenantId <= 0 ||
            company.Id is not long companyId || companyId <= 0)
            throw new InvalidOperationException("缺少租户或公司范围。");
        return (tenantId, companyId);
    }
}
