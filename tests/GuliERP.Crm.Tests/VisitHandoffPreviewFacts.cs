using System.Text.Json;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class VisitHandoffPreviewFacts
{
    private static CrmDbContext Db() => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    [Fact]
    public async Task UnverifiedVisitReference_AlwaysStaysPendingAndCreatesNothing()
    {
        using var db = Db();
        db.Leads.Add(new Lead { Id = 101, CompanyName = "模拟客户" });
        await db.SaveChangesAsync();
        var preview = await new VisitHandoffPreviewService(db).PreviewAsync(101,
            new("website", "visit", "visit:sample-1"), default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(preview));
        Assert.Equal("pending_sync", json.RootElement.GetProperty("handoffStatus").GetString());
        Assert.Equal("unverified_external", json.RootElement.GetProperty("sourceVerification").GetString());
        Assert.False(json.RootElement.GetProperty("createsDeal").GetBoolean());
        Assert.False(json.RootElement.GetProperty("copiesAttachment").GetBoolean());
        Assert.False(json.RootElement.GetProperty("performsExternalRequest").GetBoolean());
        Assert.Empty(await db.Deals.ToListAsync());
        Assert.Empty(await db.Signals.ToListAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => new VisitHandoffPreviewService(db).PreviewAsync(101,
            new("website", "visit", "/mnt/nas/secret.jpg"), default));
    }

    [Fact]
    public async Task InternalReportMustBelongToLeadAndDoesNotBypassConfirmedAssessment()
    {
        using var db = Db();
        db.Leads.Add(new Lead { Id = 101, CompanyName = "模拟客户", SourceIdentityCandidateId = 201 });
        db.WebsiteDiagnosticReports.AddRange(
            new WebsiteDiagnosticReport { Id = 301, CandidateId = 201, Version = 1, Url = "https://example.com", Status = "completed" },
            new WebsiteDiagnosticReport { Id = 302, CandidateId = 202, Version = 1, Url = "https://other.com", Status = "completed" });
        await db.SaveChangesAsync();
        var svc = new VisitHandoffPreviewService(db);
        var preview = await svc.PreviewAsync(101, new("geo", "website_report", "301"), default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(preview));
        Assert.Equal("confirmed_internal_report", json.RootElement.GetProperty("sourceVerification").GetString());
        Assert.False(json.RootElement.GetProperty("canUseExistingDealFlow").GetBoolean());
        Assert.False(json.RootElement.GetProperty("createsTicket").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PreviewAsync(101,
            new("geo", "website_report", "302"), default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.PreviewAsync(999,
            new("geo", "website_report", "301"), default));
        db.LeadDemandAssessments.Add(new LeadDemandAssessment { Id = 401, LeadId = 101,
            Category = BusinessCategory.GEOOpt, State = "confirmed", ReviewerId = 9 });
        await db.SaveChangesAsync();
        var stillPending = await svc.PreviewAsync(101, new("geo", "website_report", "301"), default);
        using var final = JsonDocument.Parse(JsonSerializer.Serialize(stillPending));
        Assert.True(final.RootElement.GetProperty("assessmentConfirmed").GetBoolean());
        Assert.False(final.RootElement.GetProperty("canUseExistingDealFlow").GetBoolean());
        Assert.Equal("pending_sync", final.RootElement.GetProperty("handoffStatus").GetString());
    }
}
