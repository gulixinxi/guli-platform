using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public class CrmBusinessFlowFacts
{
    [Fact]
    public async Task LeadToWonDeal_CreatesScopedCustomerContactAndFirstActivity()
    {
        var tenant = new MutableCurrentTenant(100);
        var companyScope = new MutableCurrentCompany(200);
        using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            tenant,
            companyScope);
        var numbers = new TestCrmNumberGenerator();
        var leads = new LeadService(db, numbers);
        var deals = new DealService(db, numbers);

        var lead = await leads.CreateAsync(new CreateLeadRequest(
            "验收测试企业", null, null, "测试联系人", null, null, null, null, null,
            CompanyCity.Weifang, null, null, CompanyIndustry.Service, null,
            SourceChannel.Referral, null, null, null, "导入补充资料"), 300, CancellationToken.None);
        Assert.True(await leads.ClaimAsync(lead.Id, 300, CancellationToken.None));

        var customer = await leads.ConvertAsync(new ConvertLeadRequest(
            lead.Id, "验收测试企业", null, null, CompanyCity.Weifang, null, null,
            CompanyIndustry.Service, null, null, false, null, null, null),
            300, CancellationToken.None);

        Assert.Equal(LeadStatus.Converted, (await db.Leads.SingleAsync()).Status);
        Assert.Equal(customer.Id, (await db.Leads.SingleAsync()).ConvertedToCompanyId);
        var storedCustomer = await db.Companies.SingleAsync();
        Assert.Equal((100L, 200L), (storedCustomer.TenantId, storedCustomer.CompanyId));
        Assert.Equal("导入补充资料", storedCustomer.Remarks);
        Assert.Equal(customer.Id, (await db.Contacts.SingleAsync()).CrmCompanyId);
        Assert.Equal(customer.Id, (await db.Activities.SingleAsync()).CrmCompanyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => leads.ConvertAsync(
            new ConvertLeadRequest(lead.Id, "重复转化", null, null, CompanyCity.Weifang,
                null, null, CompanyIndustry.Service, null, null, false, null, null, null),
            300, CancellationToken.None));

        var assessments = new LeadDemandAssessmentService(db);
        // 阶段 B1 起，「已确认」需要结构化证据（旧契约只要求一段自由文本）。
        await assessments.SaveAsync(lead.Id, "website",
            new LeadDemandAssessmentInput("confirmed", "high", null, "客户访谈", "制定方案",
                [new EvidenceItemInput("manual", null, null, "verified", "测试客户确认需要官网建设")]),
            300, CancellationToken.None);
        var sourceAssessment = await db.LeadDemandAssessments.SingleAsync();
        var validContactId = (await db.Contacts.SingleAsync()).Id;
        companyScope.Set(201);
        db.Contacts.Add(new Contact { Id = 999001, CrmCompanyId = customer.Id, Name = "其他公司联系人" });
        await db.SaveChangesAsync();
        companyScope.Set(200);
        db.Contacts.Add(new Contact { Id = 999002, CrmCompanyId = customer.Id + 1, Name = "其他客户联系人" });
        await db.SaveChangesAsync();
        var dealRequest = new CreateDealRequest(
            "验收商机", customer.Id, validContactId, BusinessCategory.WebsiteBuild, 1000m,
            null, null, null, 300, null, sourceAssessment.Id, sourceAssessment.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.CreateAsync(
            dealRequest with { ContactId = 999001 }, 300, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.CreateAsync(
            dealRequest with { ContactId = 999002 }, 300, CancellationToken.None));
        Assert.Empty(await db.Deals.ToListAsync());
        var deal = await deals.CreateAsync(dealRequest, 300, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deals.UpdateAsync(deal.Id,
            new UpdateDealRequest("试图改品类", BusinessCategory.GEOOpt, 1000m,
                DealStage.InitialContact, null, null, 300, DealStatus.InProgress, null, null), CancellationToken.None));
        Assert.Equal(BusinessCategory.WebsiteBuild, (await db.Deals.SingleAsync()).Category);
        Assert.True(await deals.UpdateStageAsync(deal.Id, DealStage.Proposal, CancellationToken.None));
        Assert.True(await deals.WinAsync(deal.Id, CancellationToken.None));
        Assert.Equal(DealStatus.Won, (await db.Deals.SingleAsync()).Status);
        Assert.Equal(DealStage.Won, (await db.Deals.SingleAsync()).Stage);

        companyScope.Set(201);
        Assert.Null(await leads.GetByIdAsync(lead.Id, CancellationToken.None));
        Assert.Null(await deals.GetByIdAsync(deal.Id, CancellationToken.None));
        Assert.False(await leads.AssignAsync(lead.Id, 999, CancellationToken.None));
        Assert.False(await deals.LoseAsync(deal.Id, LostReason.Other, CancellationToken.None));
        companyScope.Set(200);
        Assert.Equal(300, (await db.Leads.SingleAsync()).OwnerId);
        Assert.Equal(DealStatus.Won, (await db.Deals.SingleAsync()).Status);
        tenant.Set(101);
        Assert.Null(await leads.GetByIdAsync(lead.Id, CancellationToken.None));
        Assert.False(await deals.UpdateStageAsync(deal.Id, DealStage.Negotiation, CancellationToken.None));
    }
}
