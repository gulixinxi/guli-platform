using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GuliERP.Api.Tests;

/// <summary>
/// P1-09 迁移链完整性守卫。
///
/// <para><b>它防的是什么</b>：EF Core 的迁移历史由 <c>[Migration("&lt;id&gt;")]</c>
/// 属性决定，而 <b>不是</b>文件所在目录。一旦同一个 DbContext 的迁移散落到第二个目录，
/// 或者同一个上下文出现两个 ModelSnapshot，两个事实源就会各自声称「我是这条链」——
/// EF 的 <c>has-pending-model-changes</c>、<c>migrations script</c>、
/// <c>migrations remove</c> 会按其中一个算，另一个静默失配。这类故障不报错，
/// 只在真正回滚或改模型时才炸。</para>
///
/// <para><b>为什么不是「把 CRM 的迁移搬回一个目录」</b>：搬迁会改命名空间、动 14 个
/// 文件的 git 历史，收益只是目录好看，风险与收益不成比例。本守卫改为
/// <b>把当前形态钉死</b>：任何新增/迁移目录都必须显式改这份基线，
/// 从而强制一个人来拍板，而不是让形态悄悄漂移。</para>
///
/// <para><b>为什么不用 <c>dotnet ef</c> 断言</b>：迁移 CLI 需要数据库连接或
/// 设计时工厂，且版本随 SDK 变化；这里只读源码，不连库、不写库，
/// 能在任何 CI 环境（含离线）稳定跑。</para>
/// </summary>
public sealed class MigrationChainIntegrityFacts
{
    /// <summary>
    /// 每个 DbContext 允许出现的迁移目录集合（仓库相对路径，正斜杠）。
    ///
    /// <para><b>这份基线是「已接受的现状」，不是「理想形态」</b>：
    /// CRM 的 31 个迁移目前分两个目录（24 个在 Migrations/，7 个在
    /// Persistence/Migrations/）。实测 EF 把它们当成一条连贯的 31 步链
    /// （<c>dotnet ef migrations list</c>，首尾相接、无分叉），
    /// 且 7 个上下文 <c>has-pending-model-changes</c> 全部 in-sync，
    /// 所以 <b>现状不构成上线阻塞</b>，但它必须被看见并被钉住。</para>
    ///
    /// <para>改动这份表 = 承认一次迁移目录重组，那需要同时复核
    /// <c>dotnet ef migrations list</c> 与 <c>has-pending-model-changes</c>。</para>
    /// </summary>
    private static readonly Dictionary<string, string[]> ExpectedMigrationDirectories = new(StringComparer.Ordinal)
    {
        ["CrmDbContext"] = new[]
        {
            "modules/crm/GuliERP.Crm.Infrastructure/Migrations",
            "modules/crm/GuliERP.Crm.Infrastructure/Persistence/Migrations",
        },
        ["DocumentKernelDbContext"] = new[]
        {
            "modules/document-kernel/GuliERP.DocumentKernel.Infrastructure/Migrations",
        },
        ["FoundationDbContext"] = new[]
        {
            "modules/foundation/GuliERP.Foundation/Migrations",
        },
        ["IdentityDbContext"] = new[]
        {
            "modules/identity/GuliERP.Identity.Infrastructure/Migrations",
        },
        ["MdmDbContext"] = new[]
        {
            "modules/mdm/GuliERP.Mdm.Infrastructure/Migrations",
        },
        ["PurchaseDbContext"] = new[]
        {
            "modules/purchase/GuliERP.Purchase.Infrastructure/Migrations",
        },
        ["SalesDbContext"] = new[]
        {
            "modules/sales/GuliERP.Sales.Infrastructure/Migrations",
        },
    };

