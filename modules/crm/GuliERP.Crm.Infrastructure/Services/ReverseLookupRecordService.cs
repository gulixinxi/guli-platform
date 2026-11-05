using System.Text.Json;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed class ReverseLookupRecordService(CrmDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveBatchAsync(IReadOnlyList<ReverseLookupResult> results, CancellationToken ct)
    {
        var batchId = Guid.NewGuid();
        var queriedAt = DateTime.UtcNow;
        var saved = new List<ReverseLookupRecord>(results.Count);
        foreach (var result in results)
        {
            var record = new ReverseLookupRecord
            {
                BatchId = batchId,
                CompanyName = result.CompanyName,
                InputWebsite = result.InputWebsite,
                ResultJson = JsonSerializer.Serialize(result, JsonOptions),
                QueriedAt = queriedAt
            };
            db.ReverseLookupRecords.Add(record);
            saved.Add(record);
        }
        await db.SaveChangesAsync(ct);
        for (var i = 0; i < results.Count; i++) results[i].RecordId = saved[i].Id.ToString();
    }

    public async Task<IReadOnlyList<object>> GetRecentAsync(CancellationToken ct)
    {
        var records = await db.ReverseLookupRecords.AsNoTracking()
            .OrderByDescending(r => r.QueriedAt).ThenByDescending(r => r.Id)
            .Take(100).ToListAsync(ct);
        return records.Select(r => (object)new
        {
            r.Id,
            r.BatchId,
            r.CompanyName,
            r.InputWebsite,
            r.QueriedAt,
            Result = JsonSerializer.Deserialize<ReverseLookupResult>(r.ResultJson, JsonOptions)
        }).ToList();
    }
}
