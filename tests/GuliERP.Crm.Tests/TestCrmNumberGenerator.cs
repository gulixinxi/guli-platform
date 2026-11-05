using GuliERP.Crm.Infrastructure.Services;

namespace GuliERP.Crm.Tests;

internal sealed class TestCrmNumberGenerator : ICrmNumberGenerator
{
    private long _company;
    private long _deal;

    public Task<string> NextCompanyNoAsync(CancellationToken ct) =>
        Task.FromResult($"CO20261027{Interlocked.Increment(ref _company):D4}");

    public Task<string> NextDealNoAsync(CancellationToken ct) =>
        Task.FromResult($"DE20261027{Interlocked.Increment(ref _deal):D4}");
}
