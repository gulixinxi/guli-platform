using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class WebsiteContentEvidenceFacts
{
    private static CrmDbContext Db() => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    [Fact]
    public void ReportForeignKey_RequiresSameTenantCompanyAndCandidate()
    {
        using var db = Db();
        var evidence = db.Model.FindEntityType(typeof(WebsiteContentEvidence))!;
        var reportForeignKey = Assert.Single(evidence.GetForeignKeys(), fk =>
            fk.PrincipalEntityType.ClrType == typeof(WebsiteDiagnosticReport));

        Assert.Equal(new[] { "TenantId", "CompanyId", "CandidateId", "ReportId" },
            reportForeignKey.Properties.Select(property => property.Name));
        Assert.Equal(new[] { "TenantId", "CompanyId", "CandidateId", "Id" },
            reportForeignKey.PrincipalKey.Properties.Select(property => property.Name));
    }

    [Fact]
    public async Task ContentEvidence_RequiresCompletedSameCandidateReport_AndSameDomainSource()
    {
        using var db = Db();
        db.IdentityCandidates.AddRange(
            new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "甲", Status = "confirmed", CreatedBy = 1 },
            new IdentityCandidate { Id = 102, AssetId = 2, Domain = "other.com", ProposedName = "乙", Status = "confirmed", CreatedBy = 1 });
        db.WebsiteDiagnosticReports.AddRange(
            new WebsiteDiagnosticReport { Id = 201, CandidateId = 101, Version = 1, Url = "https://example.com/", Status = "completed", RequestedBy = 1 },
            new WebsiteDiagnosticReport { Id = 202, CandidateId = 102, Version = 1, Url = "https://other.com/", Status = "completed", RequestedBy = 1 },
            new WebsiteDiagnosticReport { Id = 203, CandidateId = 101, Version = 2, Url = "https://example.com/", Status = "failed", RequestedBy = 1 });
        await db.SaveChangesAsync();
        var service = new WebsiteContentEvidenceService(db);
        WebsiteContentEvidenceInput Input(string reportId, string url) =>
            new(reportId, url, "页面原文", "公开页面有产品说明", "只观察页面，未测 AI 推荐", DateTime.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(101, Input("202", "https://example.com/about"), 7, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(101, Input("203", "https://example.com/about"), 7, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(101, Input("201", "https://other.com/about"), 7, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(101, Input("201", "http://example.com/about"), 7, default));
        Assert.Empty(await db.WebsiteContentEvidence.ToListAsync());
    }

    [Fact]
    public async Task AcceptedObservation_KeepsSourceAndReviewTrail_AndRejectsSecondDecision()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "甲", Status = "confirmed", CreatedBy = 1 });
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport { Id = 201, CandidateId = 101, Version = 1, Url = "https://example.com/", Status = "completed", RequestedBy = 1 });
        await db.SaveChangesAsync();
        var service = new WebsiteContentEvidenceService(db);
        await service.RecordAsync(101, new("201", "https://example.com/about", "页面原文", "产品介绍明确", "未测 AI 推荐", DateTime.UtcNow), 7, default);
        var row = await db.WebsiteContentEvidence.SingleAsync();
        await service.ReviewAsync(101, row.Id, new("accepted", "原文与链接对应，允许用于人工分析"), 8, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(101, row.Id, new("rejected", "重复审核不允许进行"), 9, default));
        var saved = await db.WebsiteContentEvidence.AsNoTracking().SingleAsync();
        Assert.Equal("accepted", saved.Status);
        Assert.Equal(8, saved.ReviewedBy);
        Assert.Equal("页面原文", saved.Excerpt);
        Assert.Equal("manual-evidence-v1", saved.MethodVersion);
    }

    [Fact]
    public async Task ScheduledReport_RemindsOnlyWhenPriorAcceptedEvidenceExists_AndOnlyOnce()
    {
        using var db = Db();
        var candidate = new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "测试企业", Status = "confirmed" };
        var old = new WebsiteDiagnosticReport { Id = 201, CandidateId = 101, Version = 1,
            Url = "https://example.com/", Status = "completed" };
        var current = new WebsiteDiagnosticReport { Id = 202, CandidateId = 101, Version = 2,
            Url = "https://example.com/", Status = "completed", Trigger = "scheduled" };
        db.IdentityCandidates.Add(candidate);
        db.WebsiteDiagnosticReports.AddRange(old, current);
        await db.SaveChangesAsync();
        Assert.Null(await WebsiteContentSignalService.BuildScheduledReviewReminderAsync(db, current, candidate, default));
        db.WebsiteContentEvidence.Add(new WebsiteContentEvidence { Id = 301, CandidateId = 101,
            ReportId = 201, SourceUrl = "https://example.com/about", Excerpt = "旧内容",
            Observation = "旧观察", Limitation = "人工观察", Status = "accepted" });
        await db.SaveChangesAsync();
        var signal = await WebsiteContentSignalService.BuildScheduledReviewReminderAsync(db, current, candidate, default);
        Assert.NotNull(signal);
        Assert.Equal(WebsiteContentSignalService.ReviewSource, signal.Source);
        Assert.Contains("尚不能判断内容是否变化", signal.Detail);
        using (var due = JsonDocument.Parse(JsonSerializer.Serialize(await new WebsiteContentEvidenceService(db).ListAsync(101, default))))
            Assert.Equal("202", due.RootElement.GetProperty("reviewDueReportId").GetString());
        db.Signals.Add(signal);
        await db.SaveChangesAsync();
        Assert.Null(await WebsiteContentSignalService.BuildScheduledReviewReminderAsync(db, current, candidate, default));
        db.WebsiteContentEvidence.Add(new WebsiteContentEvidence { Id = 302, CandidateId = 101,
            ReportId = 202, SourceUrl = "https://example.com/about", Excerpt = "新内容",
            Observation = "新观察", Limitation = "人工观察", Status = "accepted" });
        await db.SaveChangesAsync();
        using (var cleared = JsonDocument.Parse(JsonSerializer.Serialize(await new WebsiteContentEvidenceService(db).ListAsync(101, default))))
            Assert.Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("reviewDueReportId").ValueKind);
        current.Trigger = "manual";
        Assert.Null(await WebsiteContentSignalService.BuildScheduledReviewReminderAsync(db, current, candidate, default));
    }

    [Fact]
    public async Task OnlyApprovedChangedExcerptAcrossComparableReports_CreatesDifferenceSignal()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 101, AssetId = 1,
            Domain = "example.com", ProposedName = "测试企业", Status = "confirmed" });
        db.WebsiteDiagnosticReports.AddRange(
            new WebsiteDiagnosticReport { Id = 201, CandidateId = 101, Version = 1,
                Url = "https://example.com/", Status = "completed" },
            new WebsiteDiagnosticReport { Id = 202, CandidateId = 101, Version = 2,
                Url = "https://example.com/", Status = "completed" },
            new WebsiteDiagnosticReport { Id = 203, CandidateId = 101, Version = 3,
                Url = "https://example.com/", Status = "completed" });
        await db.SaveChangesAsync();
        var service = new WebsiteContentEvidenceService(db);
        async Task<WebsiteContentEvidence> Record(string reportId, string excerpt)
        {
            await service.RecordAsync(101, new(reportId, "https://example.com/about", excerpt,
                "人工内容观察", "未测 AI 推荐", DateTime.UtcNow), 7, default);
            return await db.WebsiteContentEvidence.SingleAsync(e => e.ReportId == long.Parse(reportId));
        }
        var first = await Record("201", "原始 产品 介绍");
        await service.ReviewAsync(101, first.Id, new("accepted", "已核对模拟页面原文内容"), 8, default);
        Assert.Empty(await db.Signals.Where(s => s.Source == WebsiteContentSignalService.ChangeSource).ToListAsync());
        var same = await Record("202", "原始   产品\n介绍");
        await service.ReviewAsync(101, same.Id, new("accepted", "相同页面原文，仅空白不同"), 8, default);
        Assert.Empty(await db.Signals.Where(s => s.Source == WebsiteContentSignalService.ChangeSource).ToListAsync());
        var changed = await Record("203", "新的产品介绍");
        await service.ReviewAsync(101, changed.Id, new("accepted", "已核对新版模拟页面原文"), 8, default);
        var signal = await db.Signals.SingleAsync(s => s.Source == WebsiteContentSignalService.ChangeSource);
        Assert.Equal($"evidence_id={changed.Id}", signal.QueryParams);
        Assert.Contains(same.Id.ToString(), signal.RawContent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(101, changed.Id,
            new("accepted", "重复审核不应产生新提醒"), 8, default));
        Assert.Equal(1, await db.Signals.CountAsync(s => s.Source == WebsiteContentSignalService.ChangeSource));
    }

    [Fact]
    public async Task RevokedIdentity_CannotApprovePendingContentEvidence()
    {
        using var db = Db();
        var candidate = new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com",
            ProposedName = "测试企业", Status = "confirmed" };
        db.IdentityCandidates.Add(candidate);
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport { Id = 201, CandidateId = 101,
            Version = 1, Url = "https://example.com/", Status = "completed" });
        await db.SaveChangesAsync();
        var service = new WebsiteContentEvidenceService(db);
        await service.RecordAsync(101, new("201", "https://example.com/about", "模拟原文",
            "人工观察", "未测 AI", DateTime.UtcNow), 7, default);
        var evidence = await db.WebsiteContentEvidence.SingleAsync();
        candidate.Status = "reopened";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(101, evidence.Id,
            new("accepted", "身份撤销后不能确认内容依据"), 8, default));
        Assert.Empty(await db.Signals.Where(s => s.Source == WebsiteContentSignalService.ChangeSource).ToListAsync());
    }

    [Fact]
    public async Task OutOfOrderApproval_StillFindsDifferenceWithoutDuplicateSignal()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 101, AssetId = 1,
            Domain = "example.com", ProposedName = "测试企业", Status = "confirmed" });
        db.WebsiteDiagnosticReports.AddRange(
            new WebsiteDiagnosticReport { Id = 201, CandidateId = 101, Version = 1,
                Url = "https://example.com/", Status = "completed" },
            new WebsiteDiagnosticReport { Id = 202, CandidateId = 101, Version = 2,
                Url = "https://example.com/", Status = "completed" },
            new WebsiteDiagnosticReport { Id = 203, CandidateId = 101, Version = 3,
                Url = "https://example.com/", Status = "completed" });
        await db.SaveChangesAsync();
        var service = new WebsiteContentEvidenceService(db);
        async Task<long> Record(string reportId, string excerpt)
        {
            await service.RecordAsync(101, new(reportId, "https://example.com/about", excerpt,
                "人工观察", "未测 AI", DateTime.UtcNow), 7, default);
            return (await db.WebsiteContentEvidence.SingleAsync(e => e.ReportId == long.Parse(reportId))).Id;
        }
        var newest = await Record("203", "新版内容");
        await service.ReviewAsync(101, newest, new("accepted", "先通过新版模拟内容证据"), 8, default);
        Assert.Empty(await db.Signals.Where(s => s.Source == WebsiteContentSignalService.ChangeSource).ToListAsync());
        var oldest = await Record("201", "旧版内容");
        await service.ReviewAsync(101, oldest, new("accepted", "后通过旧版模拟内容证据"), 8, default);
        Assert.Equal($"evidence_id={newest}", (await db.Signals.SingleAsync(s => s.Source == WebsiteContentSignalService.ChangeSource)).QueryParams);
        var middle = await Record("202", "旧版内容");
        await service.ReviewAsync(101, middle, new("accepted", "中间版本与旧版相同，去重"), 8, default);
        Assert.Equal(1, await db.Signals.CountAsync(s => s.Source == WebsiteContentSignalService.ChangeSource));
    }
}
