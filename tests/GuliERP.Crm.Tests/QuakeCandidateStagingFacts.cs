using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class QuakeCandidateStagingFacts
{
    private static QuakeAssetService CreateService(CrmDbContext db) => new(
        new HttpClient(),
        new QuakeRuntimeCredentialStore(new ConfigurationBuilder().Build()),
        db,
        new QuakeAssetCleaner(),
        NullLogger<QuakeAssetService>.Instance);

    [Fact]
    public async Task Search_ScansAllCompanyAssetsBeforePaging()
    {
        using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var service = CreateService(db);
        await service.ImportAsync([
            new QuakeAssetInput { Domain = "staging-one.example.com", Port = 443 },
            new QuakeAssetInput { Domain = "other.example.com", Port = 443 },
            new QuakeAssetInput { Domain = "staging-two.example.com", Port = 443 }
        ], 11, 22);
        var first = await service.SearchAsync("STAGING", null, null, 0, 1);
        var second = await service.SearchAsync("staging", null, null, 1, 1);
        Assert.Equal(2, first.Total);
        Assert.Equal(2, second.Total);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Equal(first.Items[0].Id.ToString(), first.Items[0].IdText);
    }

    [Fact]
    public async Task SeparateImportCalls_CanShareOneFileBatchId()
    {
        using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var service = CreateService(db);
        var batch = Guid.NewGuid();
        var first = await service.ImportAsync(
            [new QuakeAssetInput { Domain = "one.example.com", Port = 80 }], 11, 22, batchId: batch);
        var second = await service.ImportAsync(
            [new QuakeAssetInput { Domain = "two.example.com", Port = 443 }], 11, 22, batchId: batch);
        Assert.Equal(batch, first.BatchId);
        Assert.Equal(batch, second.BatchId);
        Assert.All(await db.QuakeAssetRecords.ToListAsync(), asset => Assert.Equal(batch, asset.BatchId));
    }

    [Fact]
    public async Task Reimport_DoesNotOverwriteFirstObservationOrCreateLead()
    {
        using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var service = CreateService(db);

        var first = await service.ImportAsync([
            new QuakeAssetInput { Domain = "Example.COM", Port = 443, Title = "首次观测", RawJson = "{\"v\":1}" }
        ], 11, 22);
        var second = await service.ImportAsync([
            new QuakeAssetInput { Domain = "example.com.", Port = 443, Title = "第二次观测", RawJson = "{\"v\":2}" }
        ], 11, 22);

        Assert.Equal(1, first.Stored);
        Assert.Equal(0, second.Stored);
        Assert.Equal(1, second.Skipped);
        var asset = Assert.Single(await db.QuakeAssetRecords.ToListAsync());
        Assert.Equal("example.com", asset.Domain);
        Assert.Equal("首次观测", asset.Title);
        Assert.Equal("{\"v\":1}", asset.RawJson);
        Assert.False(asset.Imported);
        Assert.Empty(await db.Leads.ToListAsync());
    }

    [Fact]
    public async Task SameDomainInAnotherCompany_IsSeparateCandidate()
    {
        var database = Guid.NewGuid().ToString("N");
        using (var firstDb = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(database).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22)))
        {
            var result = await CreateService(firstDb).ImportAsync([
                new QuakeAssetInput { Domain = "example.com", Port = 443 }
            ], 11, 22);
            Assert.Equal(1, result.Stored);
        }

        using var secondDb = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(database).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(23));
        var second = await CreateService(secondDb).ImportAsync([
            new QuakeAssetInput { Domain = "example.com", Port = 443 }
        ], 11, 23);
        Assert.Equal(1, second.Stored);
        Assert.Equal(0, second.Skipped);
        Assert.Single(await secondDb.QuakeAssetRecords.ToListAsync());
    }

    [Fact]
    public async Task Preview_ReportsExistingAndBatchDuplicatesWithoutWriting()
    {
        using var db = new CrmDbContext(
            new DbContextOptionsBuilder<CrmDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var service = CreateService(db);
        await service.ImportAsync([
            new QuakeAssetInput { Domain = "old.example.com", Port = 443 }
        ], 11, 22);

        var preview = await service.PreviewAsync([
            new QuakeAssetInput { Domain = "OLD.EXAMPLE.COM.", Port = 443 },
            new QuakeAssetInput { Domain = "new.example.com", Port = 443 },
            new QuakeAssetInput { Domain = "NEW.example.com.", Port = 443 },
        ]);

        Assert.Equal((3, 1, 1, 1),
            (preview.Total, preview.New, preview.Existing, preview.DuplicateInRequest));
        Assert.NotNull(preview.Items[0].ExistingAssetId);
        Assert.False(preview.Items[1].DuplicateInRequest);
        Assert.True(preview.Items[2].DuplicateInRequest);
        Assert.Single(await db.QuakeAssetRecords.ToListAsync());
    }
}
