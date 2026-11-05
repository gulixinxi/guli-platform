using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>Human supplied, reviewable content observation linked to one technical report.</summary>
[Table("website_content_evidence", Schema = "crm")]
public sealed class WebsiteContentEvidence : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long CandidateId { get; set; }
    public long ReportId { get; set; }
    [Required, MaxLength(40)] public string ScenarioVersion { get; set; } = "public-content-v1";
    [Required, MaxLength(40)] public string MethodVersion { get; set; } = "manual-evidence-v1";
    [Required, MaxLength(500)] public string SourceUrl { get; set; } = "";
    [Required, MaxLength(2000)] public string Excerpt { get; set; } = "";
    [Required, MaxLength(2000)] public string Observation { get; set; } = "";
    [Required, MaxLength(1000)] public string Limitation { get; set; } = "";
    [Required, MaxLength(20)] public string Status { get; set; } = "pending";
    public DateTime ObservedAt { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public long RecordedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public long? ReviewedBy { get; set; }
    [MaxLength(1000)] public string? ReviewReason { get; set; }
}
