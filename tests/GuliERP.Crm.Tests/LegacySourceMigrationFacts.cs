using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class LegacySourceMigrationFacts
{
    [Fact]
    public async Task MissingConfigurationAndRegistrationNeverClaimsReady()
    {
        using var db = NewDb();
        var service = NewService(db, Config());
        var statuses = await service.ListAsync(default);
        Assert.Equal(2, statuses.Count);
        Assert.All(statuses, row =>
        {
            Assert.Equal("legacy_unmetered", row.ExistingMode);
            Assert.False(row.ExistingConfigured);
            Assert.False(row.CanSwitchToUnifiedMetering);
            Assert.Equal("missing", row.RegistrationStatus);
        });
        var preview = await service.PreviewAsync(new("quake", "query", ["domain"]), default);
        Assert.Equal("registration_missing", preview.Decision);
        Assert.False(preview.PerformsExternalRequest);
        Assert.Empty(await db.DataSourceExecutionAttempts.ToListAsync());
    }

    [Fact]
    public async Task CompleteInternalRegistrationStillCannotSwitchWithoutVendorVerification()
    {
        using var db = NewDb();
        var config = Config(true);
        var register = new DataSourceAuthorizationService(db,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        await register.SaveAsync("quake", new(0, "reviewed", "test-reference", "domain,ip", 3, 30,
            "test-cost", 7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(7)), 1, default);
        await register.SaveAsync("chinaz", new(0, "reviewed", "test-reference", "company_name,icp_number", 3, 30,
            "test-cost", 7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(7)), 1, default);
        var service = NewService(db, config);
        var statuses = await service.ListAsync(default);
        Assert.All(statuses, row =>
        {
            Assert.True(row.ExistingConfigured);
            Assert.True(row.ConfigurationPrecheckPassed);
            Assert.False(row.CanSwitchToUnifiedMetering);
            Assert.Contains(row.Blockers, blocker => blocker.Contains("供应商许可"));
        });
        Assert.Equal(1, statuses.Single(x => x.SourceCode == "quake").MaximumPhysicalAttemptsPerAction);
        Assert.Equal(10, statuses.Single(x => x.SourceCode == "chinaz").MaximumPhysicalAttemptsPerAction);
        var preview = await service.PreviewAsync(new("quake", "query", ["domain", "ip"]), default);
        Assert.Equal("policy_match_live_blocked", preview.Decision);
        Assert.False(preview.PerformsExternalRequest);
        Assert.Equal("field_denied", (await service.PreviewAsync(new("quake", "query", ["credit_code"]), default)).Decision);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(new("chinaz", "query", ["company_name"]), default));
        Assert.Empty(await db.DataSourceExecutionAttempts.ToListAsync());
    }

    [Fact]
    public async Task RegistrationDoesNotCrossCompanyBoundary()
    {
        var name = Guid.NewGuid().ToString("N");
        using var owner = NewDb(name, 22);
        var register = new DataSourceAuthorizationService(owner,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        await register.SaveAsync("quake", new(0, "reviewed", "test-reference", "domain", 1, 1,
            "test-cost", 7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)), 1, default);
        using var other = NewDb(name, 23);
        var service = NewService(other, Config(true), 23);
        Assert.Equal("registration_missing", (await service.PreviewAsync(new("quake", "query", ["domain"]), default)).Decision);
        Assert.Equal("missing", (await service.ListAsync(default)).Single(x => x.SourceCode == "quake").RegistrationStatus);
    }

    private static CrmDbContext NewDb(string? name = null, long company = 22) => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(company));

    private static IConfiguration Config(bool enabled = false) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Quake:ApiKey"] = enabled ? "test-key" : null,
            ["Quake:TenantId"] = "11", ["Quake:CompanyId"] = "22",
            ["Quake:Host"] = "localhost", ["Quake:Port"] = "5432", ["Quake:Database"] = "legacy_source_test",
            ["ConnectionStrings:GuliERP"] = "Host=localhost;Port=5432;Database=legacy_source_test;Username=test",
            ["Crm:Chinaz:SettingsPath"] = Path.Combine(Path.GetTempPath(), $"guli-migration-test-{Guid.NewGuid():N}.json"),
            ["Crm:Chinaz:Enabled"] = enabled ? "true" : "false",
            ["Crm:Chinaz:AuthorizationReference"] = enabled ? "test-reference" : null
        }).Build();

    private static LegacySourceMigrationService NewService(CrmDbContext db, IConfiguration config, long company = 22)
    {
        var chinaz = new ChinazConnectorSettings(config);
        if (config.GetValue<bool>("Crm:Chinaz:Enabled"))
            chinaz.Update(11, company, new(true, "synthetic-test-reference"));
        return new(db, new MutableCurrentTenant(11), new MutableCurrentCompany(company),
            new QuakeRuntimeCredentialStore(config), chinaz);
    }
}
