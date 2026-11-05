using System.Text;
using GuliERP.Api.Crm;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GuliERP.Api.Tests;

public sealed class CrmCompanyDeletionFacts
{
    [Fact]
    public async Task DeleteCompany_ReturnsConflictWithClearSafetyMessage()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();

        await CrmEndpoints.CompanyDeletionBlocked().ExecuteAsync(http);

        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        Assert.Contains(CrmEndpoints.CompanyDeletionDisabledMessage, body, StringComparison.Ordinal);
    }
}
