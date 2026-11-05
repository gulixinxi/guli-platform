using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public class ReverseLookupRecordFacts
{
    [Fact]
    public async Task UnknownAndFailedResultsAreSavedAndCompanyScoped()
    {
        var tenant = new MutableCurrentTenant(100);
        var company = new MutableCurrentCompany(200);
        using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            tenant, company);
        var records = new ReverseLookupRecordService(db);

        await records.SaveBatchAsync([
            new ReverseLookupResult { CompanyName = "待核验单位", WebsiteStatus = "网址未提供，待核验", Suggestion = "官网信息未知" },
            new ReverseLookupResult { CompanyName = "失败单位", InputWebsite = "example.invalid", Error = "查询失败" }
        ], CancellationToken.None);

        Assert.Equal(2, (await records.GetRecentAsync(CancellationToken.None)).Count);
        Assert.All(await db.ReverseLookupRecords.ToListAsync(), row =>
        {
            Assert.Equal((100L, 200L), (row.TenantId, row.CompanyId));
            Assert.Contains("companyName", row.ResultJson);
        });
        company.Set(201);
        Assert.Empty(await records.GetRecentAsync(CancellationToken.None));
        company.Set(200);
        tenant.Set(101);
        Assert.Empty(await records.GetRecentAsync(CancellationToken.None));
    }
}
