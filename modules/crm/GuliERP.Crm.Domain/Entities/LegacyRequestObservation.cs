using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>Non-billing observation of one physical legacy connector attempt.</summary>
public sealed class LegacyRequestObservation : ICompanyScoped
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }
    public string SourceCode { get; set; } = "";
    public string Route { get; set; } = "";
    public string Outcome { get; set; } = "";
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
}
