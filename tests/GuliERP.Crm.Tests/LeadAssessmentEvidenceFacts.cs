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
/// 阶段 B1：需求评估证据从「一段自由文本」改成「结构化证据行」。
///
/// <para><b>契约变更（破坏性）</b>：<c>potential</c> / <c>confirmed</c> 过去只要求
/// <c>evidence</c> 字符串非空，现在要求至少一条 <see cref="EvidenceItemInput"/>。
/// 未升级的旧客户端提交 confirmed 会拿到 400 —— 这是有意的：旧契约允许
/// 「已确认需求，依据：客户说想做」这种无法回溯的结论。</para>
///
/// <para>测试数据不是编造的场景：<c>legacy_text</c> 那一组对应的是本次迁移
/// （20261022062607）在真实 P0 库上回填出来的 13 行，见 DbDiag 的
/// 「backfill: expected vs legacy_text」审计行。</para>
/// </summary>
public class LeadAssessmentEvidenceFacts
{
    private static CrmDbContext CreateDb()
        => new(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
            new MutableCurrentTenant(40), new MutableCurrentCompany(41));

    private static async Task<long> SeedLeadAsync(CrmDbContext db)
    {
        var lead = new Lead
        {
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            CompanyName = "证据测试企业",
            Source = SourceChannel.Other,
            Status = LeadStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead.Id;
    }

    private static EvidenceItemInput Ev(
        string type = "website", string verification = "verified",
        string? url = "https://example.com", string? note = "首页可访问") => new(type, url, null, verification, note);

    private static LeadDemandAssessmentInput In(
        string state, EvidenceItemInput[]? items, string? evidence = null) =>
        new(state, "medium", evidence, "理由", "下一步", items);

    // ---- 证据必填规则 --------------------------------------------------

    [Theory]
    [InlineData("potential")]
    [InlineData("confirmed")]
    public async Task PotentialOrConfirmed_WithoutStructuredEvidence_IsRejected(string state)
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(leadId, "website", In(state, null), reviewerId: 7, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(leadId, "website", In(state, []), reviewerId: 7, CancellationToken.None));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("pending")]
    [InlineData("not_applicable")]
    public async Task NonClaimingStates_MaySaveWithoutEvidence(string state)
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        var result = await svc.SaveAsync(leadId, "website", In(state, null), reviewerId: 7, CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task LegacyTextAlone_DoesNotSatisfyTheEvidenceRule()
    {
        // The old free-text column must NOT silently keep satisfying the new
        // rule — that would let the untraceable path stay open after the fix.
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(leadId, "website",
                In("confirmed", null, evidence: "客户说想做外贸"), reviewerId: 7, CancellationToken.None));
    }

    // ---- 词表封闭性 ----------------------------------------------------

