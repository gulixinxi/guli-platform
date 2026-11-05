using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>Per confirmed website opt-in for passive technical rechecks.</summary>
[Table("website_recheck_schedules", Schema = "crm")]
public sealed class WebsiteRecheckSchedule : ICompanyScoped
{
    [Key] public long CandidateId { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public bool Enabled { get; set; }
    public int IntervalDays { get; set; } = 7;
    public DateTime? NextDueAt { get; set; }
    public DateTime? LastQueuedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public long UpdatedBy { get; set; }
}
