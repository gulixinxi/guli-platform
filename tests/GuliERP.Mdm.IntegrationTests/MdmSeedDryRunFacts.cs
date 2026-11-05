using GuliERP.Foundation.Kernel;
using GuliERP.Mdm.Application;
using GuliERP.Mdm.Infrastructure;
using GuliERP.Mdm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Mdm.IntegrationTests;

/// <summary>
/// TASK 3 第 7 项：<c>seed-mdm --dry-run</c> 曾经照样写库。
///
/// <para>
/// 缺陷面：CLI 的 <c>--dry-run</c> 从来没有传到
/// <c>IMdmMasterDataSeedService</c> / <c>IMdmNumberingRuleSeedService</c>，
/// 服务层也没有 <c>DryRun</c> 分支 ⇒ 干跑把行真写进数据库。
/// </para>
///
/// <para>
/// 修法：整次播种包进一个事务并在结束时回滚（关系型库）。
/// </para>
///
/// <para>
/// <b>断言形态</b>：本文件刻意采用「<i>摘要说建了 N 行，库里却查不到</i>」
/// 的形状 —— 既证明写入<b>确实发生过</b>（摘要非 0），又证明它<b>确实被撤销</b>
/// （库里为 0）。只断言「库里为 0」是空洞的：本来就没东西可写时它也会通过。
/// </para>
///
/// <para>
/// 需要真实 PostgreSQL：连接串来自 <c>ConnectionStrings__GuliERP</c>
/// （或 <c>GULIERP_ConnectionStrings__GuliERP</c>）环境变量；未提供则跳过。
/// </para>
/// </summary>
[Collection(MdmPostgresIntegrationCollection.Name)]
public sealed class MdmSeedDryRunFacts
{
    private const string NumberingSeedMarker = "data/bootstrap/reference/mdm/numbering/document-numbering.json";
    private const string MasterDataSeedMarker = "data/bootstrap/reference/mdm/masterdata/item-category.json";

    [SkippableFact]
    public async Task Numbering_DryRun_Creates_Nothing_But_Reports_What_Would_Be_Created()
    {
        var connStr = RequireConnectionString();
        Skip.If(string.IsNullOrWhiteSpace(connStr), "ConnectionStrings__GuliERP not set.");

        var seedDir = ResolveRepoPath(Path.GetDirectoryName(NumberingSeedMarker)!);
        Skip.If(seedDir is null, "MDM numbering seed directory not found relative to the test binary.");

        var tenantId = 91_000_000L + Random.Shared.Next(1, 900_000);
        var companyId = 92_000_000L + Random.Shared.Next(1, 900_000);

        await using var provider = BuildProvider(connStr);

        var before = await CountRulesAsync(provider, tenantId);
        Assert.Equal(0, before);

        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IMdmNumberingRuleSeedService>();
            var summary = await service.SeedAllFromPathAsync(
                seedDir!, tenantId, companyId, includePlanned: true, dryRun: true);

            // The seed really did run and really did insert inside the
            // transaction — otherwise this assertion would be vacuous.
            Assert.True(summary.ItemsCreated > 0,
                $"Expected the dry-run to report created rows, got {summary.ItemsCreated}.");
        }

        var after = await CountRulesAsync(provider, tenantId);
        Assert.Equal(before, after);
    }

    [SkippableFact]
    public async Task MasterData_DryRun_Creates_Nothing_But_Reports_What_Would_Be_Created()
    {
        var connStr = RequireConnectionString();
        Skip.If(string.IsNullOrWhiteSpace(connStr), "ConnectionStrings__GuliERP not set.");

        var seedDir = ResolveRepoPath(Path.GetDirectoryName(MasterDataSeedMarker)!);
        Skip.If(seedDir is null, "MDM masterdata seed directory not found relative to the test binary.");

        var tenantId = 93_000_000L + Random.Shared.Next(1, 900_000);
        var companyId = 94_000_000L + Random.Shared.Next(1, 900_000);

        await using var provider = BuildProvider(connStr);

        var before = await CountCategoriesAsync(provider, tenantId);
        Assert.Equal(0, before);

        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IMdmMasterDataSeedService>();
            var summary = await service.SeedAllFromPathAsync(
                seedDir!, tenantId, companyId, dryRun: true);

            // ItemCategory has no FK prerequisite, so the dry-run must have
            // inserted rows inside the transaction before rolling back.
            Assert.True(summary.ItemCategoriesCreated > 0,
                $"Expected the dry-run to report created ItemCategory rows, got {summary.ItemCategoriesCreated}.");
        }

        var after = await CountCategoriesAsync(provider, tenantId);
        Assert.Equal(before, after);
    }

    private static string RequireConnectionString()
    {
        var conn = Environment.GetEnvironmentVariable("ConnectionStrings__GuliERP");
        if (string.IsNullOrWhiteSpace(conn))
        {
            conn = Environment.GetEnvironmentVariable("GULIERP_ConnectionStrings__GuliERP");
        }
        return conn ?? string.Empty;
    }

    private static ServiceProvider BuildProvider(string connStr)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGuliErpMdm(connStr);
        services.AddSingleton<ICurrentTenant, FixedCurrentTenant>();
        services.AddSingleton<ICurrentCompany, FixedCurrentCompany>();
        services.AddSingleton<ICurrentUser, FixedCurrentUser>();
        return services.BuildServiceProvider();
    }

    private static async Task<int> CountRulesAsync(ServiceProvider provider, long tenantId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MdmDbContext>();
        return await db.NumberingRules.AsNoTracking()
            .CountAsync(x => x.TenantId == tenantId);
    }

    private static async Task<int> CountCategoriesAsync(ServiceProvider provider, long tenantId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MdmDbContext>();
        return await db.ItemCategories.AsNoTracking()
            .CountAsync(x => x.TenantId == tenantId);
    }

    /// <summary>
    /// Walk up from the test binary until the repo-root-relative marker is
    /// found (same strategy as <c>MdmSeed.ResolveSeedFilePath</c>).
    /// </summary>
    private static string? ResolveRepoPath(string relativeMarker)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativeMarker);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed class FixedCurrentTenant : ICurrentTenant
    {
        public long? Id { get; private set; }
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? tenantId)
        {
            var previous = Id;
            Id = tenantId;
            return new Restore(() => Id = previous);
        }
    }

    private sealed class FixedCurrentCompany : ICurrentCompany
    {
        public long? Id { get; private set; }
        public string? Name => null;
        public bool IsAvailable => Id.HasValue;
        public IDisposable Change(long? companyId)
        {
            var previous = Id;
            Id = companyId;
            return new Restore(() => Id = previous);
        }
    }

    private sealed class FixedCurrentUser : ICurrentUser
    {
        public long? Id { get; private set; } = 99;
        public string? UserName => "integration-tester";
        public bool IsAuthenticated => true;
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
