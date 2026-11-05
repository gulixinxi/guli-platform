using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public interface ICrmNumberGenerator
{
    Task<string> NextCompanyNoAsync(CancellationToken ct);
    Task<string> NextDealNoAsync(CancellationToken ct);
}

/// <summary>
/// Allocates globally unique display numbers through PostgreSQL sequences.
/// Sequence values may have gaps after a transaction rolls back; they are never reused.
/// </summary>
public sealed class CrmNumberGenerator(CrmDbContext db) : ICrmNumberGenerator
{
    public async Task<string> NextCompanyNoAsync(CancellationToken ct)
    {
        var value = await db.Database.SqlQueryRaw<long>(
            "SELECT nextval('crm.company_no_seq') AS \"Value\"").SingleAsync(ct);
        return $"CO{DateTime.Now:yyyyMMdd}{value:D4}";
    }

    public async Task<string> NextDealNoAsync(CancellationToken ct)
    {
        var value = await db.Database.SqlQueryRaw<long>(
            "SELECT nextval('crm.deal_no_seq') AS \"Value\"").SingleAsync(ct);
        return $"DE{DateTime.Now:yyyyMMdd}{value:D4}";
    }
}
