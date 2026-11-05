using System.Text;
using GuliERP.Api.Crm;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Api.Tests;

public sealed class CrmExternalLookupErrorFacts
{
    [Fact]
    public async Task CsvParseError_UsesFixedClientMessage()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();

        await CrmEndpoints.QuakeCsvParseError().ExecuteAsync(http);

        Assert.Equal(StatusCodes.Status400BadRequest, http.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        Assert.Contains("CSV 解析失败，请检查文件格式后重试。", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), 400)]
    [InlineData(typeof(InvalidOperationException), 503)]
    [InlineData(typeof(HttpRequestException), 502)]
    [InlineData(typeof(TimeoutException), 502)]
    [InlineData(typeof(Exception), 500)]
    public async Task Response_DoesNotExposeExceptionDetails(Type exceptionType, int status)
    {
        const string privateDetail = "Password=should-never-leak";
        var exception = (Exception)Activator.CreateInstance(exceptionType, privateDetail)!;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext();
        http.RequestServices = services;
        http.Response.Body = new MemoryStream();

        await CrmEndpoints.ExternalLookupError(exception, http, NullLoggerFactory.Instance, "test")
            .ExecuteAsync(http);

        Assert.Equal(status, http.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        Assert.DoesNotContain(privateDetail, body, StringComparison.Ordinal);
        Assert.Contains("detail", body, StringComparison.Ordinal);
    }
}
