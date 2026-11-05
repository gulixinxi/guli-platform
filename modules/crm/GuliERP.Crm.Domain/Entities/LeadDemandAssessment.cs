using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;
using GuliERP.Crm.Domain.Enums;

namespace GuliERP.Crm.Domain.Entities;

[Table("lead_demand_assessments", Schema = "crm")]
public sealed class LeadDemandAssessment : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long LeadId { get; set; }
    public BusinessCategory Category { get; set; }
    [Required, MaxLength(30)] public string State { get; set; } = "unknown";
    [MaxLength(20)] public string? Confidence { get; set; }
    [MaxLength(2000)] public string? Evidence { get; set; }
    [MaxLength(1000)] public string? Reason { get; set; }
    [MaxLength(500)] public string? NextAction { get; set; }
    public long ReviewerId { get; set; }
    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;
}
