using System.Text;
using GuliERP.Api.Crm;
using GuliERP.Crm.Application;
using GuliERP.Foundation.Kernel;
using GuliERP.Identity.Domain.Entities;
using GuliERP.Identity.Domain.Enums;
using GuliERP.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GuliERP.Api.Tests;

/// <summary>
/// R42：API 层负责人有效性检查（当前租户 + 当前公司 + 用户 Active + 成员关系 Active）。
///
/// <para>
/// 四要素与既有客户／线索分配端点逐字一致，本文件覆盖该规则的边界格：
/// 空负责人 / 有效成员 / 跨公司成员 / 停用用户 / 跨租户用户 / 成员关系已撤销 / 用户不存在 /
/// 缺少租户公司范围。
/// </para>
///
/// <para>
/// 全部基于 EF Core InMemory 内存库，<b>不代表 PostgreSQL 或真实 HTTP 验收</b>。
/// </para>
/// </summary>
public sealed class CrmOwnerGuardFacts
{
    private const long TenantId = 1001;
    private const long CompanyId = 2001;
    private const long OtherCompanyId = 2002;
    private const long OtherTenantId = 1002;

    private const long ActiveMemberId = 30001;
    private const long DisabledUserId = 30002;
    private const long OtherCompanyMemberId = 30003;
    private const long OtherTenantMemberId = 30004;
    private const long RevokedMemberId = 30005;
    private const long UnknownUserId = 30999;

    private static IdentityDbContext CreateIdentityDb()
    {
        return new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
    }

    private static GuliErpUser MakeUser(long id, long tenantId, UserStatus status) => new()
    {
        Id = id,
        UserName = $"owner_user_{id}",
        NormalizedUserName = $"OWNER_USER_{id}",
        DisplayName = $"负责人测试用户{id}",
        TenantId = tenantId,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static UserCompanyMembership MakeMembership(
        long id, long tenantId, long companyId, long userId, MembershipStatus status) => new()
    {
        Id = id,
        TenantId = tenantId,
        CompanyId = companyId,
        UserId = userId,
        Status = status,
        JoinedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        ModifiedAt = DateTimeOffset.UtcNow
    };

    private static async Task<IdentityDbContext> SeedAsync()
    {
        var db = CreateIdentityDb();
        db.Users.AddRange(
            MakeUser(ActiveMemberId, TenantId, UserStatus.Active),
            MakeUser(DisabledUserId, TenantId, UserStatus.Disabled),
            MakeUser(OtherCompanyMemberId, TenantId, UserStatus.Active),
            MakeUser(OtherTenantMemberId, OtherTenantId, UserStatus.Active),
            MakeUser(RevokedMemberId, TenantId, UserStatus.Active));
        db.UserCompanyMemberships.AddRange(
            MakeMembership(1, TenantId, CompanyId, ActiveMemberId, MembershipStatus.Active),
            MakeMembership(2, TenantId, CompanyId, DisabledUserId, MembershipStatus.Active),
            MakeMembership(3, TenantId, OtherCompanyId, OtherCompanyMemberId, MembershipStatus.Active),
            MakeMembership(4, OtherTenantId, CompanyId, OtherTenantMemberId, MembershipStatus.Active),
            MakeMembership(5, TenantId, CompanyId, RevokedMemberId, MembershipStatus.Revoked));
        await db.SaveChangesAsync();
        return db;
    }

    private static (ServiceProvider Services, DefaultHttpContext Http) NewHttpContext()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        return (services, http);
    }

