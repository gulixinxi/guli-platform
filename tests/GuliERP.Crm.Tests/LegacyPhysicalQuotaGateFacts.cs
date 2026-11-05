using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using GuliERP.Foundation.Kernel;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class LegacyPhysicalQuotaGateFacts
{
    [Fact]
    public async Task QuakeFallback_ReservesBeforeEachSyntheticPhysicalAttempt()
    {
        using var db = NewDb();
        await RegisterAsync(db, "quake", "domain", 2, 9);
        var gate = Executor(db);
        var adapter = new FakeAdapter("quake", SourceAdapterOutcome.RateLimited, SourceAdapterOutcome.Success);
        var query = new SourceLookupRequest("quake", "query", "synthetic-only", ["domain"]);

        var first = await gate.ExecuteOneSyntheticAttemptAsync(query, adapter, default);
        var second = await gate.ExecuteOneSyntheticAttemptAsync(query, adapter, default);
        var third = await gate.ExecuteOneSyntheticAttemptAsync(query, adapter, default);

        Assert.Equal(new[] { "rate_limited", "succeeded", "quota_exhausted" },
            new[] { first.Status, second.Status, third.Status });
        Assert.Equal(2, adapter.Calls);
        Assert.Equal(2, await db.DataSourceExecutionAttempts.CountAsync());
        Assert.All(await db.DataSourceExecutionAttempts.ToListAsync(), row => Assert.NotNull(row.CompletedAt));
    }

    [Fact]
    public async Task ChinazBatch_StopsAtFifthReservation()
    {
        using var db = NewDb();
        await RegisterAsync(db, "chinaz", "company_name", 5, 5);
        var gate = Executor(db);
        var adapter = new FakeAdapter("chinaz", SourceAdapterOutcome.Success);
        var outcomes = new List<string>();
        for (var i = 1; i <= 10; i++)
            outcomes.Add((await gate.ExecuteOneSyntheticAttemptAsync(
                new("chinaz", "domain", $"site{i}.example", ["company_name"]), adapter, default)).Status);
        Assert.Equal(5, outcomes.Count(x => x == "succeeded"));
        Assert.Equal(5, outcomes.Count(x => x == "quota_exhausted"));
        Assert.Equal(5, adapter.Calls);
        Assert.Equal(5, await db.DataSourceExecutionAttempts.CountAsync());
    }

    [Fact]
    public async Task MonthlyQuota_StopsBeforeThirdPhysicalAttempt()
    {
        using var db = NewDb();
        await RegisterAsync(db, "chinaz", "company_name", 10, 2);
        var gate = Executor(db);
        var adapter = new FakeAdapter("chinaz", SourceAdapterOutcome.Success);
        var statuses = new List<string>();
        for (var i = 1; i <= 3; i++)
            statuses.Add((await gate.ExecuteOneSyntheticAttemptAsync(
                new("chinaz", "domain", $"site{i}.example", ["company_name"]), adapter, default)).Status);
        Assert.Equal(new[] { "succeeded", "succeeded", "quota_exhausted" }, statuses);
        Assert.Equal(2, adapter.Calls);
    }

    [Fact]
    public async Task ConcurrentPhysicalReservations_AllowOnlyOneAdapterCall()
    {
        var database = Guid.NewGuid().ToString("N");
        using var setup = NewDb(database);
        await RegisterAsync(setup, "quake", "domain", 1, 1);
        using var left = NewDb(database);
        using var right = NewDb(database);
        var adapter = new FakeAdapter("quake", SourceAdapterOutcome.Success);
        var query = new SourceLookupRequest("quake", "query", "synthetic-only", ["domain"]);
        var results = await Task.WhenAll(
            Executor(left).ExecuteOneSyntheticAttemptAsync(query, adapter, default),
            Executor(right).ExecuteOneSyntheticAttemptAsync(query, adapter, default));
        Assert.Equal(1, results.Count(x => x.Status == "succeeded"));
        Assert.Equal(1, results.Count(x => x.Status == "quota_exhausted"));
        Assert.Equal(1, adapter.Calls);
        Assert.Equal(1, await setup.DataSourceExecutionAttempts.CountAsync());
    }

    [Fact]
    public async Task CancellationAfterReservation_RemainsUncertain_WithoutSecondCall()
    {
        using var db = NewDb();
        await RegisterAsync(db, "quake", "domain", 2, 2);
        using var cancel = new CancellationTokenSource();
        var adapter = new CancelAdapter(cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor(db)
            .ExecuteOneSyntheticAttemptAsync(new("quake", "query", "synthetic-only", ["domain"]), adapter, cancel.Token));
        Assert.Equal(1, adapter.Calls);
        Assert.Equal("uncertain", (await db.DataSourceExecutionAttempts.SingleAsync()).Status);
    }

    [Fact]
    public async Task ReservationFailure_AndLiveAdapter_NeverInvokeSupplierCallback()
    {
        var options = Options();
        using var db = new FailingReservationDb(options, new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        await RegisterAsync(db, "quake", "domain", 2, 2);
        var adapter = new FakeAdapter("quake", SourceAdapterOutcome.Success);
        var query = new SourceLookupRequest("quake", "query", "synthetic-only", ["domain"]);
        Assert.Equal("not_configured", (await Executor(db).ExecuteOneSyntheticAttemptAsync(
            query, new FakeAdapter("quake", SourceAdapterOutcome.Success, synthetic: false), default)).Status);
        db.FailReservations = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(db)
            .ExecuteOneSyntheticAttemptAsync(query, adapter, default));
        Assert.Equal(0, adapter.Calls);
    }

    private static DbContextOptions<CrmDbContext> Options(string? name = null) =>
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options;
    private static CrmDbContext NewDb(string? name = null) => new(Options(name), new MutableCurrentTenant(11), new MutableCurrentCompany(22));
    private static DataSourceExecutionService Executor(CrmDbContext db) =>
        new(db, new MutableCurrentTenant(11), new MutableCurrentCompany(22));
    private static async Task RegisterAsync(CrmDbContext db, string source, string fields, int daily, int monthly) =>
        await new DataSourceAuthorizationService(db, new MutableCurrentTenant(11), new MutableCurrentCompany(22))
            .SaveAsync(source, new(0, "reviewed", "synthetic-test-only", fields, daily, monthly,
                "synthetic", 1, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(1)), 1, default);

    private sealed class FakeAdapter(string source, SourceAdapterOutcome first,
        SourceAdapterOutcome? second = null, bool synthetic = true) : IDataSourceAdapter
    {
        private int _calls;
        public int Calls => _calls;
        public string SourceCode => source;
        public bool IsSynthetic => synthetic;
        public Task<SourceAdapterReply> ExecuteAsync(SourceLookupRequest request, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls);
            return Task.FromResult(new SourceAdapterReply(call == 1 ? first : second ?? first));
        }
    }
    private sealed class CancelAdapter(CancellationTokenSource cancel) : IDataSourceAdapter
    {
        public int Calls { get; private set; }
        public string SourceCode => "quake";
        public bool IsSynthetic => true;
        public Task<SourceAdapterReply> ExecuteAsync(SourceLookupRequest request, CancellationToken ct)
        {
            Calls++;
            cancel.Cancel();
            throw new OperationCanceledException(ct);
        }
    }
    private sealed class FailingReservationDb(DbContextOptions<CrmDbContext> options,
        ICurrentTenant tenant, ICurrentCompany company) : CrmDbContext(options, tenant, company)
    {
        public bool FailReservations { get; set; }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailReservations && ChangeTracker.Entries<DataSourceExecutionAttempt>()
                .Any(x => x.State == EntityState.Added)) throw new InvalidOperationException("synthetic db failure");
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
