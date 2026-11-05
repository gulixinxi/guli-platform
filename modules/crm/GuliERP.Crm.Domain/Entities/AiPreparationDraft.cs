using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>One immutable generated version; only the review decision may change.</summary>
[Table("ai_preparation_drafts", Schema = "crm")]
public sealed class AiPreparationDraft : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long CandidateId { get; set; }
    public Guid RequestKey { get; set; }
    public int Version { get; set; }
    [Required, MaxLength(30)] public string TaskType { get; set; } = "";
    [Required, MaxLength(40)] public string ProviderCode { get; set; } = "simulated-v1";
    [Required, MaxLength(40)] public string ContractVersion { get; set; } = "prep-v1";
    [Required, MaxLength(20)] public string Status { get; set; } = "draft";
    [Required] public string InputEvidenceJson { get; set; } = "[]";
    [Required] public string ClaimsJson { get; set; } = "[]";
    public int EstimatedCostCents { get; set; }
    public int MaxCostCents { get; set; }
    public int ActualCostCents { get; set; }
    [MaxLength(1000)] public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long CreatedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public long? ReviewedBy { get; set; }
    [MaxLength(1000)] public string? ReviewReason { get; set; }
}
