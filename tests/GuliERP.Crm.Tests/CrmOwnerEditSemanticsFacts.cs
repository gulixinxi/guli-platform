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
/// R42：普通编辑接口不得更换或清空负责人（用户已定案）。
///
/// <para>
/// 规则：
/// <list type="bullet">
///   <item>省略 <c>ownerId</c> 或传 null ⇒ 保持原负责人。</item>
///   <item>传原值 ⇒ 保持原负责人（无变更）。</item>
///   <item>传非 null 且与当前负责人不同 ⇒ 拒绝，并指向专门分配操作。</item>
///   <item>当前负责人为 null 时，传任何非 null 都算「变更」⇒ 拒绝。</item>
/// </list>
/// </para>
///
/// <para>
/// 判定放在服务层（<see cref="CrmOwnerRules.IsOwnerChange"/>），
/// 因此任何调用方——不只是 HTTP——都拿到同一结论。
/// 本文件全部基于 EF Core InMemory 内存库，不代表 PostgreSQL 或正式环境验收。
/// </para>
/// </summary>
public class CrmOwnerEditSemanticsFacts
{
    private const long TenantId = 70;
    private const long CompanyId = 71;
    private const long OtherCompanyId = 72;

    private const long CompanyRecordId = 910001;
    private const long DealRecordId = 910002;
    private const long ActivityRecordId = 910003;

    private const long CurrentOwnerId = 500;
    private const long OtherOwnerId = 600;

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

