using System.Net;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class ChinazLookupFacts
{
    private static CrmDbContext Db() => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));
    private static ChinazConnectorSettings Settings(IConfiguration config) => new(
        new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(new Dictionary<string, string?>
        { ["Crm:Chinaz:SettingsPath"] = Path.Combine(Path.GetTempPath(), "guli-crm-tests", Guid.NewGuid().ToString("N"), "chinaz.json") }).Build());

    [Fact]
    public void ConnectorSwitch_RequiresAuthorizationReferenceAndPersistsState()
    {
        var path = Path.Combine(Path.GetTempPath(), "guli-crm-tests", Guid.NewGuid().ToString("N"), "chinaz.json");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Crm:Chinaz:SettingsPath"] = path }).Build();
        var settings = new ChinazConnectorSettings(config);
        Assert.False(settings.GetStatus(11, 22).Enabled);
        Assert.Throws<ArgumentException>(() => settings.Update(11, 22, new(true, "")));
        Assert.False(File.Exists(path));
        Assert.True(settings.Update(11, 22, new(true, "test-authorization")).Enabled);
        Assert.True(new ChinazConnectorSettings(config).GetStatus(11, 22).Enabled);
        Assert.False(settings.GetStatus(11, 23).Enabled);
        Assert.False(settings.Update(11, 22, new(false, "test-authorization")).Enabled);
        Assert.False(new ChinazConnectorSettings(config).GetStatus(11, 22).Enabled);
    }

    [Fact]
    public async Task NoAuthorizationConfiguration_SendsNoNetworkRequest()
    {
        using var db = Db();
        var handler = new StubHandler("<html></html>");
        var service = new ChinazLookupService(new HttpClient(handler), Settings(new ConfigurationBuilder().Build()), db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var result = await service.LookupAsync(new(["101"]), CancellationToken.None);
        Assert.False(result.Configured);
        Assert.Equal("not_configured", Assert.Single(result.Items).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task EnabledConnector_UsesOnlyFixedHostAndReturnsUnconfirmedCandidate()
    {
        using var db = Db();
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 101, Domain = "example.com" });
        await db.SaveChangesAsync();
        var handler = new StubHandler("<div>example.com是甲企业有限公司旗下网站</div><script>var license = '京ICP备123号';</script>");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Crm:Chinaz:Enabled"] = "true",
            ["Crm:Chinaz:AuthorizationReference"] = "synthetic-test-authorization"
        }).Build();
        var settings = Settings(config);
        settings.Update(11, 22, new(true, "synthetic-test-authorization"));
        var service = new ChinazLookupService(new HttpClient(handler), settings, db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var result = await service.LookupAsync(new(["101"]), CancellationToken.None);
        var row = Assert.Single(result.Items);
        Assert.Equal("found", row.Status);
        Assert.Equal("甲企业有限公司", row.ProposedName);
        Assert.Equal("京ICP备123号", row.ReportedIcpNumber);
        Assert.Equal("seo.chinaz.com", handler.LastUri!.Host);
        Assert.Empty(await db.IdentityCandidates.ToListAsync());
    }

    [Fact]
    public void Parser_DoesNotInferCompanyFromMaskedOrUnrelatedText()
    {
        Assert.Null(ChinazLookupService.Parse("example.com", "other.com是乙企业旗下网站").Name);
        Assert.Null(ChinazLookupService.Parse("example.com", "example.com是乙**企业旗下网站").Name);
        Assert.Equal("甲企业", ChinazLookupService.Parse("example.com", "<p>example.com是甲企业旗下网站</p>").Name);
    }

    [Fact]
    public async Task InvalidAssetIds_AreRejectedBeforeAnyRequest()
    {
        using var db = Db();
        var handler = new StubHandler("unused");
        var service = new ChinazLookupService(new HttpClient(handler), Settings(new ConfigurationBuilder().Build()), db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        await Assert.ThrowsAsync<ArgumentException>(() => service.LookupAsync(new(["0"]), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TenSyntheticSites_ReturnPerRowOutcomesWithoutSavingBusinessData()
    {
        using var db = Db();
        for (var i = 1; i <= 10; i++)
            db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 2000 + i, Domain = $"site{i}.example.com" });
        await db.SaveChangesAsync();
        var handler = new TenSiteHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Crm:Chinaz:Enabled"] = "true",
            ["Crm:Chinaz:AuthorizationReference"] = "synthetic-test-only"
        }).Build();
        var settings = Settings(config);
        settings.Update(11, 22, new(true, "synthetic-test-only"));
        var service = new ChinazLookupService(new HttpClient(handler), settings, db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var result = await service.LookupAsync(new(Enumerable.Range(2001, 10).Select(id => id.ToString()).ToList()), CancellationToken.None);
        Assert.Equal(10, handler.Calls);
        Assert.Equal(10, result.Items.Count);
        Assert.Equal(7, result.Items.Count(row => row.Status == "found"));
        Assert.Contains(result.Items, row => row.Status == "no_subject");
        Assert.Contains(result.Items, row => row.Status == "rate_limited");
        Assert.Contains(result.Items, row => row.Status == "http_error");
        Assert.Empty(await db.IdentityCandidates.ToListAsync());
    }

    [Fact]
    public async Task SwitchOffDuringBatch_StopsRemainingNetworkRequests()
    {
        using var db = Db();
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 301, Domain = "one.example.com" });
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 302, Domain = "two.example.com" });
        await db.SaveChangesAsync();
        var settings = Settings(new ConfigurationBuilder().Build());
        settings.Update(11, 22, new(true, "synthetic-test-authorization"));
        var handler = new SwitchOffHandler(() => settings.Update(11, 22, new(false, "synthetic-test-authorization")));
        var service = new ChinazLookupService(new HttpClient(handler), settings, db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));

        var result = await service.LookupAsync(new(["301", "302"]), CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.False(result.Configured);
        Assert.Equal("not_configured", result.Items[1].Status);
        Assert.False(settings.GetStatus(11, 22).Enabled);
    }

    [Fact]
    public async Task AnotherCompany_CannotUseThisCompanysEnabledConnector()
    {
        using var db = Db();
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 401, Domain = "example.com" });
        await db.SaveChangesAsync();
        var settings = Settings(new ConfigurationBuilder().Build());
        settings.Update(11, 22, new(true, "synthetic-test-authorization"));
        var handler = new StubHandler("<html></html>");
        var service = new ChinazLookupService(new HttpClient(handler), settings, db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(23));

        var result = await service.LookupAsync(new(["401"]), CancellationToken.None);

        Assert.False(result.Configured);
        Assert.Equal("not_configured", Assert.Single(result.Items).Status);
        Assert.Equal(0, handler.Calls);
    }

    private sealed class StubHandler(string html) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }

    private sealed class TenSiteHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("seo.chinaz.com", request.RequestUri!.Host);
            var domain = request.RequestUri.AbsolutePath.TrimStart('/');
            var number = int.Parse(domain[4..domain.IndexOf('.')]);
            if (number == 9) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            if (number == 10) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var html = number == 8 ? "<div>页面未提供主体</div>"
                : $"<div>{domain}是测试企业{number}有限公司旗下网站</div><script>var license = '测ICP备{number}号';</script>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) });
        }
    }

    private sealed class SwitchOffHandler(Action afterFirstRequest) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1) afterFirstRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<div>one.example.com是测试企业旗下网站</div>")
            });
        }
    }
}
