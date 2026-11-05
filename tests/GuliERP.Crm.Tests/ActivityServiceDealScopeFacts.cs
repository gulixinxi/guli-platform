using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using GuliERP.Foundation.Kernel;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

/// <summary>
/// R30 P1-03：跟进记录关联商机时的归属校验。
///
/// <para>
/// 规则：<c>ActivityService.CreateAsync</c> 接受 DealId 时，目标商机必须与该跟进记录的
/// CRM 客户属于同一租户、同一公司，且商机本身属于该 CRM 客户；否则拒绝保存（不落库）。
/// </para>
///
/// <para>
/// 覆盖：合法同客户商机 / 不同客户 / 不同公司 / 不同租户 / 商机不存在 / 无 DealId 路径。
/// 全部基于 EF Core InMemory 内存库，不代表 PostgreSQL 或正式环境验收。
/// </para>
/// </summary>
public class ActivityServiceDealScopeFacts
{
    private const long TenantId = 50;
    private const long CompanyId = 51;
    private const long OtherCompanyId = 52;
    private const long OtherTenantId = 53;

    private const long CrmCompanyAId = 900001;
    private const long CrmCompanyBId = 900002;
    private const long DealId = 900003;
    private const long MissingDealId = 900099;

    private static CrmDbContext CreateDb(
        ICurrentTenant? tenant = null,
        ICurrentCompany? company = null,
        string? databaseName = null)
    {
        return new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"))
                .Options,
            tenant ?? new MutableCurrentTenant(TenantId),
            company ?? new MutableCurrentCompany(CompanyId));
    }

    private static Company MakeCrmCompany(long id, string companyNo, string fullName) => new()
    {
        Id = id,
        CompanyNo = companyNo,
        FullName = fullName,
        Source = SourceChannel.Other,
        Status = CustomerStatus.ToContact,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static Deal MakeDeal(long id, string dealNo, string name, long crmCompanyId) => new()
    {
        Id = id,
        DealNo = dealNo,
        Name = name,
        CrmCompanyId = crmCompanyId,
        Category = BusinessCategory.Other,
        Stage = DealStage.InitialContact,
        Status = DealStatus.InProgress,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static CreateActivityRequest Request(long crmCompanyId, long? dealId) =>
        new(crmCompanyId, dealId, FollowType.Phone, null, "测试跟进内容", null, "下一步动作", null, null, null, null);

    [Fact]
    public async Task CreateAsync_DealOfSameCrmCompany_IsSaved()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCrmCompany(CrmCompanyAId, "CO-A", "客户A"));
        db.Deals.Add(MakeDeal(DealId, "DE-1", "客户A的商机", CrmCompanyAId));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var result = await service.CreateAsync(
            Request(CrmCompanyAId, DealId), userId: 7, CancellationToken.None);

        Assert.Equal(CrmCompanyAId, result.CrmCompanyId);
        Assert.Equal(DealId, result.DealId);
        Assert.Single(await db.Activities.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_DealOfAnotherCrmCompany_IsRejectedAndNotSaved()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCrmCompany(CrmCompanyAId, "CO-A", "客户A"));
        db.Companies.Add(MakeCrmCompany(CrmCompanyBId, "CO-B", "客户B"));
        db.Deals.Add(MakeDeal(DealId, "DE-1", "客户B的商机", CrmCompanyBId));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Request(CrmCompanyAId, DealId), userId: 7, CancellationToken.None));

        Assert.Equal(ActivityService.DealScopeErrorMessage, ex.Message);
        Assert.Empty(await db.Activities.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_DealInAnotherCompanyScope_IsRejectedAndNotSaved()
    {
        var databaseName = Guid.NewGuid().ToString("N");

        // 同一租户、不同公司范围下的客户与商机
        using (var otherScope = CreateDb(
            new MutableCurrentTenant(TenantId), new MutableCurrentCompany(OtherCompanyId), databaseName))
        {
            otherScope.Companies.Add(MakeCrmCompany(CrmCompanyBId, "CO-B", "客户B"));
            otherScope.Deals.Add(MakeDeal(DealId, "DE-1", "客户B的商机", CrmCompanyBId));
            await otherScope.SaveChangesAsync();
        }

        using var db = CreateDb(databaseName: databaseName);
        db.Companies.Add(MakeCrmCompany(CrmCompanyAId, "CO-A", "客户A"));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Request(CrmCompanyAId, DealId), userId: 7, CancellationToken.None));

        Assert.Equal(ActivityService.DealScopeErrorMessage, ex.Message);
        Assert.Empty(await db.Activities.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_DealInAnotherTenantScope_IsRejectedAndNotSaved()
    {
        var databaseName = Guid.NewGuid().ToString("N");

        // 同一公司、不同租户范围下的客户与商机
        using (var otherScope = CreateDb(
            new MutableCurrentTenant(OtherTenantId), new MutableCurrentCompany(CompanyId), databaseName))
        {
            otherScope.Companies.Add(MakeCrmCompany(CrmCompanyBId, "CO-B", "客户B"));
            otherScope.Deals.Add(MakeDeal(DealId, "DE-1", "客户B的商机", CrmCompanyBId));
            await otherScope.SaveChangesAsync();
        }

        using var db = CreateDb(databaseName: databaseName);
        db.Companies.Add(MakeCrmCompany(CrmCompanyAId, "CO-A", "客户A"));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Request(CrmCompanyAId, DealId), userId: 7, CancellationToken.None));

        Assert.Equal(ActivityService.DealScopeErrorMessage, ex.Message);
        Assert.Empty(await db.Activities.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_MissingDeal_IsRejectedAndNotSaved()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCrmCompany(CrmCompanyAId, "CO-A", "客户A"));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Request(CrmCompanyAId, MissingDealId), userId: 7, CancellationToken.None));

        Assert.Equal(ActivityService.DealScopeErrorMessage, ex.Message);
        Assert.Empty(await db.Activities.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_WithoutDeal_IsStillSaved()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCrmCompany(CrmCompanyAId, "CO-A", "客户A"));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var result = await service.CreateAsync(
            Request(CrmCompanyAId, null), userId: 7, CancellationToken.None);

        Assert.Null(result.DealId);
        Assert.Equal(CrmCompanyAId, result.CrmCompanyId);
        Assert.Single(await db.Activities.ToListAsync());
    }

    /// <summary>
    /// 跨范围的商机与不存在的商机必须给出完全一致的报错，
    /// 否则调用方可以通过报错差异反推某个商机在别的租户是否存在（存在性预言机）。
    /// </summary>
    [Fact]
    public async Task CreateAsync_CrossScopeDealAndMissingDeal_AreIndistinguishable()
    {
        var databaseName = Guid.NewGuid().ToString("N");

        using (var otherScope = CreateDb(
            new MutableCurrentTenant(TenantId), new MutableCurrentCompany(OtherCompanyId), databaseName))
        {
            otherScope.Companies.Add(MakeCrmCompany(CrmCompanyBId, "CO-B", "客户B"));
            otherScope.Deals.Add(MakeDeal(DealId, "DE-1", "客户B的商机", CrmCompanyBId));
            await otherScope.SaveChangesAsync();
        }

        using var db = CreateDb(databaseName: databaseName);
        db.Companies.Add(MakeCrmCompany(CrmCompanyAId, "CO-A", "客户A"));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);

        var crossScope = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Request(CrmCompanyAId, DealId), userId: 7, CancellationToken.None));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Request(CrmCompanyAId, MissingDealId), userId: 7, CancellationToken.None));

        Assert.Equal(crossScope.Message.Replace($"(deal {DealId})", "(deal *)"), missing.Message.Replace($"(deal {MissingDealId})", "(deal *)"));
        Assert.Empty(await db.Activities.ToListAsync());
    }
}