    [Fact]
    public void EveryDbContext_HasExactlyOneModelSnapshot()
    {
        var snapshots = DiscoverSnapshots();

        foreach (var group in snapshots.GroupBy(s => s.ContextName, StringComparer.Ordinal))
        {
            Assert.True(
                group.Count() == 1,
                $"{group.Key} has {group.Count()} ModelSnapshot files: " +
                string.Join(", ", group.Select(s => s.RelativePath)) +
                ". A second snapshot means two sources of truth for the same model.");
        }

        // The context set must not silently shrink: a context that loses its
        // snapshot (or is renamed) must fail here instead of at deploy time.
        Assert.Equal(
            ExpectedMigrationDirectories.Keys.OrderBy(k => k, StringComparer.Ordinal),
            snapshots.Select(s => s.ContextName).Distinct().OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryDbContext_MigrationsLiveInAKnownDirectorySet()
    {
        var migrations = DiscoverMigrations();

        foreach (var group in migrations.GroupBy(m => m.ContextName, StringComparer.Ordinal))
        {
            var actual = group.Select(m => m.Directory).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToArray();
            var expected = ExpectedMigrationDirectories[group.Key];
            Assert.True(
                actual.SequenceEqual(expected, StringComparer.Ordinal),
                $"{group.Key} migration directories changed.\n  expected: {string.Join(" | ", expected)}\n  actual:   {string.Join(" | ", actual)}\n" +
                "Moving a migration is not a free action: EF keys history on the [Migration] attribute, " +
                "so a directory move must be re-verified with 'dotnet ef migrations list' and " +
                "'dotnet ef migrations has-pending-model-changes' before this baseline is updated.");
        }
    }

    [Fact]
    public void MigrationIds_AreUniqueWithinTheirContext()
    {
        var migrations = DiscoverMigrations();

        foreach (var group in migrations.GroupBy(m => m.ContextName, StringComparer.Ordinal))
        {
            var duplicates = group
                .GroupBy(m => m.MigrationId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();
            Assert.True(
                duplicates.Length == 0,
                $"{group.Key} has duplicate migration ids: {string.Join(", ", duplicates)}.");
        }
    }

    [Fact]
    public void MigrationIdPrefix_MatchesTheFileNamePrefix()
    {
        var migrations = DiscoverMigrations();
        var mismatches = migrations
            .Where(m => !string.Equals(m.FileNamePrefix, m.MigrationIdPrefix, StringComparison.Ordinal))
            .Select(m => $"{m.RelativePath} declares {m.MigrationId}")
            .ToArray();

        Assert.True(
            mismatches.Length == 0,
            "migration file name and [Migration] id disagree:\n  " + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void EveryMigration_HasItsDesignerAndEveryDesignerHasItsMigration()
    {
        var migrations = DiscoverMigrations();

        var orphanMigrations = migrations
            .Where(m => !m.HasDesigner)
            .Select(m => m.RelativePath)
            .ToArray();
        Assert.True(
            orphanMigrations.Length == 0,
            "migration without its .Designer.cs (the model snapshot for that step):\n  " +
            string.Join("\n  ", orphanMigrations));

        var orphanDesigners = migrations
            .Where(m => !m.HasMigrationFile)
            .Select(m => m.RelativePath)
            .ToArray();
        Assert.True(
            orphanDesigners.Length == 0,
            "designer without its migration file:\n  " + string.Join("\n  ", orphanDesigners));
    }

    [Fact]
    public void MigrationTimestamps_AreUniqueWithinTheirContext()
    {
        var migrations = DiscoverMigrations();

        foreach (var group in migrations.GroupBy(m => m.ContextName, StringComparer.Ordinal))
        {
            var duplicates = group
                .GroupBy(m => m.FileNamePrefix, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();
            Assert.True(
                duplicates.Length == 0,
                $"{group.Key} has two migrations with the same timestamp prefix: {string.Join(", ", duplicates)}. " +
                "EF orders by timestamp; a tie makes the chain order ambiguous.");
        }
    }

    [Fact]
    public void ModelSnapshot_SharesADirectoryWithItsContextsMigrations()
    {
        var snapshots = DiscoverSnapshots();
        var migrations = DiscoverMigrations();

        foreach (var snapshot in snapshots)
        {
            var dirs = migrations
                .Where(m => string.Equals(m.ContextName, snapshot.ContextName, StringComparison.Ordinal))
                .Select(m => m.Directory)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Assert.True(
                ((IEnumerable<string>)dirs).Contains(snapshot.Directory, StringComparer.Ordinal),
                $"{snapshot.ContextName} snapshot lives in '{snapshot.Directory}' which is not one of its migration directories " +
                $"({string.Join(", ", dirs)}). The snapshot must sit with the chain it describes.");
        }
    }

    // ---------------------------------------------------------------------
    // discovery helpers
    // ---------------------------------------------------------------------

    private sealed record Snapshot(string ContextName, string RelativePath, string Directory);

    private sealed record Migration(
        string ContextName,
        string MigrationId,
        string RelativePath,
        string Directory,
        string FileNamePrefix,
        string MigrationIdPrefix,
        bool HasDesigner,
        bool HasMigrationFile);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "modules"))
                && Directory.Exists(Path.Combine(dir.FullName, "apps")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "repo root not found: no modules/ + apps/ above the test assembly.");
    }

    private static IEnumerable<string> MigrationDirectories()
    {
        var modules = Path.Combine(RepoRoot(), "modules");
        return Directory.EnumerateDirectories(modules, "Migrations", SearchOption.AllDirectories);
    }

    private static IReadOnlyList<Snapshot> DiscoverSnapshots()
    {
        var list = new List<Snapshot>();
        foreach (var dir in MigrationDirectories())
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*ModelSnapshot.cs"))
            {
                var text = File.ReadAllText(file);
                var context = ReadContext(text);
                if (context is null) continue;
                list.Add(new Snapshot(context, Rel(file), RelDir(file)));
            }
        }

        return list;
    }

    private static IReadOnlyList<Migration> DiscoverMigrations()
    {
        var list = new List<Migration>();

        foreach (var dir in MigrationDirectories())
        {
            foreach (var designer in Directory.EnumerateFiles(dir, "*.Designer.cs"))
            {
                if (designer.EndsWith("ModelSnapshot.Designer.cs", StringComparison.Ordinal)) continue;

                var text = File.ReadAllText(designer);
                var context = ReadContext(text);
                var migrationId = ReadMigrationId(text);
                if (context is null || migrationId is null) continue;

                var fileName = Path.GetFileName(designer);
                var baseName = fileName[..^".Designer.cs".Length];
                var mainFile = Path.Combine(dir, baseName + ".cs");

                list.Add(new Migration(
                    context,
                    migrationId,
                    Rel(designer),
                    RelDir(designer),
                    FileNamePrefix(baseName),
                    MigrationIdPrefix(migrationId),
                    HasDesigner: true,
                    HasMigrationFile: File.Exists(mainFile)));
            }

            // Main migration files without a designer are the failure this
            // guard exists for, so they must be visible as records too.
            foreach (var main in Directory.EnumerateFiles(dir, "*.cs"))
            {
                var name = Path.GetFileName(main);
                if (name.EndsWith(".Designer.cs", StringComparison.Ordinal)) continue;
                if (name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)) continue;

                var designerPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(main) + ".Designer.cs");
                if (File.Exists(designerPath)) continue;

                var text = File.ReadAllText(main);
                var context = ReadContext(text) ?? "(unknown)";
                list.Add(new Migration(
                    context,
                    MigrationId: "(none)",
                    Rel(main),
                    RelDir(main),
                    FileNamePrefix(Path.GetFileNameWithoutExtension(main)),
                    "(none)",
                    HasDesigner: false,
                    HasMigrationFile: true));
            }
        }

        return list;
    }

    private static string? ReadContext(string text) =>
        Regex.Match(text, @"DbContext\(typeof\((\w+)\)\)").Groups[1].Success
            ? Regex.Match(text, @"DbContext\(typeof\((\w+)\)\)").Groups[1].Value
            : null;

    private static string? ReadMigrationId(string text)
    {
        var m = Regex.Match(text, @"Migration\(""([^""]+)""\)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string FileNamePrefix(string baseName) =>
        Regex.Match(baseName, @"^(\d{14})_").Groups[1].Success
            ? Regex.Match(baseName, @"^(\d{14})_").Groups[1].Value
            : "(none)";

    private static string MigrationIdPrefix(string migrationId) =>
        Regex.Match(migrationId, @"^(\d{14})_").Groups[1].Success
            ? Regex.Match(migrationId, @"^(\d{14})_").Groups[1].Value
            : "(none)";

    private static string Rel(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string RelDir(string path) =>
        Path.GetRelativePath(RepoRoot(), Path.GetDirectoryName(path)!).Replace('\\', '/');
}
