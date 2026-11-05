using System.ComponentModel.DataAnnotations;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>Append-only audit trail; contains no contact value.</summary>
public sealed class ContactCandidateEvent : ICompanyScoped
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long CandidateId { get; set; }
    [Required, MaxLength(30)] public string Action { get; set; } = "";
    [Required, MaxLength(1000)] public string Reason { get; set; } = "";
    public long ActorId { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
