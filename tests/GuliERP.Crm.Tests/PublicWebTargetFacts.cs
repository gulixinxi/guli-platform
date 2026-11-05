using System.Net;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GuliERP.Crm.Tests;

public class PublicWebTargetFacts
{
    [Theory]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://192.168.1.10")]
    [InlineData("http://example.com@127.0.0.1")]
    [InlineData("http://example.com:8080")]
    [InlineData("localhost")]
    [InlineData("https://[::1]")]
    public void Host_RejectsNonPublicOrAmbiguousTargets(string input) =>
        Assert.Throws<ArgumentException>(() => PublicWebTarget.Host(input));

    [Theory]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.2.3", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("172.16.2.3", false)]
    [InlineData("192.168.2.3", false)]
    [InlineData("198.18.1.1", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("8.8.8.8", true)]
    public void IsPublicIpv4_RejectsSpecialRanges(string address, bool expected) =>
        Assert.Equal(expected, PublicWebTarget.IsPublicIpv4(IPAddress.Parse(address)));

    [Fact]
    public async Task Icp_DefaultOff_DoesNotStartExternalProcess()
    {
        var service = new IcpLookupService(NullLogger<IcpLookupService>.Instance);
        var result = await service.LookupAsync("example.com");
        Assert.Equal("disabled", result?.QueryStatus);
    }

    [Fact]
    public async Task Icp_InvalidTarget_DoesNotStartExternalProcess()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Crm:Icp:Enabled"] = "true" }).Build();
        var service = new IcpLookupService(NullLogger<IcpLookupService>.Instance, configuration);
        var result = await service.LookupAsync("127.0.0.1");
        Assert.NotNull(result);
        Assert.Equal("error", result.QueryStatus);
        Assert.False(result.Found);
        Assert.Equal("ICP 查询失败，请稍后重试。", result.Error);
    }

    [Fact]
    public async Task GeoCli_IsDisabledUntilGuardedEgressIsConfigured()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new GeoCheckService(NullLogger<GeoCheckService>.Instance, configuration);
        var result = await service.CheckAsync("example.com");
        Assert.False(result.Checked);
        Assert.Contains("未启用", result.Error);
    }
}
