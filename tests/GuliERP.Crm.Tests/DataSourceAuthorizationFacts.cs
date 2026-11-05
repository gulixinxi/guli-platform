using System.Text.Json;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class DataSourceAuthorizationFacts
{
    [Fact]
    public async Task SavesVersionedRegistrationWithoutEnablingRequests()
    {
        using var db = NewDb(Guid.NewGuid().ToString("N"), 11, 22);
        var service = new DataSourceAuthorizationService(db, new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var first = new DataSourceAuthorizationInput(0, "draft", "agreement-1", "domain", null, null,
            "待确认", null, null, null);
        await service.SaveAsync("quake", first, 7, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveAsync("quake", first, 7, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync("unknown", first, 7, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync("quake", first with { ExpectedVersion = 1, Status = "reviewed" }, 7, default));

        var reviewed = first with { ExpectedVersion = 1, Status = "reviewed", RetentionDays = 30,
            DailyQuota = 10, ValidFrom = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30) };
        var result = await service.SaveAsync("quake", reviewed, 8, default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(2, json.RootElement.GetProperty("Version").GetInt32());
        Assert.False(json.RootElement.GetProperty("EnablesAutomaticRequests").GetBoolean());
        var revisions = await service.HistoryAsync("quake", default);
        Assert.Equal(new[] { 2, 1 }, revisions.Select(x => x.Version));
        Assert.Equal(new long[] { 8, 7 }, revisions.Select(x => x.ActorId));
    }

    [Fact]
    public async Task TenantAndCompanyIsolationHidesOtherCompanyRecords()
    {
        var name = Guid.NewGuid().ToString("N");
        using var companyA = NewDb(name, 11, 22);
        var serviceA = new DataSourceAuthorizationService(companyA, new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        await serviceA.SaveAsync("registry", new(0, "draft", "ref-a", "name", null, null, "pending", null, null, null), 7, default);
        using var companyB = NewDb(name, 11, 23);
        var serviceB = new DataSourceAuthorizationService(companyB, new MutableCurrentTenant(11), new MutableCurrentCompany(23));
        Assert.Empty(await serviceB.ListAsync(default));
        Assert.Empty(await serviceB.HistoryAsync("registry", default));
        await serviceB.SaveAsync("registry", new(0, "draft", "ref-b", "name", null, null, "pending", null, null, null), 8, default);
        Assert.Single(await serviceA.ListAsync(default));
        Assert.Single(await serviceB.ListAsync(default));
    }

    private static CrmDbContext NewDb(string name, long tenant, long company) => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name).Options,
        new MutableCurrentTenant(tenant), new MutableCurrentCompany(company));
}
