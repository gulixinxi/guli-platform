using GuliERP.Api.Crm;
using Xunit;

namespace GuliERP.Api.Tests;

public sealed class CrmExternalLookupBatchFacts
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    [InlineData(1000, false)]
    public void ExternalProbeBatch_HasBoundedRequestSize(int size, bool expected)
    {
        var domains = Enumerable.Repeat("example.com", size).ToArray();
        Assert.Equal(expected, CrmEndpoints.IsValidExternalLookupBatch(domains));
    }

    [Fact]
    public void ExternalProbeBatch_RejectsMissingBody()
    {
        Assert.False(CrmEndpoints.IsValidExternalLookupBatch(null));
    }
}
