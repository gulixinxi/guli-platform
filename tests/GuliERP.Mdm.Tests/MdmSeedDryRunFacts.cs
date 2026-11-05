using GuliERP.Foundation.Kernel;
using GuliERP.Mdm.Application;
using GuliERP.Mdm.Infrastructure.Persistence;
using GuliERP.Mdm.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Mdm.Tests;

/// <summary>
/// TASK 3 第 7 项：<c>seed-mdm --dry-run</c> 曾经照样写库。
///
/// <para>
/// 缺陷面：CLI 的 <c>--dry-run</c> 从来没有传到
/// <c>IMdmMasterDataSeedService</c> / <c>IMdmNumberingRuleSeedService</c>，
/// 服务层也没有 <c>DryRun</c> 分支 ⇒ 干跑把行真写进数据库。
/// 只有 <c>ReferenceSeedService</c> 实现了真正的干跑。
/// </para>
///
/// <para>
/// 修法：关系型数据库上整次播种包进事务并回滚；非关系型 provider
/// （InMemory 等）<b>明确拒绝</b>，因为内存库的 SaveChanges 是真写入且无法回滚，
/// 此时「声称干跑却真的写库」比直接报错更危险。
/// </para>
///
/// <para>
/// 本文件覆盖 provider 边界（内存库必须拒绝）；PostgreSQL 上的
/// 「写入被回滚」由 <c>GuliERP.Mdm.IntegrationTests.MdmSeedDryRunFacts</c> 覆盖。
/// </para>
/// </summary>
public sealed class MdmSeedDryRunFacts
{
    [Fact]
    public async Task MasterData_DryRun_On_NonRelationalProvider_Is_Rejected_And_Writes_Nothing()
    {
        await using var db = CreateDb();
        var service = new MdmMasterDataSeedService(
            db,
            null!, null!, null!, null!,
            new FixedCurrentTenant(30),
            new FixedCurrentCompany(31),
            NullLogger<MdmMasterDataSeedService>.Instance);

        using var temp = new TempDirectory();

        var ex = await Assert.ThrowsAsync<MdmValidationException>(() =>
            service.SeedAllFromPathAsync(temp.Path, tenantId: 30, companyId: 31, dryRun: true));

        Assert.Equal(MdmErrorCodes.ValidationFailed, ex.Code);
        Assert.Contains("Dry-run", ex.Message, StringComparison.Ordinal);

        // Nothing may have been written — not even partially.
        Assert.Equal(0, await db.Items.CountAsync());
        Assert.Equal(0, await db.ItemCategories.CountAsync());
        Assert.Equal(0, await db.BusinessPartners.CountAsync());
        Assert.Equal(0, await db.Warehouses.CountAsync());
        Assert.Equal(0, await db.Locations.CountAsync());
    }

    [Fact]
    public async Task Numbering_DryRun_On_NonRelationalProvider_Is_Rejected_And_Writes_Nothing()
    {
        await using var db = CreateDb();
        var service = new MdmNumberingRuleSeedService(
            db,
            null!,
            new FixedCurrentTenant(30),
            new FixedCurrentCompany(31),
            new FixedCurrentUser(99),
            NullLogger<MdmNumberingRuleSeedService>.Instance);

        using var temp = new TempDirectory();

        var ex = await Assert.ThrowsAsync<MdmValidationException>(() =>
            service.SeedAllFromPathAsync(
                temp.Path, tenantId: 30, companyId: 31, includePlanned: true, dryRun: true));

        Assert.Equal(MdmErrorCodes.ValidationFailed, ex.Code);
        Assert.Equal(0, await db.NumberingRules.CountAsync());
    }

    private static MdmDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MdmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "mdm-dryrun-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class FixedCurrentTenant(long id) : ICurrentTenant
    {
        public long? Id { get; private set; } = id;
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? tenantId)
        {
            var previous = Id;
            Id = tenantId;
            return new Restore(() => Id = previous);
        }
    }

    private sealed class FixedCurrentCompany(long id) : ICurrentCompany
    {
        public long? Id { get; private set; } = id;
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? companyId)
        {
            var previous = Id;
            Id = companyId;
            return new Restore(() => Id = previous);
        }
    }

    private sealed class FixedCurrentUser(long id) : ICurrentUser
    {
        public long? Id { get; private set; } = id;
        public string? UserName => "tester";
        public bool IsAuthenticated => Id.HasValue;
        public bool IsPlatformAdmin => false;
        public IDisposable Change(long? userId)
        {
            var previous = Id;
            Id = userId;
            return new Restore(() => Id = previous);
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
