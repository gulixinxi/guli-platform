using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class CompanyDeletionSafetyFacts
{
    [Fact]
    public async Task DeleteCompany_DoesNotRemoveRelatedBusinessRecords()
    {
        using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            new MutableCurrentTenant(10), new MutableCurrentCompany(20));
        db.Companies.Add(new Company { Id = 100, CompanyNo = "C-100", FullName = "测试客户" });
        db.Contacts.Add(new Contact { Id = 101, CrmCompanyId = 100, Name = "联系人" });
        db.Deals.Add(new Deal { Id = 102, CrmCompanyId = 100, DealNo = "D-102", Name = "商机" });
        db.Activities.Add(new Activity
        {
            Id = 103, CrmCompanyId = 100, Content = "跟进", NextAction = "回访"
        });
        await db.SaveChangesAsync();

        var service = new CompanyService(db, new TestCrmNumberGenerator());
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await service.DeleteAsync(100, CancellationToken.None));

        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Equal(1, await db.Contacts.CountAsync());
        Assert.Equal(1, await db.Deals.CountAsync());
        Assert.Equal(1, await db.Activities.CountAsync());
    }
}
