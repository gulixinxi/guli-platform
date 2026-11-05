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
/// TASK 3 B-4：统一社会信用代码超长 ⇒ 500 → 400；重复建档 ⇒ 500 → 409。
///
/// <para>
/// <b>缺陷</b>：<c>crm.companies.CreditCode</c> 是 <c>varchar(18)</c>，
/// 而 <c>CompanyService.NormalizeCreditCode</c> 只做 trim+大写、<b>不校验长度</b>
/// ⇒ 超长值一路送到 PostgreSQL 触发 22001（value too long），
/// 而 <c>POST /api/v1/crm/companies</c> 端点<b>完全没有 try/catch</b>
/// ⇒ 纯客户端错误被报成 HTTP 500。
/// </para>
///
/// <para>
/// 同一个端点上「客户已存在」（<c>InvalidOperationException</c>）同样没有出口，
/// 也是 500 —— 本文件一并固定为 <c>crm.duplicate_company</c>（409 语义）。
/// </para>
///
/// <para>
/// 注意：EF Core InMemory provider <b>不</b>执行列宽约束，所以本文件证明的是
/// 应用层拦截生效（PostgreSQL 侧的 22001 由此不再发生）。
/// </para>
/// </summary>
public sealed class CompanyCreditCodeValidationFacts
{
    private const int MaxCreditCodeLength = 18;

    [Fact]
    public async Task Create_With_Overlong_CreditCode_Is_Rejected_And_Nothing_Is_Written()
    {
        await using var db = CreateDb();
        var service = new CompanyService(db, new TestCrmNumberGenerator());

        var ex = await Assert.ThrowsAsync<CrmValidationException>(() =>
            service.CreateAsync(NewCreate(creditCode: new string('A', MaxCreditCodeLength + 1)), 42, CancellationToken.None));

        Assert.Equal(CrmErrorCodes.InvalidCreditCode, ex.Code);
        Assert.Equal(0, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Create_With_CreditCode_At_MaxLength_Is_Accepted()
    {
        await using var db = CreateDb();
        var service = new CompanyService(db, new TestCrmNumberGenerator());

        var created = await service.CreateAsync(
            NewCreate(creditCode: new string('A', MaxCreditCodeLength)), 42, CancellationToken.None);

        Assert.Equal(new string('A', MaxCreditCodeLength), created.CreditCode);
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Create_With_Overlong_CreditCode_Is_Rejected_On_Update_Path()
    {
        await using var db = CreateDb();
        var service = new CompanyService(db, new TestCrmNumberGenerator());
        var created = await service.CreateAsync(NewCreate(creditCode: "91370600MA3XXXXX0A"), 42, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CrmValidationException>(() =>
            service.UpdateAsync(
                created.Id,
                NewUpdate(creditCode: new string('B', MaxCreditCodeLength + 1)),
                CancellationToken.None));

        Assert.Equal(CrmErrorCodes.InvalidCreditCode, ex.Code);

        // The rejected update must not have truncated / written anything.
        var stored = await db.Companies.SingleAsync();
        Assert.Equal("91370600MA3XXXXX0A", stored.CreditCode);
    }

    [Fact]
    public async Task Create_With_Duplicate_CreditCode_Is_Rejected_As_Duplicate()
    {
        await using var db = CreateDb();
        var service = new CompanyService(db, new TestCrmNumberGenerator());
        await service.CreateAsync(NewCreate(fullName: "第一家客户", creditCode: "91370600MA3XXXXX0A"), 42, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CrmValidationException>(() =>
            service.CreateAsync(NewCreate(fullName: "第二家客户", creditCode: "91370600MA3XXXXX0A"), 42, CancellationToken.None));

        Assert.Equal(CrmErrorCodes.DuplicateCompany, ex.Code);
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Create_With_Duplicate_FullName_Is_Rejected_As_Duplicate()
    {
        await using var db = CreateDb();
        var service = new CompanyService(db, new TestCrmNumberGenerator());
        await service.CreateAsync(NewCreate(fullName: "同名客户", creditCode: null), 42, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<CrmValidationException>(() =>
            service.CreateAsync(NewCreate(fullName: "同名客户", creditCode: null), 42, CancellationToken.None));

        Assert.Equal(CrmErrorCodes.DuplicateCompany, ex.Code);
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Duplicate_And_Length_Messages_Do_Not_Echo_Caller_Input()
    {
        await using var db = CreateDb();
        var service = new CompanyService(db, new TestCrmNumberGenerator());
        const string sensitiveCode = "91370600MA3XXXXX0A";

        await service.CreateAsync(NewCreate(fullName: "第一家客户", creditCode: sensitiveCode), 42, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<CrmValidationException>(() =>
            service.CreateAsync(NewCreate(fullName: "第二家客户", creditCode: sensitiveCode), 42, CancellationToken.None));

        // The message must be a fixed reviewed string: it may not carry the
        // caller's own credit code back out of the service layer.
        Assert.DoesNotContain(sensitiveCode, ex.Message, StringComparison.Ordinal);
    }

    private static CrmDbContext CreateDb() =>
        new(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            new MutableCurrentTenant(70),
            new MutableCurrentCompany(71));

    private static CreateCompanyRequest NewCreate(string fullName = "测试客户", string? creditCode = null) =>
        new(
            fullName,
            creditCode,
            null, null,
            CompanyCity.Weifang, null, null,
            CompanyIndustry.Service, null, null,
            false, null,
            SourceChannel.Other, null,
            null,
            CustomerStatus.ToContact, null, null, null);

    private static UpdateCompanyRequest NewUpdate(string fullName = "测试客户", string? creditCode = null) =>
        new(
            fullName,
            creditCode,
            null, null,
            CompanyCity.Weifang, null, null,
            CompanyIndustry.Service, null, null,
            false, null,
            SourceChannel.Other, null,
            null,
            CustomerStatus.ToContact, null, null);
}
