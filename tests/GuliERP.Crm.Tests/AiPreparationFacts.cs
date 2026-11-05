using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class AiPreparationFacts
{
    private static CrmDbContext Db() => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    [Fact]
    public async Task UnconfirmedIdentityAndMissingEvidence_AreExplicitlyUnsupported()
    {
        using var db = Db();
        var candidate = new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "模拟企业" };
        db.IdentityCandidates.Add(candidate);
        await db.SaveChangesAsync();
        var svc = new AiPreparationService(db, new SimulatedAiPreparationAdapter(), new SimulatedOnlyAiPreparationGate());
        await svc.CreateAsync(101, new(Guid.NewGuid(), "company_profile", 1), 7, default);
        Assert.Equal("unsupported", (await db.AiPreparationDrafts.SingleAsync()).Status);
        candidate.Status = "confirmed";
        await db.SaveChangesAsync();
        await svc.CreateAsync(101, new(Guid.NewGuid(), "company_profile", 1), 7, default);
        Assert.All(await db.AiPreparationDrafts.ToListAsync(), row => Assert.Equal("unsupported", row.Status));
    }

    [Fact]
    public async Task BudgetIdentityEvidenceReviewAndVersion_AreTracked()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "模拟企业", Status = "confirmed" });
        db.IdentityEvidence.Add(new IdentityEvidence { Id = 201, CandidateId = 101, SourceType = "icp", SourceReference = "https://example.com", ClaimText = "模拟备案", ObservedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var svc = new AiPreparationService(db, new SimulatedAiPreparationAdapter(), new SimulatedOnlyAiPreparationGate());
        var key = Guid.NewGuid();
        await svc.CreateAsync(101, new(key, "company_profile", 0), 7, default);
        Assert.Equal("cost_limit", (await db.AiPreparationDrafts.SingleAsync()).Status);
        await svc.CreateAsync(101, new(key, "company_profile", 0), 7, default);
        Assert.Single(await db.AiPreparationDrafts.ToListAsync());
        await svc.CreateAsync(101, new(Guid.NewGuid(), "company_profile", 1), 7, default);
        var draft = await db.AiPreparationDrafts.SingleAsync(d => d.Status == "draft");
        Assert.Equal(2, draft.Version);
        Assert.Equal(0, draft.ActualCostCents);
        await svc.ReviewAsync(101, draft.Id, new("approved", "已核对公开来源和引用，可作为内部参考草稿"), 8, default);
        Assert.Equal("approved", (await db.AiPreparationDrafts.SingleAsync(d => d.Id == draft.Id)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReviewAsync(101, draft.Id, new("rejected", "不应重复审核同一版本"), 9, default));
    }

    [Fact]
    public async Task GeneratorFailure_IsSavedWithoutUnreviewedApproval()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "模拟企业", Status = "confirmed" });
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport { Id = 301, CandidateId = 101, Version = 1, Url = "https://example.com/", Status = "completed" });
        await db.SaveChangesAsync();
        var svc = new AiPreparationService(db, new FailingAdapter(), new PermissiveTestGate());
        await svc.CreateAsync(101, new(Guid.NewGuid(), "diagnostic_summary", 1), 7, default);
        Assert.Equal("failed", (await db.AiPreparationDrafts.SingleAsync()).Status);
    }

    [Fact]
    public async Task NonSimulatorIsBlockedBeforeItsEstimateOrGenerationRuns()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "模拟企业", Status = "confirmed" });
        db.WebsiteDiagnosticReports.Add(new WebsiteDiagnosticReport { Id = 301, CandidateId = 101, Version = 1, Url = "https://example.com/", Status = "completed" });
        await db.SaveChangesAsync();
        var adapter = new FailingAdapter();
        var svc = new AiPreparationService(db, adapter, new SimulatedOnlyAiPreparationGate());
        await svc.CreateAsync(101, new(Guid.NewGuid(), "diagnostic_summary", 1), 7, default);
        var row = await db.AiPreparationDrafts.SingleAsync();
        Assert.Equal("failed", row.Status);
        Assert.Equal("blocked-provider", row.ProviderCode);
        Assert.Equal(0, adapter.Attempts);
        Assert.Equal(0, row.ActualCostCents);
    }

    [Fact]
    public async Task LongDigitContactExcerpt_IsNotAcceptedAsIcebreakerInput()
    {
        using var db = Db();
        db.IdentityCandidates.Add(new IdentityCandidate { Id = 101, AssetId = 1, Domain = "example.com", ProposedName = "模拟企业", Status = "confirmed" });
        db.WebsiteContentEvidence.Add(new WebsiteContentEvidence { Id = 401, CandidateId = 101, ReportId = 301,
            SourceUrl = "https://example.com/about", Excerpt = "联系电话 01012345678", Status = "accepted" });
        await db.SaveChangesAsync();
        var svc = new AiPreparationService(db, new SimulatedAiPreparationAdapter(), new SimulatedOnlyAiPreparationGate());
        await svc.CreateAsync(101, new(Guid.NewGuid(), "icebreaker", 1), 7, default);
        Assert.Equal("unsupported", (await db.AiPreparationDrafts.SingleAsync()).Status);
    }

    private sealed class FailingAdapter : IAiPreparationAdapter
    {
        public int Attempts { get; private set; }
        public string ProviderCode => "test-failure";
        public string ContractVersion => "prep-v1";
        public int EstimateCostCents(AiPreparationContext context) { Attempts++; return 1; }
        public Task<IReadOnlyList<AiPreparationClaim>> GenerateAsync(AiPreparationContext context, CancellationToken ct) { Attempts++; throw new InvalidOperationException("synthetic provider failure"); }
    }

    private sealed class PermissiveTestGate : IAiPreparationExecutionGate
    {
        public bool CanExecute(IAiPreparationAdapter adapter) => true;
        public object Status() => new { testOnly = true };
    }
}
