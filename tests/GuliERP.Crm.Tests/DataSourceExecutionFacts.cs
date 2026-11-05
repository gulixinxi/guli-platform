using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class DataSourceExecutionFacts
{
    [Fact]
    public async Task BlocksUnreviewedUnauthorizedFieldsAndLiveAdaptersWithoutConsumingQuota()
    {
        using var db = NewDb();
        var executor = NewExecutor(db);
        var mock = new MockAdapter("registry", true, SourceAdapterOutcome.Success);
        Assert.Equal("not_configured", (await executor.ExecuteAsync(Query("company_name"), mock, default)).Status);
        var registration = NewRegistration(db);
        await registration.SaveAsync("registry", new(0, "draft", "test-agreement", "company_name", 2, 3,
            "synthetic", 7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)), 1, default);
        Assert.Equal("not_configured", (await executor.ExecuteAsync(Query("company_name"), mock, default)).Status);
        await registration.SaveAsync("registry", new(1, "reviewed", "test-agreement", "company_name", 2, 3,
            "synthetic", 7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)), 1, default);
        Assert.Equal("field_denied", (await executor.ExecuteAsync(Query("credit_code"), mock, default)).Status);
        Assert.Equal("not_configured", (await executor.ExecuteAsync(Query("company_name"),
            new MockAdapter("registry", false, SourceAdapterOutcome.Success), default)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(
            new("registry", "domain", "example.com", ["company_name"]), mock, default));
        Assert.Empty(await db.DataSourceExecutionAttempts.ToListAsync());
        Assert.Equal(0, mock.Calls);
    }

    [Fact]
    public async Task CountsEachAttemptAndStopsAtDailyQuotaAcrossRetries()
    {
        using var db = NewDb();
        await ReviewAsync(db, daily: 2, monthly: 5);
        var mock = new MockAdapter("registry", true,
            SourceAdapterOutcome.TemporaryFailure, SourceAdapterOutcome.RateLimited, SourceAdapterOutcome.Success);
        var executor = NewExecutor(db);
        var result = await executor.ExecuteAsync(Query("company_name"), mock, default);
        Assert.Equal("quota_exhausted", result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, mock.Calls);
        Assert.Equal(new[] { "temporary_failure", "rate_limited" },
            await db.DataSourceExecutionAttempts.OrderBy(x => x.AttemptNumber).Select(x => x.Status).ToArrayAsync());
        var usage = await executor.ListUsageAsync(default);
        Assert.Contains("DailyUsed = 2", usage.Single().ToString());
        Assert.Equal("quota_exhausted", (await executor.ExecuteAsync(Query("company_name"), mock, default)).Status);
    }

    [Fact]
    public async Task ReturnsOnlyRequestedFieldsAndDoesNotRetryAmbiguousFailure()
    {
        using var db = NewDb();
        await ReviewAsync(db, daily: 3, monthly: 3);
        var executor = NewExecutor(db);
        var success = new MockAdapter("registry", true, SourceAdapterOutcome.Success);
        var result = await executor.ExecuteAsync(Query("company_name"), success, default);
        Assert.Equal("succeeded", result.Status);
        Assert.Equal("示例企业", result.Fields!["company_name"]);
        Assert.DoesNotContain("credit_code", result.Fields.Keys);
        var failing = new ThrowingAdapter();
        var uncertain = await executor.ExecuteAsync(Query("company_name"), failing, default);
        Assert.Equal("uncertain", uncertain.Status);
        Assert.Equal(1, uncertain.Attempts);
        Assert.Equal(1, failing.Calls);
        Assert.Equal(2, await db.DataSourceExecutionAttempts.CountAsync());
    }

    [Fact]
    public async Task ConcurrentReservationsCannotExceedQuota()
    {
        var database = Guid.NewGuid().ToString("N");
        using var setup = NewDb(database);
        await ReviewAsync(setup, daily: 1, monthly: 1);
        using var firstDb = NewDb(database);
        using var secondDb = NewDb(database);
        var first = new MockAdapter("registry", true, SourceAdapterOutcome.Success);
        var second = new MockAdapter("registry", true, SourceAdapterOutcome.Success);
        var results = await Task.WhenAll(
            NewExecutor(firstDb).ExecuteAsync(Query("company_name"), first, default),
            NewExecutor(secondDb).ExecuteAsync(Query("company_name"), second, default));
        Assert.Equal(new[] { "quota_exhausted", "succeeded" }, results.Select(x => x.Status).OrderBy(x => x));
        Assert.Equal(1, first.Calls + second.Calls);
        Assert.Equal(1, await setup.DataSourceExecutionAttempts.CountAsync());
    }

    [Fact]
    public async Task ExpiredRegistrationStopsBeforeReservation()
    {
        using var db = NewDb();
        await NewRegistration(db).SaveAsync("registry", new(0, "draft", "test-agreement", "company_name",
            1, 1, "synthetic", 7, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1)), 1, default);
        var row = await db.DataSourceAuthorizations.SingleAsync();
        row.Status = "reviewed"; // Simulates a once-valid registration that has reached its expiry date.
        await db.SaveChangesAsync();
        var result = await NewExecutor(db).ExecuteAsync(Query("company_name"),
            new MockAdapter("registry", true, SourceAdapterOutcome.Success), default);
        Assert.Equal("expired", result.Status);
        Assert.Empty(await db.DataSourceExecutionAttempts.ToListAsync());
    }

    [Fact]
    public async Task AnotherCompanyCannotUseRegistrationOrQuota()
    {
        var database = Guid.NewGuid().ToString("N");
        using var owner = NewDb(database);
        await ReviewAsync(owner, daily: 1, monthly: 1);
        using var other = NewDb(database, companyId: 23);
        var result = await new DataSourceExecutionService(other,
            new MutableCurrentTenant(11), new MutableCurrentCompany(23))
            .ExecuteAsync(Query("company_name"), new MockAdapter("registry", true, SourceAdapterOutcome.Success), default);
        Assert.Equal("not_configured", result.Status);
        Assert.Empty(await other.DataSourceExecutionAttempts.ToListAsync());
    }

    private static CrmDbContext NewDb(string? name = null, long companyId = 22) => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(companyId));

    private static DataSourceAuthorizationService NewRegistration(CrmDbContext db) =>
        new(db, new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    private static DataSourceExecutionService NewExecutor(CrmDbContext db) =>
        new(db, new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    private static SourceLookupRequest Query(params string[] fields) =>
        new("registry", "company_name", "示例企业", fields);

    private static Task ReviewAsync(CrmDbContext db, int daily, int monthly) =>
        NewRegistration(db).SaveAsync("registry", new(0, "reviewed", "test-agreement", "company_name",
            daily, monthly, "synthetic", 7, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)), 1, default);

    private sealed class MockAdapter(string source, bool synthetic, params SourceAdapterOutcome[] outcomes) : IDataSourceAdapter
    {
        public string SourceCode => source;
        public bool IsSynthetic => synthetic;
        public int Calls { get; private set; }
        public Task<SourceAdapterReply> ExecuteAsync(SourceLookupRequest request, CancellationToken ct)
        {
            var outcome = outcomes[Math.Min(Calls++, outcomes.Length - 1)];
            return Task.FromResult(new SourceAdapterReply(outcome,
                new Dictionary<string, string> { ["company_name"] = "示例企业", ["credit_code"] = "不应返回" }));
        }
    }

    private sealed class ThrowingAdapter : IDataSourceAdapter
    {
        public string SourceCode => "registry";
        public bool IsSynthetic => true;
        public int Calls { get; private set; }
        public Task<SourceAdapterReply> ExecuteAsync(SourceLookupRequest request, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("ambiguous provider result");
        }
    }
}