    private static Company MakeCompany(long? ownerId) => new()
    {
        Id = CompanyRecordId,
        CompanyNo = "CO-OWNER-1",
        FullName = "负责人语义测试客户",
        Source = SourceChannel.Other,
        Status = CustomerStatus.ToContact,
        OwnerId = ownerId,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static Deal MakeDeal(long? ownerId) => new()
    {
        Id = DealRecordId,
        DealNo = "DE-OWNER-1",
        Name = "负责人语义测试商机",
        CrmCompanyId = CompanyRecordId,
        Category = BusinessCategory.Other,
        Stage = DealStage.InitialContact,
        Status = DealStatus.InProgress,
        OwnerId = ownerId,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static Activity MakeActivity(long? ownerId) => new()
    {
        Id = ActivityRecordId,
        CrmCompanyId = CompanyRecordId,
        FollowType = FollowType.Phone,
        FollowAt = DateTimeOffset.UtcNow,
        Content = "负责人语义测试跟进",
        NextAction = "回访",
        OwnerId = ownerId,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static UpdateCompanyRequest CompanyUpdate(long? ownerId) => new(
        "负责人语义测试客户",
        null, null, null,
        CompanyCity.Weifang, null, null,
        CompanyIndustry.Service, null, null,
        false, null,
        SourceChannel.Other, null,
        ownerId,
        CustomerStatus.ToContact, null, null);

    private static UpdateDealRequest DealUpdate(long? ownerId) => new(
        "负责人语义测试商机",
        BusinessCategory.Other,
        100m,
        DealStage.InitialContact,
        null, null,
        ownerId,
        DealStatus.InProgress, null, null);

    private static UpdateActivityRequest ActivityUpdate(long? ownerId) => new(
        FollowType.Phone,
        DateTimeOffset.UtcNow,
        "负责人语义测试跟进",
        null,
        "回访",
        null,
        ownerId,
        null, null);

    // ===== 客户编辑 =====

    [Fact]
    public async Task CompanyUpdate_WithoutOwner_KeepsExistingOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new CompanyService(db, new TestCrmNumberGenerator());
        var updated = await service.UpdateAsync(
            CompanyRecordId, CompanyUpdate(ownerId: null), CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(CurrentOwnerId, updated.OwnerId);
        Assert.Equal(CurrentOwnerId, (await db.Companies.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task CompanyUpdate_WithSameOwner_KeepsExistingOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new CompanyService(db, new TestCrmNumberGenerator());
        var updated = await service.UpdateAsync(
            CompanyRecordId, CompanyUpdate(CurrentOwnerId), CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(CurrentOwnerId, (await db.Companies.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task CompanyUpdate_WithDifferentOwner_IsRejectedAndKeepsOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new CompanyService(db, new TestCrmNumberGenerator());
        var ex = await Assert.ThrowsAsync<CrmOwnerChangeNotAllowedException>(() =>
            service.UpdateAsync(CompanyRecordId, CompanyUpdate(OtherOwnerId), CancellationToken.None));

        Assert.Equal(CrmOwnerEntityKind.Company, ex.Kind);
        Assert.Equal(CurrentOwnerId, (await db.Companies.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task CompanyUpdate_WhenOwnerIsNull_AndRequestSetsOwner_IsRejected()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(null));
        await db.SaveChangesAsync();

        var service = new CompanyService(db, new TestCrmNumberGenerator());
        await Assert.ThrowsAsync<CrmOwnerChangeNotAllowedException>(() =>
            service.UpdateAsync(CompanyRecordId, CompanyUpdate(CurrentOwnerId), CancellationToken.None));

        Assert.Null((await db.Companies.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task CompanyClearOwner_UsesDedicatedOperation()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new CompanyService(db, new TestCrmNumberGenerator());
        Assert.True(await service.ClearOwnerAsync(CompanyRecordId, CancellationToken.None));
        Assert.Null((await db.Companies.SingleAsync()).OwnerId);
    }

    // ===== 商机编辑 =====

    [Fact]
    public async Task DealUpdate_WithoutOwner_KeepsExistingOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Deals.Add(MakeDeal(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new DealService(db, new TestCrmNumberGenerator());
        var updated = await service.UpdateAsync(
            DealRecordId, DealUpdate(ownerId: null), CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(CurrentOwnerId, (await db.Deals.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task DealUpdate_WithSameOwner_KeepsExistingOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Deals.Add(MakeDeal(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new DealService(db, new TestCrmNumberGenerator());
        var updated = await service.UpdateAsync(
            DealRecordId, DealUpdate(CurrentOwnerId), CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(CurrentOwnerId, (await db.Deals.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task DealUpdate_WithDifferentOwner_IsRejectedAndKeepsOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Deals.Add(MakeDeal(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new DealService(db, new TestCrmNumberGenerator());
        var ex = await Assert.ThrowsAsync<CrmOwnerChangeNotAllowedException>(() =>
            service.UpdateAsync(DealRecordId, DealUpdate(OtherOwnerId), CancellationToken.None));

        Assert.Equal(CrmOwnerEntityKind.Deal, ex.Kind);
        Assert.Equal(CurrentOwnerId, (await db.Deals.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task DealUpdate_WhenOwnerIsNull_AndRequestSetsOwner_IsRejected()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(null));
        db.Deals.Add(MakeDeal(null));
        await db.SaveChangesAsync();

        var service = new DealService(db, new TestCrmNumberGenerator());
        await Assert.ThrowsAsync<CrmOwnerChangeNotAllowedException>(() =>
            service.UpdateAsync(DealRecordId, DealUpdate(CurrentOwnerId), CancellationToken.None));

        Assert.Null((await db.Deals.SingleAsync()).OwnerId);
    }

    // ===== 跟进记录编辑 =====

    [Fact]
    public async Task ActivityUpdate_WithoutOwner_KeepsExistingOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Activities.Add(MakeActivity(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var updated = await service.UpdateAsync(
            ActivityRecordId, ActivityUpdate(ownerId: null), CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(CurrentOwnerId, (await db.Activities.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task ActivityUpdate_WithSameOwner_KeepsExistingOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Activities.Add(MakeActivity(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var updated = await service.UpdateAsync(
            ActivityRecordId, ActivityUpdate(CurrentOwnerId), CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(CurrentOwnerId, (await db.Activities.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task ActivityUpdate_WithDifferentOwner_IsRejectedAndKeepsOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Activities.Add(MakeActivity(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        var ex = await Assert.ThrowsAsync<CrmOwnerChangeNotAllowedException>(() =>
            service.UpdateAsync(ActivityRecordId, ActivityUpdate(OtherOwnerId), CancellationToken.None));

        Assert.Equal(CrmOwnerEntityKind.Activity, ex.Kind);
        Assert.Equal(CurrentOwnerId, (await db.Activities.SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task ActivityUpdate_WhenOwnerIsNull_AndRequestSetsOwner_IsRejected()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(null));
        db.Activities.Add(MakeActivity(null));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        await Assert.ThrowsAsync<CrmOwnerChangeNotAllowedException>(() =>
            service.UpdateAsync(ActivityRecordId, ActivityUpdate(CurrentOwnerId), CancellationToken.None));

        Assert.Null((await db.Activities.SingleAsync()).OwnerId);
    }

    // ===== 专门分配操作（商机 / 跟进） =====

    [Fact]
    public async Task DealAssignOwner_SetsOwnerAndAuditActor()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(null));
        db.Deals.Add(MakeDeal(null));
        await db.SaveChangesAsync();

        var service = new DealService(db, new TestCrmNumberGenerator());
        Assert.True(await service.AssignOwnerAsync(DealRecordId, OtherOwnerId, 42, CancellationToken.None));

        var stored = await db.Deals.SingleAsync();
        Assert.Equal(OtherOwnerId, stored.OwnerId);
        Assert.Equal(42, stored.ModifiedBy);
    }

    [Fact]
    public async Task DealAssignOwner_WithNullOwner_ClearsOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Deals.Add(MakeDeal(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new DealService(db, new TestCrmNumberGenerator());
        Assert.True(await service.AssignOwnerAsync(DealRecordId, null, 42, CancellationToken.None));

        var stored = await db.Deals.SingleAsync();
        Assert.Null(stored.OwnerId);
        Assert.Equal(42, stored.ModifiedBy);
    }

    [Fact]
    public async Task ActivityAssignOwner_SetsOwnerAndAuditActor()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(null));
        db.Activities.Add(MakeActivity(null));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        Assert.True(await service.AssignOwnerAsync(ActivityRecordId, OtherOwnerId, 43, CancellationToken.None));

        var stored = await db.Activities.SingleAsync();
        Assert.Equal(OtherOwnerId, stored.OwnerId);
        Assert.Equal(43, stored.ModifiedBy);
    }

    [Fact]
    public async Task ActivityAssignOwner_WithNullOwner_ClearsOwner()
    {
        using var db = CreateDb();
        db.Companies.Add(MakeCompany(CurrentOwnerId));
        db.Activities.Add(MakeActivity(CurrentOwnerId));
        await db.SaveChangesAsync();

        var service = new ActivityService(db);
        Assert.True(await service.AssignOwnerAsync(ActivityRecordId, null, 43, CancellationToken.None));

        var stored = await db.Activities.SingleAsync();
        Assert.Null(stored.OwnerId);
        Assert.Equal(43, stored.ModifiedBy);
    }

    [Fact]
    public async Task AssignOwner_OutsideCurrentCompanyScope_ReturnsFalse()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        using (var otherScope = CreateDb(
            new MutableCurrentTenant(TenantId), new MutableCurrentCompany(OtherCompanyId), databaseName))
        {
            otherScope.Companies.Add(MakeCompany(CurrentOwnerId));
            otherScope.Deals.Add(MakeDeal(CurrentOwnerId));
            await otherScope.SaveChangesAsync();
        }

        using var db = CreateDb(databaseName: databaseName);
        var service = new DealService(db, new TestCrmNumberGenerator());
        Assert.False(await service.AssignOwnerAsync(DealRecordId, OtherOwnerId, 42, CancellationToken.None));
    }
}
