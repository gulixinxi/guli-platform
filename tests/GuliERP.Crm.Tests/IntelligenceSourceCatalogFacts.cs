using GuliERP.Crm.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class IntelligenceSourceCatalogFacts
{
    [Fact]
    public void UnknownLicensesStayDisabledAndCatalogDoesNotExposeKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"guli-source-catalog-{Guid.NewGuid():N}.json");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Crm:Chinaz:SettingsPath"] = path,
            ["Quake:ApiKey"] = "test-secret-key",
            ["Quake:TenantId"] = "11", ["Quake:CompanyId"] = "22",
            ["Quake:Host"] = "localhost", ["Quake:Port"] = "5432", ["Quake:Database"] = "catalog_test",
            ["ConnectionStrings:GuliERP"] = "Host=localhost;Port=5432;Database=catalog_test;Username=test"
        }).Build();
        var catalog = new IntelligenceSourceCatalog(new QuakeRuntimeCredentialStore(config),
            new ChinazConnectorSettings(config));

        var items = catalog.List(11, 22);

        Assert.Equal(6, items.Count);
        Assert.Equal("connection_configured", items.Single(x => x.Code == "quake").Status);
        Assert.Equal("not_configured", items.Single(x => x.Code == "chinaz").Status);
        Assert.All(items.Where(x => x.Code is "registry" or "baidu_ads" or "recruitment"),
            item => { Assert.Equal("not_configured", item.Status); Assert.False(item.CanRequestNow); });
        Assert.DoesNotContain("test-secret-key", System.Text.Json.JsonSerializer.Serialize(items));
        Assert.Throws<ArgumentException>(() => catalog.List(0, 22));
        Assert.False(File.Exists(path));
    }
}
