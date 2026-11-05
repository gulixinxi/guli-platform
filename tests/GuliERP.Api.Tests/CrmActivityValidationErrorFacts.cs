using System.Text;
using GuliERP.Api.Crm;
using GuliERP.Crm.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GuliERP.Api.Tests;

/// <summary>
/// R30 P1-03：非法 DealId（不存在、跨租户跨公司、或不属于该 CRM 客户）的跟进记录创建
/// 必须返回 4xx，而不是被全局异常处理当成 500。
///
/// <para>
/// 断言直接打真实端点使用的同一个出口（<see cref="CrmEndpoints.CreateValidationError"/>），
/// 不复制一份判定逻辑，避免测试与端点行为漂移。
/// 不启动 WebApplicationFactory，不需要数据库。
/// </para>
/// </summary>
public sealed class CrmActivityValidationErrorFacts
{
    /// <summary>
    /// 返回测试用的 HttpContext 与它依赖的 ServiceProvider。
    /// ServiceProvider 必须活到断言结束：Results 执行时还要从 RequestServices 取依赖。
    /// </summary>
    private static (ServiceProvider Services, DefaultHttpContext Http) NewHttpContext()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Response.Body = new MemoryStream();
        return (services, http);
    }

    private static string ReadBody(HttpContext http) =>
        Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

    [Fact]
    public async Task IllegalDealScopeError_IsMappedToBadRequest()
    {
        var ex = new InvalidOperationException(ActivityService.DealScopeErrorMessage);
        var (services, http) = NewHttpContext();
        using (services)
        {
            await CrmEndpoints.CreateValidationError(ex).ExecuteAsync(http);

            Assert.Equal(StatusCodes.Status400BadRequest, http.Response.StatusCode);
            var body = ReadBody(http);
            Assert.Contains(ActivityService.DealScopeErrorMessage, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task IllegalDealScopeError_DoesNotLeakInternalIds()
    {
        var ex = new InvalidOperationException(ActivityService.DealScopeErrorMessage);
        var (services, http) = NewHttpContext();
        using (services)
        {
            await CrmEndpoints.CreateValidationError(ex).ExecuteAsync(http);

            var body = ReadBody(http);
            Assert.DoesNotContain("900001", body, StringComparison.Ordinal);
            Assert.DoesNotContain("900003", body, StringComparison.Ordinal);
            Assert.DoesNotContain("not found", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task UnknownCrmCompanyError_StillMapsToBadRequest()
    {
        // 既有路径不得回归：CRM 客户不存在仍是 400。
        var ex = new InvalidOperationException("CRM company 900001 not found.");
        var (services, http) = NewHttpContext();
        using (services)
        {
            await CrmEndpoints.CreateValidationError(ex).ExecuteAsync(http);

            Assert.Equal(StatusCodes.Status400BadRequest, http.Response.StatusCode);
        }
    }

    [Fact]
    public async Task UnknownCreateFailure_IsNotDowngradedToBadRequest()
    {
        // 不在已知校验错误白名单里的异常不是客户端输入问题：
        // 不得降级成 400，必须原样抛出交全局异常处理（500）。
        var ex = new InvalidOperationException("internal failure Password=should-never-leak");
        var (services, http) = NewHttpContext();
        using (services)
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await CrmEndpoints.CreateValidationError(ex).ExecuteAsync(http));

            Assert.Equal(ex.Message, thrown.Message);
            Assert.Equal(StatusCodes.Status200OK, http.Response.StatusCode);
            Assert.DoesNotContain("should-never-leak", ReadBody(http), StringComparison.Ordinal);
        }
    }
}
