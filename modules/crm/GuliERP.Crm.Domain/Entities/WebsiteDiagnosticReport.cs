using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>A versioned, immutable result of one passive website diagnostic job.</summary>
[Table("website_diagnostic_reports", Schema = "crm")]
public sealed class WebsiteDiagnosticReport : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long CandidateId { get; set; }
    public int Version { get; set; }
    [Required, MaxLength(500)] public string Url { get; set; } = "";
    [Required, MaxLength(40)] public string MethodVersion { get; set; } = "passive-web-v1";
    [Required, MaxLength(20)] public string Status { get; set; } = "queued";
    [Required, MaxLength(20)] public string Trigger { get; set; } = "manual";
    [Required] public string ChecksJson { get; set; } = "[]";
    [Required] public string AttemptLogJson { get; set; } = "[]";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public long RequestedBy { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    [MaxLength(1000)] public string? Error { get; set; }
}
