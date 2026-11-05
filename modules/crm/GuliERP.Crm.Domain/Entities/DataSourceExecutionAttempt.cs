using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>
/// One reserved supplier request, including retries. A reserved row consumes quota even if
/// the process stops before the result is known. Query targets and returned data are not stored.
/// </summary>
public sealed class DataSourceExecutionAttempt : ICompanyScoped
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long AuthorizationId { get; set; }
    public string SourceCode { get; set; } = "";
    public Guid RequestId { get; set; }
    public int AttemptNumber { get; set; }
    public string RequestedFields { get; set; } = "";
    public string Status { get; set; } = "reserved";
    public string? FailureCode { get; set; }
    public DateTime ReservedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
