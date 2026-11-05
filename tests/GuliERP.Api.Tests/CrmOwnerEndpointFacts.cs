using GuliERP.Api.Crm;
using GuliERP.Crm.Application;
using GuliERP.Crm.Infrastructure;
using GuliERP.Foundation;
using GuliERP.Identity.Infrastructure;
using GuliERP.Mdm.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace GuliERP.Api.Tests;

/// <summary>
/// R42：负责人相关端点的授权面与路由形状。
///
/// <para>
/// 断言真实注册出来的 <see cref="RouteEndpoint"/> 元数据，
/// 而不是复制一份「应该挂什么策略」的清单，避免测试与端点行为漂移。
/// </para>
///
/// <para>
/// 本文件是<b>端点元数据断言</b>，不是 HTTP 实测：没有启动 TestServer，
/// 没有发出真实请求，也不代表 PostgreSQL 或正式环境验收。
/// </para>
/// </summary>
public sealed class CrmOwnerEndpointFacts
{
    private static RouteEndpoint[] MapCrmEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        const string unusedConnection = "Host=127.0.0.1;Database=metadata_only;Username=none;Password=none";
        builder.Services.AddGuliErpFoundation(unusedConnection);
        builder.Services.AddGuliErpIdentity(unusedConnection);
        builder.Services.AddGuliErpMdm(unusedConnection);
        builder.Services.AddGuliErpCrm(unusedConnection);
        using var app = builder.Build();
        app.MapCrmEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
    }

    private static void AssertPolicies(
        IEnumerable<RouteEndpoint> endpoints,
        string route,
        params string[] expected)
    {
        var endpoint = Assert.Single(endpoints, item => item.RoutePattern.RawText == route);
        AssertPolicies(endpoint, expected);
    }

    /// <summary>
    /// 同一路由模板下有多个动词时（GET/PUT/DELETE 共用 /{id:long}），
    /// 必须连同 HTTP 方法一起定位，否则会命中多个端点。
    /// </summary>
    private static void AssertPoliciesForMethod(
        IEnumerable<RouteEndpoint> endpoints,
        string route,
        string httpMethod,
        params string[] expected)
    {
        var endpoint = Assert.Single(endpoints, item =>
            item.RoutePattern.RawText == route
            && item.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(httpMethod) == true);
        AssertPolicies(endpoint, expected);
    }

    private static void AssertPolicies(RouteEndpoint endpoint, string[] expected)
    {
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), policies.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DealAndActivity_ExposeDedicatedAssignmentEndpoints()
    {
        var endpoints = MapCrmEndpoints();

        // 商机与跟进此前没有专门分配入口；本轮新增，且沿用各自既有的管理权限，
        // 不新增权限码、不改角色包。
        AssertPolicies(endpoints, "/api/v1/crm/deals/{id:long}/assign", CrmPolicies.DealManage);
        AssertPolicies(endpoints, "/api/v1/crm/activities/{id:long}/assign", CrmPolicies.ActivityManage);
    }

    [Fact]
    public void ExistingCompanyAndLeadAssignmentEndpoints_KeepTheirPolicies()
    {
        var endpoints = MapCrmEndpoints();

        // 既有专门分配路径不得回退。
        AssertPolicies(endpoints, "/api/v1/crm/companies/{id:long}/assign", CrmPolicies.CompanyAssign);
        AssertPolicies(endpoints, "/api/v1/crm/leads/{id:long}/assign", CrmPolicies.LeadManage);
    }

    [Fact]
    public void TeamMembersListing_KeepsItsReadPolicy()
    {
        var endpoints = MapCrmEndpoints();

        AssertPolicies(endpoints, "/api/v1/crm/team-members", CrmPolicies.LeadRead);
    }

    [Fact]
    public void NormalEditEndpoints_KeepManagePermissions()
    {
        var endpoints = MapCrmEndpoints();

        // 普通编辑仍然要求管理权限——本轮只改「编辑不得更换/清空负责人」的语义，
        // 不改这些端点的授权要求。
        AssertPoliciesForMethod(endpoints, "/api/v1/crm/companies/{id:long}", "PUT", CrmPolicies.CompanyManage);
        AssertPoliciesForMethod(endpoints, "/api/v1/crm/deals/{id:long}", "PUT", CrmPolicies.DealManage);
        AssertPoliciesForMethod(endpoints, "/api/v1/crm/activities/{id:long}", "PUT", CrmPolicies.ActivityManage);
    }
}
