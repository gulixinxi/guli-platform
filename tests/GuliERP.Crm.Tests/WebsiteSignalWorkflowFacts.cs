using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Application;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class WebsiteSignalWorkflowFacts
{
    private static CrmDbContext Db() => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    [Fact]
    public async Task MalformedAttemptHistory_DoesNotHideDiagnosticReport()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 20, AssetId = 10, Domain = "example.com", ProposedName = "模拟企业" });
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport { Id = 30, CandidateId = 20,
            Version = 1, Url = "https://example.com/", Status = "completed",
            ChecksJson = "[]", AttemptLogJson = "[\"invalid test attempt\"]" });
        await db.SaveChangesAsync();
        var result = await new WebsiteDiagnosticService(db).ListAsync(20, default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var report = json.RootElement.GetProperty("items")[0];
        Assert.Equal("30", report.GetProperty("id").GetString());
        Assert.Equal(0, report.GetProperty("attempts").GetArrayLength());
        Assert.Contains("格式异常", report.GetProperty("attemptsDataWarning").GetString());
    }

    [Fact]
    public async Task PendingSummary_SeparatesWebsiteWorkFromSalesSignalsAndHandledRows()
    {
        using var db = Db();
        db.Signals.AddRange(
            new Signal { Id = 1, Source = "website-diagnostic", Status = SignalStatus.Pending },
            new Signal { Id = 2, Source = "website-content-review", Status = SignalStatus.Pending },
            new Signal { Id = 3, Source = "website-content-change", Status = SignalStatus.Pending },
            new Signal { Id = 4, Source = "website-content-change", Status = SignalStatus.Confirmed },
            new Signal { Id = 5, Source = "quake", Status = SignalStatus.Pending });
        await db.SaveChangesAsync();
        var result = await new WebsiteSignalWorkflowService(db).PendingSummaryAsync(default);
        Assert.Equal(3, result.Total);
        Assert.Equal((1, 1, 1), (result.Technical, result.ContentReview, result.ContentChange));
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public async Task Target_ResolvesExactReportAndEvidence_AndRejectsBrokenLinks()
    {
        using var db = Db();
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 10, Domain = "example.com", BatchId = Guid.NewGuid() });
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 20, AssetId = 10, Domain = "example.com", ProposedName = "模拟企业" });
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport { Id = 30, CandidateId = 20, Version = 1, Url = "https://example.com/", Status = "completed" });
        db.WebsiteContentEvidence.Add(new WebsiteContentEvidence { Id = 40, CandidateId = 20, ReportId = 30,
            SourceUrl = "https://example.com/about", Excerpt = "模拟原文", Observation = "模拟观察", Limitation = "仅供测试" });
        db.Signals.AddRange(
            new Signal { Id = 50, Source = "website-content-review", QueryParams = "report_id=30" },
            new Signal { Id = 51, Source = "website-content-change", QueryParams = "evidence_id=40" },
            new Signal { Id = 52, Source = "website-content-change", QueryParams = "evidence_id=999" },
            new Signal { Id = 53, Source = "website-content-review", QueryParams = "report_id=30&extra=1" });
        await db.SaveChangesAsync();
        var service = new WebsiteSignalWorkflowService(db);
        var report = await service.TargetAsync(50, default);
        Assert.Equal("30", report.ReportId);
        Assert.Null(report.EvidenceId);
        Assert.Equal("example.com", report.Asset.Domain);
        var change = await service.TargetAsync(51, default);
        Assert.Equal("40", change.EvidenceId);
        Assert.Equal("20", change.CandidateId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.TargetAsync(52, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.TargetAsync(53, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetSignalAsync(999, default));
    }

    [Fact]
    public async Task Review_RequiresAcceptedEvidence_AndRejectsShortReason()
    {
        using var db = Db();
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 10, Domain = "example.com", BatchId = Guid.NewGuid() });
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 20, AssetId = 10, Domain = "example.com", ProposedName = "模拟企业" });
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport { Id = 30, CandidateId = 20, Version = 1, Url = "https://example.com/", Status = "completed" });
        db.Signals.Add(new Signal { Id = 50, Source = "website-content-review", QueryParams = "report_id=30" });
        await db.SaveChangesAsync();
        var service = new WebsiteSignalWorkflowService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideAsync(50, new("reviewed", "短"), 7, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecideAsync(50,
            new("reviewed", "模拟审核记录已经仔细核对"), 7, default));
        var generic = new SignalService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => generic.UpdateStatusAsync(50,
            new UpdateSignalStatusCommand(SignalStatus.Confirmed, "绕过专用处理"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => generic.UpdateVerificationAsync(50,
            new UpdateVerificationCommand(1, null, null), default));
    }
}
