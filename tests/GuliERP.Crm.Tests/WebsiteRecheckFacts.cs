using System.Text.Json;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class WebsiteRecheckFacts
{
    private static CrmDbContext Db() => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    [Fact]
    public async Task UsesExistingCrmStageAndOnlySelectedConfirmedSitesAreScheduled()
    {
        using var db = Db();
        db.IdentityCandidates.AddRange(
            new IdentityCandidate { Id = 1, Domain = "one.example.com", ProposedName = "仅核验", Status = "confirmed" },
            new IdentityCandidate { Id = 2, Domain = "two.example.com", ProposedName = "已成线索", Status = "confirmed" },
            new IdentityCandidate { Id = 3, Domain = "three.example.com", ProposedName = "已转客户", Status = "confirmed" },
            new IdentityCandidate { Id = 4, Domain = "four.example.com", ProposedName = "未核验", Status = "pending" });
        db.Leads.AddRange(
            new Lead { Id = 20, SourceIdentityCandidateId = 2, CompanyName = "已成线索" },
            new Lead { Id = 30, SourceIdentityCandidateId = 3, CompanyName = "已转客户", ConvertedToCompanyId = 40 });
        db.Companies.Add(new Company { Id = 40, FullName = "已转客户", Status = CustomerStatus.Deal });
        await db.SaveChangesAsync();
        var service = new WebsiteRecheckService(db);
        Assert.Equal(1, Total(await service.ListAsync("identity", 1, default)));
        Assert.Equal(1, Total(await service.ListAsync("lead", 1, default)));
        Assert.Equal(1, Total(await service.ListAsync("customer", 1, default)));
        Assert.False((await service.GetAsync(1, default)).Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateBatchAsync(new(["1", "4"], true), 8, default));
        Assert.Empty(await db.WebsiteRecheckSchedules.ToListAsync());
        Assert.Equal(2, await service.UpdateBatchAsync(new(["1", "2"], true), 8, default));
        Assert.Equal(2, await db.WebsiteRecheckSchedules.CountAsync(s => s.Enabled));
        Assert.Equal(2, await db.WebsiteRecheckChanges.CountAsync(change => change.Enabled && change.ChangedBy == 8));
        Assert.InRange((await service.GetAsync(1, default)).NextDueAt!.Value,
            DateTime.UtcNow.AddDays(7).AddMinutes(-1), DateTime.UtcNow.AddDays(7).AddMinutes(1));
        await service.UpdateBatchAsync(new(["1"], false), 8, default);
        Assert.False((await service.GetAsync(1, default)).Enabled);
        Assert.True((await service.GetAsync(2, default)).Enabled);
        Assert.Null((await service.GetAsync(1, default)).NextDueAt);
        Assert.Equal(3, await db.WebsiteRecheckChanges.CountAsync());
        using var history = JsonDocument.Parse(JsonSerializer.Serialize(await service.HistoryAsync(1, default)));
        Assert.False(history.RootElement.GetProperty("items")[0].GetProperty("Enabled").GetBoolean());
        Assert.Equal(2, history.RootElement.GetProperty("items").GetArrayLength());
    }

    private static int Total(object result)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return doc.RootElement.GetProperty("total").GetInt32();
    }
}
