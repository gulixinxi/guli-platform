using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace GuliERP.Api.Tests;

/// <summary>
/// CRM 枚举标签一致性守卫（PRD 阶段 A「修复信号/商机状态枚举显示」）。
///
/// <para><b>这个缺陷是真实发生过的，不是假想</b>：修复前每个 CRM 页面各自内联一份
/// 标签数组，于是同一个 <c>BusinessCategory.DouyinAddress</c> 在 DealList.vue 显示
/// 「抖音地址」、在 CompanyDetail.vue 显示「短视频地址」；CompanyDetail.vue 的数组只
/// 写到下标 7，导致 <c>SocialMedia(8)</c> 和 <c>ForeignTradeLeads(9)</c> 静默落到
/// 「其他」兜底 —— 用户看到一个自媒体商机被标成「其他」。</para>
///
/// <para><b>为什么用源码断言而不是运行时测试</b>：前端没有测试运行器（package.json
/// 只有 dev/build/typecheck），标签又没有任何行为断言能覆盖。这里沿用仓库已有的
/// <c>*SourceFacts</c> 模式，用源码契约锁住——代价是路径耦合，收益是这类漂移下次
/// 会立刻变红。</para>
/// </summary>
public sealed class CrmEnumLabelContractFacts
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "apps", "web", "src", "utils", "crmEnum.ts")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "repo root not found: no apps/web/src/utils/crmEnum.ts above the test assembly");
    }

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));

    private static IEnumerable<string> CrmViews() =>
        Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "apps", "web", "src", "views", "crm"), "*.vue")
            .Select(Path.GetFileName)
            .Select(n => n!);

    /// <summary>
    /// Drops block comments and // line comments before scanning.
    /// Without this the guard matches the explanatory comments that
    /// document the very defect it is checking for.
    /// (Caveat: a // inside a string literal is also stripped — none of
    /// the scanned files carry URL literals with one.)
    /// </summary>
    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return string.Join("\n", src.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));
    }

    // ----------------------------------------------------------------

    [Fact]
    public void EveryBusinessCategoryMember_HasALabel()
    {
        // 绑定到 types/crm.ts 的枚举定义本身：枚举加成员而字典没跟上，这里立刻红。
        var enums = Read("apps", "web", "src", "types", "crm.ts");
        var labels = Read("apps", "web", "src", "utils", "crmEnum.ts");

        var block = Regex.Match(enums, @"export enum BusinessCategory\s*\{(?<body>.*?)\}", RegexOptions.Singleline);
        Assert.True(block.Success, "BusinessCategory enum not found in types/crm.ts");

        var members = Regex.Matches(block.Groups["body"].Value, @"^\s*(\w+)\s*=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(members.Count >= 11, $"expected >=11 BusinessCategory members, saw {members.Count}");

        var missing = members
            .Where(m => !labels.Contains($"[BusinessCategory.{m}]"))
            .ToList();
        Assert.True(missing.Count == 0,
            $"businessCategoryLabels is missing: {string.Join(", ", missing)}. " +
            "Add the label to crmEnum.ts — do NOT add a local array in the view.");
    }

    [Fact]
    public void CrmViews_DoNotDeclareInlineEnumLabelArrays()
    {
        // Scans EVERY view so a new page cannot reintroduce a local map.
        //
        // Shape 1: a Chinese label array indexed by a number. The real
        // occurrences were `return ['待联系', ...][crmEnumNumber(...)]`,
        // `... ? '其他' : ['企业建站', ...][value]` and
        // `const map: Record<number,string> = {...}` — note the first two
        // have NO `=` before the `[`, so a pattern anchored on `= [`
        // misses exactly the defect it was written to catch.
        // Requiring a CJK literal keeps plain colour arrays like
        // `['warning','success',...][crmEnumNumber(...)]` out of scope.
        const string labelArray = @"\[[^\]]*'[^']*[\u4e00-\u9fff][^']*'\s*\]\s*\[";
        const string positionalRecord = @"Record<\s*number\s*,\s*string\s*>\s*=\s*\{";

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot(), "apps", "web", "src", "views", "crm"), "*.vue"))
        {
            var name = Path.GetFileName(file);
            var src = StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(src, labelArray)) offenders.Add($"{name}(内联中文标签数组)");
            if (Regex.IsMatch(src, positionalRecord)) offenders.Add($"{name}(Record<number,string> 位置字典)");
        }

        Assert.True(offenders.Count == 0,
            "内联枚举标签会各自漂移——历史上同一个 DouyinAddress 在两个页面显示不同中文，" +
            "短数组还把 8/9 静默吃成「其他」。请改用 utils/crmEnum.ts 的共享字典。命中：" +
            string.Join("; ", offenders));
    }

    [Fact]
    public void DouyinAddress_HasOnlyOneSpellingAcrossTheWebApp()
    {
        // 后端 CrmEnums.cs 的注释是「抖音/快手地址添加」，所以「抖音地址」是规范写法。
        var webSrc = Path.Combine(RepoRoot(), "apps", "web", "src");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(webSrc, "*.ts", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(webSrc, "*.vue", SearchOption.AllDirectories)))
        {
            if (StripComments(File.ReadAllText(file)).Contains("短视频地址")) offenders.Add(file);
        }
        Assert.True(offenders.Count == 0,
            $"「短视频地址」是错误的品类写法，改用共享字典 crmEnum.ts 的「抖音地址」: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void OpportunitySuggestions_SurfacesProductCategoryAndUsesSharedLabels()
    {
        // productCategory 后端一直在算，页面从来没显示过 —— 销售看不到建议对应哪个品类。
        var src = Read("apps", "web", "src", "views", "crm", "OpportunitySuggestions.vue");
        Assert.Contains("productCategory", src);
        Assert.Contains("businessCategoryLabel", src);
        Assert.Contains("assessmentStateLabel", src);
    }

    [Fact]
    public void SuggestionEndpoint_SetsAssessmentStatusOnEveryRuleBranch()
    {
        // 修复前 8 条规则里只有 3 条设了 assessmentStatus，前端靠 || '待核验' 兜住。
        // 现在每条分支都必须显式给，且统一用线值词汇 pending。
        var src = Read("apps", "api", "GuliERP.Api", "Crm", "CrmEndpoints.cs");
        var blocks = Regex.Matches(src, @"suggestions\.Add\(new\s*\{(?<body>.*?)\}\s*\)", RegexOptions.Singleline);
        Assert.True(blocks.Count >= 8, $"expected >=8 suggestion branches, saw {blocks.Count}");

        var missing = blocks
            .Select(b => b.Groups["body"].Value)
            .Where(body => !body.Contains("assessmentStatus"))
            .ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} suggestion branch(es) omit assessmentStatus. Every rule must state it explicitly.");

        Assert.DoesNotContain(@"assessmentStatus = ""待核验""", src);
        Assert.Equal(blocks.Count, Regex.Matches(src, @"assessmentStatus = ""pending""").Count);
    }
}