    private static string ReadBody(HttpContext http) =>
        Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var (services, http) = NewHttpContext();
        using (services)
        {
            await result.ExecuteAsync(http);
            return (http.Response.StatusCode, ReadBody(http));
        }
    }

    // ===== 四种初始负责人 =====

    [Fact]
    public async Task EnsureValidOwner_NullOwner_IsAllowed()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            null, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureValidOwner_ValidActiveMember_IsAllowed()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            ActiveMemberId, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureValidOwner_CrossCompanyMember_IsRejected()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            OtherCompanyMemberId, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.NotNull(result);
        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains(CrmOwnerGuard.InvalidMemberMessage, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureValidOwner_DisabledUser_IsRejected()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            DisabledUserId, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.NotNull(result);
        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains(CrmOwnerGuard.InvalidMemberMessage, body, StringComparison.Ordinal);
    }

    // ===== 其余边界格 =====

    [Fact]
    public async Task EnsureValidOwner_OtherTenantMember_IsRejected()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            OtherTenantMemberId, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.NotNull(result);
        var (status, _) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task EnsureValidOwner_RevokedMembership_IsRejected()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            RevokedMemberId, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.NotNull(result);
        var (status, _) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task EnsureValidOwner_UnknownUser_IsRejected()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            UnknownUserId, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.NotNull(result);
        var (status, _) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task EnsureValidOwner_WithoutTenantCompanyScope_IsForbidden()
    {
        using var db = await SeedAsync();

        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            ActiveMemberId, db, new StubTenant(null), new StubCompany(null), CancellationToken.None);

        Assert.NotNull(result);
        Assert.IsType<ForbidHttpResult>(result);
    }

    // ===== 团队成员列表与单成员判定同源 =====

    [Fact]
    public async Task ListActiveCompanyMembers_ContainsOnlyActiveMembersOfCurrentScope()
    {
        using var db = await SeedAsync();

        var members = await CrmOwnerGuard.ListActiveCompanyMembersAsync(
            TenantId, CompanyId, db, CancellationToken.None);

        Assert.Contains(members, m => m.Id == ActiveMemberId);
        Assert.DoesNotContain(members, m => m.Id == DisabledUserId);
        Assert.DoesNotContain(members, m => m.Id == OtherCompanyMemberId);
        Assert.DoesNotContain(members, m => m.Id == OtherTenantMemberId);
        Assert.DoesNotContain(members, m => m.Id == RevokedMemberId);
    }

    // ===== 转客户：继承值同样要过校验 =====

    [Fact]
    public void ResolveEffectiveOwner_PrefersRequestedOwner()
    {
        Assert.Equal(30001, CrmOwnerRules.ResolveEffectiveOwner(30001, 30002));
    }

    [Fact]
    public void ResolveEffectiveOwner_FallsBackToInheritedOwner()
    {
        Assert.Equal(30002, CrmOwnerRules.ResolveEffectiveOwner(null, 30002));
    }

    [Fact]
    public void ResolveEffectiveOwner_WithoutAnyOwner_IsNull()
    {
        Assert.Null(CrmOwnerRules.ResolveEffectiveOwner(null, null));
    }

    [Fact]
    public async Task Convert_InheritingInvalidOldOwner_IsRejected()
    {
        // 线索原负责人已不是本公司有效成员：转客户时继承该值必须被拒绝，
        // 不能静默把无效旧 ID 写进新客户。
        using var db = await SeedAsync();

        var inheritedOwnerId = CrmOwnerRules.ResolveEffectiveOwner(null, RevokedMemberId);
        var result = await CrmOwnerGuard.EnsureValidOwnerAsync(
            inheritedOwnerId, db, new StubTenant(TenantId), new StubCompany(CompanyId), CancellationToken.None);

        Assert.NotNull(result);
        var (status, body) = await ExecuteAsync(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains(CrmOwnerGuard.InvalidMemberMessage, body, StringComparison.Ordinal);
    }

    // ===== 普通编辑的变更判定 =====

    [Theory]
    [InlineData(500L, null, false)]   // 省略 ownerId ⇒ 保持
    [InlineData(500L, 500L, false)]   // 传原值 ⇒ 保持
    [InlineData(500L, 600L, true)]    // 传新值 ⇒ 变更
    [InlineData(null, 500L, true)]    // 当前为空时传任何值 ⇒ 变更
    [InlineData(null, null, false)]   // 两边都空 ⇒ 保持
    public void IsOwnerChange_MatchesAgreedRule(long? current, long? requested, bool expected)
    {
        Assert.Equal(expected, CrmOwnerRules.IsOwnerChange(current, requested));
    }

    private sealed class StubTenant(long? id) : ICurrentTenant
    {
        public long? Id { get; } = id;
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? tenantId) => new Noop();
    }

    private sealed class StubCompany(long? id) : ICurrentCompany
    {
        public long? Id { get; } = id;
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? companyId) => new Noop();
    }

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}
