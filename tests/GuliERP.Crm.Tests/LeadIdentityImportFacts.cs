using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public class LeadIdentityImportFacts
{
    [Fact]
    public async Task BatchImport_DeduplicatesStrongIdentityButNotSharedDomain()
    {
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200));
        var service = new LeadService(db, new TestCrmNumberGenerator());
        var result = await service.BatchImportAsync([
            Request("甲公司", "ab123", "shared.example"),
            Request("甲公司重复", "AB123", "other.example"),
            Request("乙公司", "CD456", "shared.example")
        ], 300, CancellationToken.None);

        Assert.Equal(2, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, await db.Leads.CountAsync());
    }

    [Fact]
    public async Task SingleCreate_WithoutCreditCode_UsesNormalizedName()
    {
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(100), new MutableCurrentCompany(200));
        var service = new LeadService(db, new TestCrmNumberGenerator());
        var first = await service.CreateAsync(Request("  Example Co  ", null, null), 300, CancellationToken.None);
        var second = await service.CreateAsync(Request("example co", null, null), 300, CancellationToken.None);
        Assert.Equal(first.Id, second.Id);
    }

    private static CreateLeadRequest Request(string name, string? code, string? domain) =>
        new(name, code, domain, null, null, null, null, null, null,
            null, null, null, null, null, SourceChannel.Excel,
            null, null, null, null);
}
