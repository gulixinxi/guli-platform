using GuliERP.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GuliERP.Api.Tests;

public sealed class ReleaseDatabaseTargetGuardFacts
{
    private const string Connection =
        "Host=192.0.2.10;Port=5432;Database=gulierp_next_prod;Username=app_runtime;Password=canary-secret";

    private static IConfiguration Config(string database = "gulierp_next_prod", string host = "192.0.2.10", int port = 5432) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Host"] = host,
            ["Port"] = port.ToString(),
            ["Database"] = database,
            ["Username"] = "app_runtime",
            ["ServerAddress"] = "192.0.2.10"
        }).Build();

    [Fact]
    public void ApprovedTargetPassesLocalValidationWithoutConnecting()
    {
        var target = ReleaseDatabaseTargetGuard.Validate(Connection, Config());
        Assert.Equal("gulierp_next_prod", target.Database);
    }

    [Theory]
    [InlineData("gulierp_g2_003_test", "192.0.2.10")]
    [InlineData("gulierp_next_prod", "192.0.2.11")]
    public void WrongOrReservedTargetFailsBeforeConnection(string database, string host)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ReleaseDatabaseTargetGuard.Validate(Connection, Config(database, host)));
        Assert.DoesNotContain("canary-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingApprovedAddressFailsBeforeConnection()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ReleaseDatabaseTargetGuard.Validate(Connection, new ConfigurationBuilder().Build()));
        Assert.Contains("missing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("canary-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnavailableServerFailsWithoutLeakingCredentials()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReleaseDatabaseTargetGuard.VerifyAsync(
                "Host=127.0.0.1;Port=1;Database=gulierp_next_prod;Username=app_runtime;Password=canary-secret;Timeout=1",
                Config(host: "127.0.0.1", port: 1)));
        Assert.DoesNotContain("canary-secret", error.ToString(), StringComparison.Ordinal);
    }
}
