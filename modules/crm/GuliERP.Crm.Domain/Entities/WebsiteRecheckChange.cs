using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>Append-only record of an operator changing a website's scheduled recheck setting.</summary>
[Table("website_recheck_changes", Schema = "crm")]
public sealed class WebsiteRecheckChange : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public long CandidateId { get; set; }
    public bool Enabled { get; set; }
    public int IntervalDays { get; set; } = 7;
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public long ChangedBy { get; set; }
}