    [Fact]
    public async Task UnknownEvidenceType_IsRejected()
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(leadId, "website",
                new LeadDemandAssessmentInput("pending", null, null, null, null,
                    [Ev(type: "我随便编的类型")]), reviewerId: 7, CancellationToken.None));
        Assert.Contains("证据类型", ex.Message);
    }

    [Fact]
    public async Task UnknownVerificationStatus_IsRejected()
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(leadId, "website",
                In("pending", [Ev(verification: "looks_fine_to_me")]), reviewerId: 7, CancellationToken.None));
        Assert.Contains("核验状态", ex.Message);
    }

    [Fact]
    public async Task EvidenceWithNeitherUrlNorNote_IsRejected()
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(leadId, "website",
                In("pending", [Ev(url: null, note: null)]), reviewerId: 7, CancellationToken.None));
    }

    [Fact]
    public async Task TextOnlyEvidence_IsAllowed()
    {
        // A phone call is evidence. What is NOT allowed is an empty row.
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        var result = await svc.SaveAsync(leadId, "geo",
            In("potential", [Ev(type: "manual", url: null, note: "电话沟通，客户提到在做海外投放")]),
            reviewerId: 7, CancellationToken.None);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("not_found")]
    [InlineData("confirmed_absent")]
    public async Task NotFoundAndConfirmedAbsent_AreDistinctAcceptableStates(string status)
    {
        // 「未查到」和「确认不存在」必须都能表达，且是不同取值。
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        var result = await svc.SaveAsync(leadId, "website",
            In("pending", [Ev(verification: status)]), reviewerId: 7, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotEqual(LeadEvidenceVerifications.NotFound, LeadEvidenceVerifications.ConfirmedAbsent);
    }

    // ---- 持久化与版本 --------------------------------------------------

    [Fact]
    public async Task SavedEvidence_RoundTripsThroughGetAsync()
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        await svc.SaveAsync(leadId, "website",
            In("confirmed", [Ev(), Ev(type: "icp", url: "https://beian.miit.gov.cn", note: "已备案")]),
            reviewerId: 7, CancellationToken.None);

        var rows = db.LeadAssessmentEvidence.Where(e => e.LeadId == leadId).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, e =>
        {
            Assert.Equal(leadId, e.LeadId);
            Assert.Equal(BusinessCategory.WebsiteBuild, e.Category);
            Assert.Equal(1, e.Version);
        });
        Assert.Contains(rows, e => e.EvidenceType == "icp" && e.Url == "https://beian.miit.gov.cn");
    }

    [Fact]
    public async Task Resaving_ReplacesEvidenceInsteadOfAccumulating()
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        await svc.SaveAsync(leadId, "website", In("pending", [Ev(), Ev(type: "geo", note: "GEO 低分")]),
            reviewerId: 7, CancellationToken.None);
        await svc.SaveAsync(leadId, "website", In("confirmed", [Ev(note: "改判依据")]),
            reviewerId: 8, CancellationToken.None);

        var rows = db.LeadAssessmentEvidence.Where(e => e.LeadId == leadId).ToList();
        // 2 条 → 1 条，不是 3 条。留旧行会让「这个版本当时依据了什么」无法回答。
        Assert.Single(rows);
        Assert.Equal("改判依据", rows[0].Note);
        Assert.Equal(2, rows[0].Version);
    }

    [Fact]
    public async Task Resave_ClearsTheDeprecatedFreeTextColumn()
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        db.LeadDemandAssessments.Add(new LeadDemandAssessment
        {
            LeadId = leadId, Category = BusinessCategory.WebsiteBuild, State = "pending", Evidence = "旧的历史文本"
        });
        await db.SaveChangesAsync();

        await svc.SaveAsync(leadId, "website", In("pending", [Ev()]), reviewerId: 8, CancellationToken.None);

        var record = await db.LeadDemandAssessments.FirstAsync(a => a.LeadId == leadId && a.Category == BusinessCategory.WebsiteBuild);
        Assert.Null(record.Evidence);
        // 新真相在证据表里，不是被抹掉。
        Assert.Single(db.LeadAssessmentEvidence.Where(e => e.LeadId == leadId));
    }

    [Fact]
    public async Task BackfilledLegacyText_IsReadableAsEvidence()
    {
        // 迁移回填出来的行必须能被现有读取路径看到，否则历史评估在新结构下「消失」了。
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);
        await svc.SaveAsync(leadId, "website", In("pending", null), reviewerId: 7, CancellationToken.None);

        var assessment = await db.LeadDemandAssessments.FirstAsync(a => a.LeadId == leadId);
        db.LeadAssessmentEvidence.Add(new LeadAssessmentEvidence
        {
            LeadId = leadId,
            AssessmentId = assessment.Id,
            Category = BusinessCategory.WebsiteBuild,
            Version = assessment.Version,
            EvidenceType = LeadEvidenceTypes.LegacyText,
            VerificationStatus = LeadEvidenceVerifications.NotVerified,
            Note = "客户说想做外贸"
        });
        await db.SaveChangesAsync();

        var result = await svc.GetAsync(leadId, CancellationToken.None);
        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("legacy_text", json);
        Assert.Contains("not_verified", json);
    }

    [Fact]
    public async Task UnknownCategory_IsStillRejected()
    {
        using var db = CreateDb();
        var leadId = await SeedLeadAsync(db);
        var svc = new LeadDemandAssessmentService(db);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveAsync(leadId, "seo", In("pending", [Ev()]), reviewerId: 7, CancellationToken.None));
    }

    [Fact]
    public void Vocabulary_KeepsNotFoundAndConfirmedApart()
    {
        Assert.Contains(LeadEvidenceVerifications.NotFound, LeadEvidenceVerifications.All);
        Assert.Contains(LeadEvidenceVerifications.ConfirmedAbsent, LeadEvidenceVerifications.All);
        Assert.NotEqual(LeadEvidenceVerifications.NotFound, LeadEvidenceVerifications.ConfirmedAbsent);
        // legacy_text 不可被手工选择，但必须存在于词表（迁移会写入它）。
        Assert.Contains(LeadEvidenceTypes.LegacyText, LeadEvidenceTypes.All);
    }
}
