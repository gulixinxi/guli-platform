using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class IdentityCandidateBatchFacts
{
    private static CrmDbContext Db(string name, long company = 22) => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(company));

    private static IdentityBatchRow Row(string domain = "example.com", string name = "甲企业") =>
        new(domain, name, "https://seo.chinaz.com/example.com", "第三方页面显示甲企业", DateTime.UtcNow);

    [Fact]
    public async Task PreviewDoesNotWrite_CommitCreatesPendingOnce()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1, Domain = "example.com" });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateBatchService(db, new IdentityCandidateService(db));
        var input = new IdentityBatchRequest([Row()], "synthetic.csv", new string('a', 64));
        var preview = await service.ProcessAsync(input, 300, false, CancellationToken.None);
        Assert.Equal("ready", Assert.Single(preview.Items).Status);
        Assert.Empty(await db.IdentityCandidates.ToListAsync());
        var imported = await service.ProcessAsync(input, 300, true, CancellationToken.None);
        Assert.Equal(1, imported.Created);
        Assert.Equal("pending", Assert.Single(await db.IdentityCandidates.ToListAsync()).Status);
        var evidence = Assert.Single(await db.IdentityEvidence.ToListAsync());
        Assert.Equal("third_party", evidence.SourceType);
        Assert.Contains("synthetic.csv", evidence.ClaimText);
        var replay = await service.ProcessAsync(input, 300, true, CancellationToken.None);
        Assert.Equal("existing_candidate", Assert.Single(replay.Items).Status);
        Assert.Equal(0, replay.Created);
    }

    [Fact]
    public async Task AmbiguousAndCrossCompanyAssetsAreNotImported()
    {
        var dbName = Guid.NewGuid().ToString("N");
        using (var other = Db(dbName, 23))
        {
            other.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1, Domain = "other.com" });
            await other.SaveChangesAsync();
        }
        using var db = Db(dbName);
        db.QuakeAssetRecords.AddRange(new QuakeAssetRecord { Id = 2, Domain = "example.com" },
            new QuakeAssetRecord { Id = 3, Domain = "example.com" });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateBatchService(db, new IdentityCandidateService(db));
        var result = await service.ProcessAsync(new([Row(), Row("other.com", "乙企业")]), 300, true, CancellationToken.None);
        Assert.Equal(["ambiguous_asset", "no_asset"], result.Items.Select(i => i.Status));
        Assert.Empty(await db.IdentityCandidates.ToListAsync());
        var chosen = await service.ProcessAsync(new([Row() with { AssetId = 2 }]), 300, false, CancellationToken.None);
        Assert.Equal("ready", Assert.Single(chosen.Items).Status);
    }

    [Fact]
    public async Task InvalidAndDuplicateInputCannotCreateCandidates()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 1, Domain = "example.com" });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateBatchService(db, new IdentityCandidateService(db));
        var result = await service.ProcessAsync(new([Row("192.0.2.1"), Row("example.com", "甲**企业"), Row(), Row()]), 300, false, CancellationToken.None);
        Assert.Equal(["invalid", "invalid", "ready", "duplicate_input"], result.Items.Select(i => i.Status));
        Assert.Empty(await db.IdentityCandidates.ToListAsync());
    }
}
