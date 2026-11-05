using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>Company-scoped record of a claimed provider authorization; never stores credentials.</summary>
public sealed class DataSourceAuthorization : ICompanyScoped
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public string SourceCode { get; set; } = "";
    public string Status { get; set; } = "draft";
    public string AuthorizationReference { get; set; } = "";
    public string PermittedFields { get; set; } = "";
    public int? DailyQuota { get; set; }
    public int? MonthlyQuota { get; set; }
    public string CostNote { get; set; } = "";
    public int? RetentionDays { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public long UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
}

/// <summary>Append-only complete revision snapshot for review and rollback planning.</summary>
public sealed class DataSourceAuthorizationRevision : ICompanyScoped
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long AuthorizationId { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = "draft";
    public string AuthorizationReference { get; set; } = "";
    public string PermittedFields { get; set; } = "";
    public int? DailyQuota { get; set; }
    public int? MonthlyQuota { get; set; }
    public string CostNote { get; set; } = "";
    public int? RetentionDays { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public long ActorId { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
