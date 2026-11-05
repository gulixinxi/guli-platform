using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class WebsiteDiagnosticAndPromotionFacts
{
    private sealed class StubWebsiteChecker : WebsiteCheckService
    {
        public PassiveFileResult Robots { get; set; } = new(200, "User-agent: *\nAllow: /", null);
        public PassiveFileResult Sitemap { get; set; } = new(200, "<urlset />", null);
        public int HomeCalls { get; private set; }
        public bool Timeout { get; set; }
        public StubWebsiteChecker() : base(new HttpClient(), NullLogger<WebsiteCheckService>.Instance) { }
        public override Task<PassiveFileResult> FetchWellKnownAsync(string domain, string path, CancellationToken ct = default) =>
            Task.FromResult(path == "/robots.txt" ? Robots : Sitemap);
        public override Task<WebsiteCheckResult> CheckAsync(string domain, CancellationToken ct = default)
        {
            HomeCalls++;
            if (Timeout) return Task.FromResult(new WebsiteCheckResult { Domain = domain, Accessible = false, TransientFailure = true, Error = "timeout" });
            return Task.FromResult(new WebsiteCheckResult
            {
                Domain = domain, Accessible = true, HttpsSupported = true, HttpsStatusCode = 200,
                Html = "<html><head><link rel='canonical' href='https://example.com/'/><script type='application/ld+json'>{}</script></head><body>" + new string('A', 250) + "</body></html>"
            });
        }
    }

    [Fact]
    public async Task RobotsRootDisallow_StopsHomeProbeAndMarksChecksNotTested()
    {
        var checker = new StubWebsiteChecker { Robots = new(200, "User-agent: *\nDisallow: /", null) };
        var checks = await WebsiteDiagnosticWorker.BuildChecksAsync("https://example.com/", checker, CancellationToken.None);
        Assert.Equal(0, checker.HomeCalls);
        Assert.Equal("not_tested", checks.Single(c => c.Key == "reachability").Status);
    }

    [Fact]
    public async Task RateLimit_IsRetryableInsteadOfAFalseNegative()
    {
        var checker = new StubWebsiteChecker { Robots = new(429, null, null) };
        await Assert.ThrowsAsync<HttpRequestException>(() => WebsiteDiagnosticWorker.BuildChecksAsync("https://example.com/", checker, CancellationToken.None));
        Assert.Equal(0, checker.HomeCalls);
    }

    [Fact]
    public async Task Timeout_IsRetryableInsteadOfAFalseNegative()
    {
        var checker = new StubWebsiteChecker { Timeout = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => WebsiteDiagnosticWorker.BuildChecksAsync("https://example.com/", checker, CancellationToken.None));
        Assert.Equal(1, checker.HomeCalls);
    }

    [Fact]
    public async Task SuccessfulProbe_ReportsOnlyTechnicalSignals()
    {
        var checker = new StubWebsiteChecker();
        var checks = await WebsiteDiagnosticWorker.BuildChecksAsync("https://example.com/", checker, CancellationToken.None);
        Assert.All(checks, c => Assert.Contains(c.Status, new[] { "pass", "fail", "not_tested", "error" }));
        Assert.Equal("pass", checks.Single(c => c.Key == "canonical").Status);
        Assert.Equal("pass", checks.Single(c => c.Key == "schema").Status);
        Assert.DoesNotContain(checks, c => c.Key.Contains("ai_recommendation", StringComparison.OrdinalIgnoreCase));
    }

    private static CrmDbContext Db(string name) => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    [Fact]
    public async Task Diagnostics_RequireConfirmedIdentity_AndKeepVersionsAndCooldown()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1001, Domain = "example.com", Port = 443 });
        await db.SaveChangesAsync();
        var identities = new IdentityCandidateService(db);
        await identities.CreateAsync(1001, new("甲企业", new("public_site", "https://example.com/about", "官网主体名称为甲企业", DateTime.UtcNow)), 5, CancellationToken.None);
        var candidate = await db.IdentityCandidates.SingleAsync();
        var diagnostics = new WebsiteDiagnosticService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.RequestAsync(candidate.Id, "https://example.com/", 5, CancellationToken.None));
        await identities.ReviewAsync(candidate.Id, new("confirm", "官网主体与域名关系经人工核对", 1), 6, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => diagnostics.RequestAsync(candidate.Id, "https://other.example.com/", 5, CancellationToken.None));
        await diagnostics.RequestAsync(candidate.Id, "https://example.com/", 5, CancellationToken.None);
        await diagnostics.RequestAsync(candidate.Id, "https://example.com/", 5, CancellationToken.None);
        Assert.Single(await db.WebsiteDiagnosticReports.ToListAsync());
        var first = await db.WebsiteDiagnosticReports.SingleAsync();
        first.RequestedAt = DateTime.UtcNow.AddHours(-2);
        await db.SaveChangesAsync();
        await diagnostics.RequestAsync(candidate.Id, "https://example.com/", 5, CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, await db.WebsiteDiagnosticReports.OrderBy(r => r.Version).Select(r => r.Version).ToArrayAsync());
    }

    [Fact]
    public async Task ReportHistory_ComparesOnlyCompletedReportsWithSameMethodVersion()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 3001, AssetId = 1001, Domain = "example.com", ProposedName = "测试主体", Status = "confirmed" });
        var now = DateTime.UtcNow;
        string Checks(string status) => JsonSerializer.Serialize(new[] { new DiagnosticCheck("https", status, "测试证据", now) });
        db.WebsiteDiagnosticReports.AddRange(
            new WebsiteDiagnosticReport { Id = 4001, CandidateId = 3001, Version = 1, Status = "completed", Url = "https://example.com/", ChecksJson = Checks("fail") },
            new WebsiteDiagnosticReport { Id = 4002, CandidateId = 3001, Version = 2, Status = "queued", Url = "https://example.com/", ChecksJson = "[]" },
            new WebsiteDiagnosticReport { Id = 4003, CandidateId = 3001, Version = 3, Status = "completed", Url = "https://example.com/", ChecksJson = Checks("pass") },
            new WebsiteDiagnosticReport { Id = 4004, CandidateId = 3001, Version = 4, Status = "completed", MethodVersion = "passive-web-v2", Url = "https://example.com/", ChecksJson = Checks("fail") });
        await db.SaveChangesAsync();
        var history = await new WebsiteDiagnosticService(db).ListAsync(3001, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(history));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("Version").GetInt32(), item => item);
        Assert.Equal(1, items[3].GetProperty("changes").GetArrayLength());
        Assert.Equal("https", items[3].GetProperty("changes")[0].GetProperty("Key").GetString());
        Assert.Equal("fail", items[3].GetProperty("changes")[0].GetProperty("PreviousStatus").GetString());
        Assert.Equal("pass", items[3].GetProperty("changes")[0].GetProperty("CurrentStatus").GetString());
        Assert.Equal(1, items[3].GetProperty("comparedWithVersion").GetInt32());
        Assert.Equal(0, items[2].GetProperty("changes").GetArrayLength());
        Assert.Equal(0, items[4].GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public void TechnicalChangeSignal_RequiresChangedCompletedComparableReport()
    {
        var now = DateTime.UtcNow;
        string Checks(string status) => JsonSerializer.Serialize(new[] { new DiagnosticCheck("https", status, "test", now) });
        var candidate = new IdentityCandidate { Id = 1, ProposedName = "测试企业", Status = "confirmed" };
        var before = new WebsiteDiagnosticReport { Id = 11, CandidateId = 1, Version = 1,
            Status = "completed", ChecksJson = Checks("fail") };
        var after = new WebsiteDiagnosticReport { Id = 12, CandidateId = 1, Version = 2,
            Status = "completed", Url = "https://example.com/", ChecksJson = Checks("pass") };
        var signal = WebsiteDiagnosticWorker.CreateChangeSignal(after, before, candidate);
        Assert.NotNull(signal);
        Assert.Equal("report_id=12", signal.QueryParams);
        Assert.Equal("website-diagnostic", signal.Source);
        Assert.Null(WebsiteDiagnosticWorker.CreateChangeSignal(after, after, candidate));
        Assert.Null(WebsiteDiagnosticWorker.CreateChangeSignal(after, null, candidate));
    }

    [Fact]
    public async Task Promotion_CreatesOneLinkedLeadAndAuditEvent_WithoutCompanyOrDeal()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1002, Domain = "example.com", Port = 443 });
        await db.SaveChangesAsync();
        var identities = new IdentityCandidateService(db);
        await identities.CreateAsync(1002, new("乙企业", new("public_site", "https://example.com/about", "网站运营主体为乙企业", DateTime.UtcNow)), 5, CancellationToken.None);
        var candidate = await db.IdentityCandidates.SingleAsync();
        var promotion = new IdentityLeadPromotionService(db, new LeadService(db, new TestCrmNumberGenerator()));
        Assert.False((await promotion.PreviewAsync(candidate.Id, CancellationToken.None)).Ready);
        await identities.ReviewAsync(candidate.Id, new("confirm", "网站主体名称和域名关系已经核对", 1), 6, CancellationToken.None);
        Assert.True((await promotion.PreviewAsync(candidate.Id, CancellationToken.None)).Ready);
        await promotion.PromoteAsync(candidate.Id, new(2), 7, CancellationToken.None);
        var lead = await db.Leads.SingleAsync();
        Assert.Equal(candidate.Id, lead.SourceIdentityCandidateId);
        Assert.Null(lead.CreditCode);
        Assert.Null(lead.ContactName);
        Assert.False((await promotion.PreviewAsync(candidate.Id, CancellationToken.None)).Ready);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => promotion.PromoteAsync(candidate.Id, new(2), 7, CancellationToken.None));
        Assert.Equal(1, await db.IdentityReviewEvents.CountAsync(e => e.Action == "promote_lead"));
        Assert.Empty(await db.Companies.ToListAsync());
        Assert.Empty(await db.Deals.ToListAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Promotion_BlocksCandidateValuesThatDoNotFitLeadColumns(bool longName)
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        var domain = longName ? "example.com" : string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), "example.com");
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1004, Domain = domain, Port = 443 });
        await db.SaveChangesAsync();
        var identities = new IdentityCandidateService(db);
        await identities.CreateAsync(1004, new(longName ? new string('甲', 201) : "测试企业",
            new("public_site", $"https://{domain}/about", "官网显示主体信息", DateTime.UtcNow)), 5, CancellationToken.None);
        var candidate = await db.IdentityCandidates.SingleAsync();
        await identities.ReviewAsync(candidate.Id, new("confirm", "主体与域名关系已经人工核对", 1), 6, CancellationToken.None);
        var promotion = new IdentityLeadPromotionService(db, new LeadService(db, new TestCrmNumberGenerator()));
        var preview = await promotion.PreviewAsync(candidate.Id, CancellationToken.None);
        Assert.False(preview.Ready);
        Assert.Contains(preview.Blockers, blocker => blocker.Contains(longName ? "名称" : "域名"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => promotion.PromoteAsync(candidate.Id, new(2), 7, CancellationToken.None));
        Assert.Empty(await db.Leads.ToListAsync());
    }

    [Fact]
    public async Task Promotion_AcceptsCandidateValuesAtLeadColumnLimits()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        var domain = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 8));
        Assert.Equal(200, domain.Length);
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1005, Domain = domain, Port = 443 });
        await db.SaveChangesAsync();
        var identities = new IdentityCandidateService(db);
        await identities.CreateAsync(1005, new(new string('甲', 200),
            new("public_site", $"https://{domain}/about", "官网显示主体信息", DateTime.UtcNow)), 5, CancellationToken.None);
        var candidate = await db.IdentityCandidates.SingleAsync();
        await identities.ReviewAsync(candidate.Id, new("confirm", "主体与域名关系已经人工核对", 1), 6, CancellationToken.None);
        var promotion = new IdentityLeadPromotionService(db, new LeadService(db, new TestCrmNumberGenerator()));
        Assert.True((await promotion.PreviewAsync(candidate.Id, CancellationToken.None)).Ready);
        await promotion.PromoteAsync(candidate.Id, new(2), 7, CancellationToken.None);
        var lead = await db.Leads.SingleAsync();
        Assert.Equal(200, lead.CompanyName.Length);
        Assert.Equal(domain, lead.WebsiteDomain);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticFailure_DoesNotPublishExceptionText(bool networkFailure)
    {
        const string secret = "Password=SECRET_SENTINEL";
        Exception failure = networkFailure ? new HttpRequestException(secret) : new InvalidOperationException(secret);
        var message = WebsiteDiagnosticWorker.PublicFailureMessage(failure);
        Assert.DoesNotContain(secret, message, StringComparison.Ordinal);
        Assert.NotEmpty(message);
    }

    [Fact]
    public void HistoricalReport_DoesNotRepublishRawFailureText()
    {
        const string secret = "Password=SECRET_SENTINEL";
        var report = new WebsiteDiagnosticReport
        {
            Id = 99, CandidateId = 1, Status = "failed", Error = secret,
            AttemptLogJson = JsonSerializer.Serialize(new[] { new DiagnosticAttempt(1, DateTime.UtcNow, secret) }),
            ChecksJson = JsonSerializer.Serialize(new[] { new DiagnosticCheck("reachability", "error", secret, DateTime.UtcNow) })
        };
        var response = JsonSerializer.Serialize(WebsiteDiagnosticService.Project(report, false),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.DoesNotContain(secret, response, StringComparison.Ordinal);
        Assert.Contains("网站检测未完成", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promotion_BlocksExistingNameOrDomainInsteadOfSilentlyMerging()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1003, Domain = "example.com", Port = 443 });
        db.Leads.Add(new Lead
        {
            Id = 2001, CompanyName = "既有线索", WebsiteDomain = "example.com",
            CreatedAt = DateTimeOffset.UtcNow, ModifiedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var identities = new IdentityCandidateService(db);
        await identities.CreateAsync(1003, new("新主体", new("public_site", "https://example.com/about", "当前网站声明新主体", DateTime.UtcNow)), 5, CancellationToken.None);
        var candidate = await db.IdentityCandidates.SingleAsync();
        await identities.ReviewAsync(candidate.Id, new("confirm", "模拟一域多主体审核，仅用于测试", 1), 6, CancellationToken.None);
        var promotion = new IdentityLeadPromotionService(db, new LeadService(db, new TestCrmNumberGenerator()));
        var preview = await promotion.PreviewAsync(candidate.Id, CancellationToken.None);
        Assert.False(preview.Ready);
        Assert.Contains(preview.Blockers, x => x.Contains("一域多主体"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => promotion.PromoteAsync(candidate.Id, new(2), 7, CancellationToken.None));
        Assert.Single(await db.Leads.ToListAsync());
    }
}
