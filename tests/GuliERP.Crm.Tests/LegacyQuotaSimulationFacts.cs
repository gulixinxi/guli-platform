using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class LegacyQuotaSimulationFacts
{
    [Fact]
    public async Task SimulatesPhysicalAttemptsIncludingFallbackAndFailure_WithoutWritingRows()
    {
        using var db = NewDb();
        db.LegacyRequestObservations.AddRange(
            new LegacyRequestObservation { SourceCode = "quake", Route = "search", Outcome = "http_429", ObservedAt = DateTime.UtcNow },
            new LegacyRequestObservation { SourceCode = "quake", Route = "search", Outcome = "http_200", ObservedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new LegacyQuotaSimulationService(db, new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        var result = await service.PreviewAsync(new("quake", 2, 3, 10), default);
        Assert.Equal(2, result.ObservedDaily);
        Assert.True(result.WouldExceedDaily);
        Assert.False(result.WouldExceedMonthly);
        var monthly = await service.PreviewAsync(new("quake", 2, 10, 3), default);
        Assert.False(monthly.WouldExceedDaily);
        Assert.True(monthly.WouldExceedMonthly);
        Assert.False(result.HistoryComplete || result.EnforcesLimit || result.PerformsExternalRequest);
        Assert.Equal(2, await db.LegacyRequestObservations.CountAsync());
    }

    [Fact]
    public async Task BatchOfTenAndOtherCompanyAreSeparated()
    {
        var name = Guid.NewGuid().ToString("N");
        using var owner = NewDb(name);
        owner.LegacyRequestObservations.Add(new LegacyRequestObservation
            { SourceCode = "chinaz", Route = "batch_lookup", Outcome = "http_500" });
        await owner.SaveChangesAsync();
        using var other = NewDb(name, 23);
        var preview = await new LegacyQuotaSimulationService(other, new MutableCurrentTenant(11),
            new MutableCurrentCompany(23)).PreviewAsync(new("chinaz", 10, 10, 10), default);
        Assert.Equal(0, preview.ObservedDaily);
        Assert.False(preview.WouldExceedDaily);
        Assert.Equal(1, await owner.LegacyRequestObservations.CountAsync());
    }

    [Fact]
    public async Task InvalidSimulationIsRejectedBeforeDatabaseUse()
    {
        using var db = NewDb();
        var service = new LegacyQuotaSimulationService(db, new MutableCurrentTenant(11), new MutableCurrentCompany(22));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(new("unknown", 1, 10, 100), default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PreviewAsync(new("quake", 11, 10, 100), default));
    }

    private static CrmDbContext NewDb(string? name = null, long company = 22) => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(company));
}
