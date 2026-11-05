using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public class LeadDemandAssessmentFacts
{
    [Theory]
    [InlineData("website", BusinessCategory.WebsiteBuild)]
    [InlineData("geo", BusinessCategory.GEOOpt)]
    [InlineData("social_media", BusinessCategory.SocialMedia)]
    [InlineData("foreign_trade", BusinessCategory.ForeignTradeLeads)]
    public void AssessmentCategories_UseExplicitBusinessMapping(string wire, BusinessCategory category)
    {
        Assert.Equal(category, AssessmentCategoryMap.ToBusinessCategory(wire));
        Assert.Equal(wire, AssessmentCategoryMap.ToWireCategory(category));
    }

    [Fact]
    public void UnknownAssessmentCategory_NeverBecomesOther()
    {
        Assert.Throws<ArgumentException>(() => AssessmentCategoryMap.ToBusinessCategory("seo"));
        Assert.Throws<ArgumentException>(() => AssessmentCategoryMap.ToWireCategory(BusinessCategory.Other));
    }

    [Fact]
    public async Task DealRequiresCurrentConfirmedAssessmentForConvertedCustomer()
    {
        var tenant = new MutableCurrentTenant(100);
        var scope = new MutableCurrentCompany(200);
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, tenant, scope);
        db.Companies.Add(new Company { Id = 10, FullName = "测试客户" });
        db.Leads.Add(new Lead { Id = 11, CompanyName = "测试线索", ConvertedToCompanyId = 10 });
        db.LeadDemandAssessments.Add(new LeadDemandAssessment
        { Id = 12, LeadId = 11, Category = BusinessCategory.GEOOpt, State = "confirmed", Version = 2, Evidence = "人工确认" });
        await db.SaveChangesAsync();
        var deals = new DealService(db, new TestCrmNumberGenerator());
        var valid = new CreateDealRequest("GEO 商机", 10, null, BusinessCategory.GEOOpt,
            1000, null, null, null, null, null, 12, 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.CreateAsync(valid with
            { SourceLeadDemandAssessmentId = null, SourceLeadDemandAssessmentVersion = null }, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.CreateAsync(valid with
            { SourceLeadDemandAssessmentVersion = null }, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.CreateAsync(valid with
            { SourceLeadDemandAssessmentVersion = 1 }, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.CreateAsync(valid with
            { Category = BusinessCategory.WebsiteBuild }, null, CancellationToken.None));
        await deals.CreateAsync(valid, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.CreateAsync(valid, null, CancellationToken.None));
        Assert.Equal(12, (await db.Deals.SingleAsync()).SourceLeadDemandAssessmentId);
    }

    [Fact]
    public async Task AssessmentRequiresEvidenceAndStaysWithinCompany()
    {
        var tenant = new MutableCurrentTenant(100);
        var company = new MutableCurrentCompany(200);
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, tenant, company);
        db.Leads.Add(new Lead { Id = 123, CompanyName = "测试线索" });
        await db.SaveChangesAsync();
        var service = new LeadDemandAssessmentService(db);
        // 阶段 B1：只有自由文本不再能满足「已确认」——那条路径无法回溯。
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(123, "geo",
            new LeadDemandAssessmentInput("confirmed", "high", "客户口头确认", null, null), 500, CancellationToken.None));
        Assert.NotNull(await service.SaveAsync(123, "geo",
            new LeadDemandAssessmentInput("potential", "medium", null, "可见性偏低", "复测",
                [new EvidenceItemInput("geo", "https://example.com", null, "verified", "官网初测结果待复核")]),
            500, CancellationToken.None));
        Assert.Equal(1, (await db.LeadDemandAssessments.SingleAsync()).Version);
        await service.SaveAsync(123, "geo",
            new LeadDemandAssessmentInput("pending", "low", "需补充目标问答场景", null, "访谈"),
            500, CancellationToken.None);
        Assert.Equal(2, (await db.LeadDemandAssessments.SingleAsync()).Version);
        var history = await db.LeadDemandAssessmentRevisions.OrderBy(r => r.Version).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal("potential", history[0].State);
        Assert.Equal("pending", history[1].State);

        company.Set(201);
        Assert.Null(await service.GetAsync(123, CancellationToken.None));
        Assert.Empty(await db.LeadDemandAssessments.ToListAsync());
        Assert.Empty(await db.LeadDemandAssessmentRevisions.ToListAsync());
    }
}
