using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GuliERP.Crm.Tests;

public sealed class SignalSourceUrlFacts
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,unsafe")]
    [InlineData("https://user:pass@example.com/path")]
    [InlineData("/relative/path")]
    public async Task Create_RejectsNonWebOrCredentialedSourceWithoutSaving(string sourceUrl)
    {
        using var db = Db();
        var service = new SignalService(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Command(sourceUrl)));

        Assert.Empty(db.Signals);
    }

    [Theory]
    [InlineData("https://example.com/path")]
    [InlineData("http://example.com/path")]
    public async Task Create_AcceptsWebSource(string sourceUrl)
    {
        using var db = Db();
        var result = await new SignalService(db).CreateAsync(Command(sourceUrl));

        Assert.Equal(sourceUrl, result.SourceUrl);
        Assert.Single(db.Signals);
    }

    private static CrmDbContext Db() => new(
        new DbContextOptionsBuilder<CrmDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new MutableCurrentTenant(11), new MutableCurrentCompany(22));

    private static CreateSignalCommand Command(string sourceUrl) => new(
        null, null, "测试企业", default(SignalType), "测试信号", null,
        "manual", sourceUrl, null, 80, null, null, null);
}
