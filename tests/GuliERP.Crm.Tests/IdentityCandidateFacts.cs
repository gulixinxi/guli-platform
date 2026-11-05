using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class IdentityCandidateFacts
{
    private static CrmDbContext Db(string name, long company = 22) => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(company));

    [Fact]
    public async Task CandidateReview_PreservesEvidenceAndDecisionHistory_WithoutCreatingCrmRecords()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 101, Domain = "example.com", Port = 443 });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateService(db);
        await service.CreateAsync(101, new("甲企业", new("manual_note", "访谈笔记", "销售初步推测，尚无官网证据", DateTime.UtcNow)), 300, CancellationToken.None);
        var candidate = Assert.Single(await db.IdentityCandidates.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(candidate.Id,
            new("confirm", "目前只有销售备注不能确认", 1), 301, CancellationToken.None));

        await service.AddEvidenceAsync(candidate.Id,
            new("public_site", "https://example.com/about", "官网关于我们页显示甲企业名称", DateTime.UtcNow), 302, CancellationToken.None);
        await service.ReviewAsync(candidate.Id, new("confirm", "官网名称与资产域名对应，人工完成身份核验", 1), 301, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddEvidenceAsync(candidate.Id,
            new("manual_note", "后续笔记", "已确认后不得直接修改证据集合", DateTime.UtcNow), 302, CancellationToken.None));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.ReviewAsync(candidate.Id,
            new("reject", "使用过期版本重复提交审核决定", 1), 303, CancellationToken.None));
        await service.ReviewAsync(candidate.Id, new("reopen", "发现官网归属可能已经改变，需要重新核对", 2), 304, CancellationToken.None);

        Assert.Equal("pending", candidate.Status);
        Assert.Equal(3, candidate.Version);
        Assert.Equal(2, await db.IdentityEvidence.CountAsync());
        Assert.Equal(2, await db.IdentityReviewEvents.CountAsync());
        Assert.Empty(await db.Leads.ToListAsync());
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task ThirdPartyPage_IsRecordedButCannotAloneConfirmIdentity()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 102, Domain = "example.com", Port = 443 });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateService(db);
        await service.CreateAsync(102, new("甲企业", new("third_party", "https://seo.chinaz.com/example.com",
            "第三方页面显示该域名对应甲企业", DateTime.UtcNow)), 300, CancellationToken.None);
        var candidate = Assert.Single(await db.IdentityCandidates.ToListAsync());
        var evidence = Assert.Single(await db.IdentityEvidence.ToListAsync());
        Assert.Equal("third_party", evidence.SourceType);
        Assert.Equal("https://seo.chinaz.com/example.com", evidence.SourceReference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(candidate.Id,
            new("confirm", "只有第三方页面记录，仍需独立核对", 1), 301, CancellationToken.None));
        Assert.Equal("pending", candidate.Status);
    }

    [Fact]
    public async Task AssetAndCandidate_AreInvisibleAcrossCompanies()
    {
        var name = Guid.NewGuid().ToString("N");
        long candidateId;
        using (var first = Db(name))
        {
            first.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 201, Domain = "example.com", Port = 443 });
            await first.SaveChangesAsync();
            var service = new IdentityCandidateService(first);
            await service.CreateAsync(201, new("甲企业", new("registry", "登记查询页", "公开登记主体名称为甲企业", DateTime.UtcNow)), 300, CancellationToken.None);
            candidateId = Assert.Single(await first.IdentityCandidates.ToListAsync()).Id;
        }
        using var other = Db(name, 23);
        var otherService = new IdentityCandidateService(other);
        Assert.Null(await otherService.GetForAssetAsync(201, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => otherService.AddEvidenceAsync(candidateId,
            new("manual_note", "笔记", "跨公司写入尝试", DateTime.UtcNow), 300, CancellationToken.None));
        Assert.Empty(await other.IdentityCandidates.ToListAsync());
    }

    [Fact]
    public async Task IpOnlyAsset_CannotBecomeEnterpriseCandidate()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 301, Domain = "192.0.2.1", IpAddress = "192.0.2.1" });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateService(db);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(301,
            new("推测企业", new("manual_note", "笔记", "只有 IP 地址", DateTime.UtcNow)), 300, CancellationToken.None));
        Assert.Empty(await db.IdentityCandidates.ToListAsync());
    }

    [Fact]
    public async Task ExpiredExternalEvidence_CannotConfirmUntilFreshEvidenceIsAdded()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 401, Domain = "example.com", Port = 443 });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateService(db);
        await service.CreateAsync(401, new("甲企业", new("registry", "公开登记页", "一年前查询的登记资料", DateTime.UtcNow.AddDays(-366))), 300, CancellationToken.None);
        var candidate = Assert.Single(await db.IdentityCandidates.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(candidate.Id,
            new("confirm", "仅有过期资料，需要近期来源", 1), 301, CancellationToken.None));
        await service.AddEvidenceAsync(candidate.Id,
            new("public_site", "https://example.com/about", "近期官网主体说明", DateTime.UtcNow), 300, CancellationToken.None);
        await service.ReviewAsync(candidate.Id, new("confirm", "近期官网说明已核对，资料有效", 1), 301, CancellationToken.None);
        Assert.Equal("confirmed", candidate.Status);
    }

    [Fact]
    public async Task OneAsset_CanHaveConflictingCandidatesWithoutOverwritingEither()
    {
        using var db = Db(Guid.NewGuid().ToString("N"));
        db.QuakeAssetRecords.Add(new QuakeAssetRecord { Id = 501, Domain = "example.com", Port = 443 });
        await db.SaveChangesAsync();
        var service = new IdentityCandidateService(db);
        await service.CreateAsync(501, new("服务商甲", new("icp", "备案查询", "备案主办方为服务商甲", DateTime.UtcNow)), 300, CancellationToken.None);
        await service.CreateAsync(501, new("运营方乙", new("public_site", "https://example.com/about", "网站声明运营方乙", DateTime.UtcNow)), 300, CancellationToken.None);
        var candidates = await db.IdentityCandidates.ToListAsync();
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("pending", candidate.Status));
        Assert.Equal(2, await db.IdentityEvidence.CountAsync());
        Assert.Empty(await db.Leads.ToListAsync());
    }
}
