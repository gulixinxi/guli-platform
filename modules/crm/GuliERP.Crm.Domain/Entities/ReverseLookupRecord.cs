using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>A company-scoped snapshot of one reverse lookup result, including unknown and failed checks.</summary>
[Table("reverse_lookup_records", Schema = "crm")]
public sealed class ReverseLookupRecord : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public Guid BatchId { get; set; }
    [Required, MaxLength(300)] public string CompanyName { get; set; } = "";
    [MaxLength(500)] public string? InputWebsite { get; set; }
    [Required] public string ResultJson { get; set; } = "{}";
    public DateTime QueriedAt { get; set; } = DateTime.UtcNow;
}
