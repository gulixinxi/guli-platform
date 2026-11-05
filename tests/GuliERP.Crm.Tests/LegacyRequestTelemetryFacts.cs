using System.Net;
using GuliERP.Crm.Infrastructure.Services;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class LegacyRequestTelemetryFacts
{
    [Fact]
    public void DisabledByDefault_AndIsolatedByCompany()
    {
        var off = new LegacyRequestTelemetry(new ConfigurationBuilder().Build());
        off.Observe(11, 22, "quake", "search", "http_200");
        Assert.Empty(off.Snapshot(11, 22));

        var on = Enabled();
        on.Observe(11, 22, "quake", "search", "http_200");
        on.Observe(11, 23, "quake", "search", "http_429");
        on.Observe(null, 22, "quake", "search", "uncertain");
        Assert.Single(on.Snapshot(11, 22));
        Assert.Equal("http_200", on.Snapshot(11, 22)[0].Outcome);
        Assert.Single(on.Snapshot(11, 23));
        Assert.Empty(on.Snapshot(12, 22));
    }

    [Fact]
    public async Task QuakeKeyFallback_CountsEachPhysicalResponseWithoutKeyOrTarget()
    {
        var telemetry = Enabled();
        var credentials = new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build());
        credentials.SetKey(11, 22, "secret-first");
        credentials.SetKey(11, 22, "secret-second");
        using var http = new HttpClient(new TwoResponseHandler());
        var service = new QuakeSearchService(http, credentials, new MutableCurrentTenant(11),
            new MutableCurrentCompany(22), NullLogger<QuakeSearchService>.Instance, telemetry);
        await service.SearchAsync("private-query");
        var counts = telemetry.Snapshot(11, 22);
        Assert.Equal(2, counts.Sum(x => x.Count));
        Assert.Contains(counts, x => x.Outcome == "http_429" && x.Count == 1);
        Assert.Contains(counts, x => x.Outcome == "http_200" && x.Count == 1);
        var json = System.Text.Json.JsonSerializer.Serialize(counts);
        Assert.DoesNotContain("secret", json);
        Assert.DoesNotContain("private-query", json);
    }

    [Fact]
    public async Task ChinazBatch_ObservesOneAttemptPerValidAsset()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Crm:Chinaz:Enabled"] = "true",
            ["Crm:Chinaz:AuthorizationReference"] = "synthetic-test-only",
            ["Crm:Chinaz:SettingsPath"] = Path.Combine(Path.GetTempPath(), "guli-crm-tests", Guid.NewGuid().ToString("N"), "chinaz.json")
        }).Build();
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 101, Domain = "example.com" });
        await db.SaveChangesAsync();
        var telemetry = Enabled();
        using var http = new HttpClient(new SingleResponseHandler());
        var settings = new ChinazConnectorSettings(config);
        settings.Update(11, 22, new(true, "synthetic-test-only"));
        var service = new ChinazLookupService(http, settings, db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22), telemetry);
        var result = await service.LookupAsync(new(["101", "102"]), CancellationToken.None);
        Assert.Equal(2, result.Items.Count);
        var observed = Assert.Single(telemetry.Snapshot(11, 22));
        Assert.Equal("chinaz", observed.SourceCode);
        Assert.Equal("batch_lookup", observed.Route);
        Assert.Equal(1, observed.Count);
    }

    [Fact]
    public async Task PersistedObservation_SurvivesNewServiceInstance_AndHidesOtherCompany()
    {
        var options = new DbContextOptionsBuilder<CrmDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Crm:LegacyRequestTelemetry:Enabled"] = "true" }).Build();
        await using (var db = new CrmDbContext(options, new MutableCurrentTenant(11), new MutableCurrentCompany(22)))
        {
            var telemetry = new LegacyRequestTelemetry(config, db);
            await telemetry.ObserveAsync(11, 22, "quake", "search", "http_200");
        }
        await using (var db = new CrmDbContext(options, new MutableCurrentTenant(11), new MutableCurrentCompany(22)))
        {
            var telemetry = new LegacyRequestTelemetry(config, db);
            Assert.Equal(1, Assert.Single(await telemetry.SnapshotAsync(11, 22, CancellationToken.None)).Count);
        }
        await using (var db = new CrmDbContext(options, new MutableCurrentTenant(11), new MutableCurrentCompany(23)))
            Assert.Empty(await new LegacyRequestTelemetry(config, db).SnapshotAsync(11, 23, CancellationToken.None));
    }

    private static LegacyRequestTelemetry Enabled() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Crm:LegacyRequestTelemetry:Enabled"] = "true" }).Build());

    private sealed class TwoResponseHandler : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var status = Interlocked.Increment(ref _calls) == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"code\":0,\"data\":[]}") });
        }
    }

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") });
    }
}
