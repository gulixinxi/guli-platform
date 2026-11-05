using System.ComponentModel.DataAnnotations;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>Unverified public contact information. Never a CRM contact until reviewed.</summary>
public sealed class ContactCandidate : ICompanyScoped
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long IdentityCandidateId { get; set; }
    [MaxLength(120)] public string? Name { get; set; }
    [MaxLength(120)] public string? Position { get; set; }
    [MaxLength(200)] public string? Email { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [Required, MaxLength(30)] public string SourceType { get; set; } = "";
    [Required, MaxLength(1000)] public string SourceReference { get; set; } = "";
    [Required] public string FieldSourcesJson { get; set; } = "{}";
    [Required, MaxLength(20)] public string Status { get; set; } = "pending";
    public DateTime ObservedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long CreatedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public long? ReviewedBy { get; set; }
    [MaxLength(1000)] public string? ReviewReason { get; set; }
    public long? PromotedContactId { get; set; }
}
