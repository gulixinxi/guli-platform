using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Foundation.Kernel;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record DataSourceAuthorizationInput(
    int ExpectedVersion, string Status, string? AuthorizationReference, string? PermittedFields,
    int? DailyQuota, int? MonthlyQuota, string? CostNote, int? RetentionDays,
    DateTime? ValidFrom, DateTime? ValidUntil);

public sealed class DataSourceAuthorizationService(CrmDbContext db, ICurrentTenant tenant, ICurrentCompany company)
{
    private static readonly HashSet<string> SourceCodes =
        ["quake", "chinaz", "icp_manual", "registry", "baidu_ads", "recruitment"];

    public static bool IsKnownSource(string code) => SourceCodes.Contains(code);

    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct)
    {
        RequireScope();
        var rows = await db.DataSourceAuthorizations.AsNoTracking().OrderBy(x => x.SourceCode).ToListAsync(ct);
        return rows.Select(x => (object)Project(x)).ToList();
    }

    public async Task<IReadOnlyList<DataSourceAuthorizationRevision>> HistoryAsync(string sourceCode, CancellationToken ct)
    {
        RequireScope();
        if (!IsKnownSource(sourceCode)) throw new ArgumentException("未知数据源。");
        var row = await db.DataSourceAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SourceCode == sourceCode, ct);
        if (row is null) return [];
        return await db.DataSourceAuthorizationRevisions.AsNoTracking()
            .Where(x => x.AuthorizationId == row.Id)
            .OrderByDescending(x => x.Version).ToListAsync(ct);
    }

    public async Task<object> SaveAsync(string sourceCode, DataSourceAuthorizationInput input,
        long actorId, CancellationToken ct)
    {
        var (tenantId, companyId) = RequireScope();
        if (!IsKnownSource(sourceCode)) throw new ArgumentException("未知数据源。");
        if (actorId <= 0) throw new ArgumentException("操作人无效。");
        if (input.ExpectedVersion < 0) throw new ArgumentException("版本号无效。");
        if (input.Status is not ("draft" or "reviewed" or "disabled"))
            throw new ArgumentException("状态无效。");
        var reference = input.AuthorizationReference?.Trim() ?? "";
        var fields = input.PermittedFields?.Trim() ?? "";
        var cost = input.CostNote?.Trim() ?? "";
        if (reference.Length > 500 || fields.Length > 1000 || cost.Length > 500)
            throw new ArgumentException("登记文字过长。");
        if (input.DailyQuota is < 0 or > 100000000 || input.MonthlyQuota is < 0 or > 100000000
            || input.RetentionDays is < 0 or > 36500)
            throw new ArgumentException("额度或保留期超出范围。");
        var from = input.ValidFrom?.ToUniversalTime();
        var until = input.ValidUntil?.ToUniversalTime();
        if (from is not null && until is not null && until <= from)
            throw new ArgumentException("到期时间必须晚于生效时间。");
        if (input.Status == "reviewed" && (reference.Length == 0 || fields.Length == 0
            || cost.Length == 0 || input.RetentionDays is null || from is null || until is null
            || (input.DailyQuota is null && input.MonthlyQuota is null)))
            throw new ArgumentException("标记已审核前，须登记依据、允许字段、额度、费用、保留期和有效期。");
        if (input.Status == "reviewed" && until <= DateTime.UtcNow)
            throw new ArgumentException("已过期的资料不能标记已审核。");

        var row = await db.DataSourceAuthorizations.SingleOrDefaultAsync(x => x.SourceCode == sourceCode, ct);
        if (row is null)
        {
            if (input.ExpectedVersion != 0) throw new InvalidOperationException("资料已由他人修改，请刷新后重试。");
            row = new DataSourceAuthorization { TenantId = tenantId, CompanyId = companyId, SourceCode = sourceCode };
            db.DataSourceAuthorizations.Add(row);
        }
        else if (row.Version != input.ExpectedVersion)
            throw new InvalidOperationException("资料已由他人修改，请刷新后重试。");

        row.Status = input.Status;
        row.AuthorizationReference = reference;
        row.PermittedFields = fields;
        row.DailyQuota = input.DailyQuota;
        row.MonthlyQuota = input.MonthlyQuota;
        row.CostNote = cost;
        row.RetentionDays = input.RetentionDays;
        row.ValidFrom = from;
        row.ValidUntil = until;
        row.UpdatedBy = actorId;
        row.UpdatedAt = DateTime.UtcNow;
        row.Version++;
        // Principal key is database-generated; save principal first in the same transaction.
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct) : null;
        await db.SaveChangesAsync(ct);
        db.DataSourceAuthorizationRevisions.Add(new DataSourceAuthorizationRevision
        {
            TenantId = tenantId, CompanyId = companyId, AuthorizationId = row.Id,
            Version = row.Version, Status = row.Status,
            AuthorizationReference = row.AuthorizationReference, PermittedFields = row.PermittedFields,
            DailyQuota = row.DailyQuota, MonthlyQuota = row.MonthlyQuota, CostNote = row.CostNote,
            RetentionDays = row.RetentionDays, ValidFrom = row.ValidFrom, ValidUntil = row.ValidUntil,
            ActorId = actorId, OccurredAt = row.UpdatedAt
        });
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return Project(row);
    }

    private (long TenantId, long CompanyId) RequireScope()
    {
        if (tenant.Id is not long tenantId || tenantId <= 0 ||
            company.Id is not long companyId || companyId <= 0)
            throw new InvalidOperationException("缺少租户或公司范围。");
        return (tenantId, companyId);
    }

    private static object Project(DataSourceAuthorization x) => new
    {
        x.SourceCode, x.Status,
        EffectiveStatus = x.Status == "reviewed" && x.ValidUntil <= DateTime.UtcNow ? "expired"
            : x.Status == "reviewed" && x.ValidFrom > DateTime.UtcNow ? "not_yet_valid" : x.Status,
        x.AuthorizationReference, x.PermittedFields, x.DailyQuota, x.MonthlyQuota,
        x.CostNote, x.RetentionDays, x.ValidFrom, x.ValidUntil,
        x.UpdatedBy, x.UpdatedAt, x.Version,
        // Registration is never a runtime permission to call a provider.
        EnablesAutomaticRequests = false
    };
}
