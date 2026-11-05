using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>A proposed relationship between one observed asset and one legal/operator name.</summary>
[Table("identity_candidates", Schema = "crm")]
public sealed class IdentityCandidate : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long AssetId { get; set; }
    [Required, MaxLength(253)] public string Domain { get; set; } = "";
    [Required, MaxLength(300)] public string ProposedName { get; set; } = "";
    [Required, MaxLength(20)] public string Status { get; set; } = "pending";
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long CreatedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public long? ReviewedBy { get; set; }
    [MaxLength(1000)] public string? ReviewReason { get; set; }
}

/// <summary>Append-only identity evidence; the application exposes no update/delete route.</summary>
[Table("identity_evidence", Schema = "crm")]
public sealed class IdentityEvidence : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long CandidateId { get; set; }
    [Required, MaxLength(30)] public string SourceType { get; set; } = "";
    [Required, MaxLength(1000)] public string SourceReference { get; set; } = "";
    [Required, MaxLength(2000)] public string ClaimText { get; set; } = "";
    public DateTime ObservedAt { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public long RecordedBy { get; set; }
}

/// <summary>Append-only decision history, including reversals of a previous review.</summary>
[Table("identity_review_events", Schema = "crm")]
public sealed class IdentityReviewEvent : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long CandidateId { get; set; }
    [Required, MaxLength(20)] public string Action { get; set; } = "";
    [Required, MaxLength(1000)] public string Reason { get; set; } = "";
    public int Version { get; set; }
    public long ActorId { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
