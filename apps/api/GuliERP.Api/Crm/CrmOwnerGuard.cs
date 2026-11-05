using GuliERP.Foundation.Kernel;
using GuliERP.Identity.Domain.Enums;
using GuliERP.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace GuliERP.Api.Crm;

/// <summary>
/// CRM 负责人（OwnerId）的<b>唯一</b>判定入口，位于 API 层。
///
/// <para>
/// 四要素规则（与既有客户／线索分配端点逐字一致，此处收敛为单一事实来源）：
/// <list type="number">
///   <item>用户存在；</item>
///   <item>用户属于当前租户；</item>
///   <item>用户状态为 <see cref="UserStatus.Active"/>；</item>
///   <item>当前公司下存在 <see cref="MembershipStatus.Active"/> 的成员关系。</item>
/// </list>
/// </para>
///
/// <para>
/// 为什么放在 API 层：成员资格判断需要查 Identity 的用户表与成员关系表，
/// 而 <c>GuliERP.Crm.Infrastructure</c> 不得引用 <c>GuliERP.Identity.Infrastructure</c>
/// （见 <c>tests/GuliERP.Crm.Tests/ErpMatchEndpointAuthorizationFacts.cs</c> 的架构断言）。
/// API 项目本来就引用 Identity.Infrastructure，因此判定放在这一层不需要新增
/// Identity.Application 契约，也不扩大模块依赖面。
/// </para>
///
/// <para>
/// R41 报告记录同一规则曾被复制 3 份（team-members / lead assign / company assign）。
/// 本类把这三处收敛为一份，并让新建、转客户、分配等此前无校验的路径共用同一判定。
/// </para>
/// </summary>
public static class CrmOwnerGuard
{
    /// <summary>
    /// 目标不是本公司有效成员时的对外文案。固定字面量，不回显任何调用方输入。
    /// </summary>
    public const string InvalidMemberMessage = "指定成员不属于当前公司或已停用";

    /// <summary>
    /// 解析当前租户与当前公司范围。范围不可用时返回 false，调用方应拒绝请求。
    /// </summary>
    public static bool TryResolveScope(
        ICurrentTenant currentTenant,
        ICurrentCompany currentCompany,
        out long tenantId,
        out long companyId)
    {
        tenantId = currentTenant.Id ?? 0;
        companyId = currentCompany.Id ?? 0;
        return tenantId > 0 && companyId > 0;
    }

    /// <summary>
    /// 四要素成员资格判定。规则见类型注释。
    /// </summary>
    public static async Task<bool> IsActiveCompanyMemberAsync(
        long userId,
        long tenantId,
        long companyId,
        IdentityDbContext identityDb,
        CancellationToken ct)
    {
        var target = await identityDb.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (target is null || target.TenantId != tenantId || target.Status != UserStatus.Active)
            return false;

        return await identityDb.UserCompanyMemberships.AsNoTracking().AnyAsync(m =>
            m.TenantId == tenantId && m.CompanyId == companyId &&
            m.UserId == userId && m.Status == MembershipStatus.Active, ct);
    }

    /// <summary>
    /// 校验「本次要写入的负责人」。
    ///
    /// <para>
    /// 返回 <c>null</c> 表示通过，可以继续写库；
    /// 返回非 null 时，调用方应<b>原样返回</b>该结果（400 或 403），不得继续写库。
    /// </para>
    ///
    /// <para>
    /// <paramref name="ownerId"/> 为 <c>null</c> 表示「不指定负责人」，一律放行：
    /// 新建场景允许没有负责人；转客户场景会在调用前把继承到的线索负责人传进来，
    /// 继承值同样要过这一关，不能静默写入无效旧 ID。
    /// </para>
    /// </summary>
    public static async Task<IResult?> EnsureValidOwnerAsync(
        long? ownerId,
        IdentityDbContext identityDb,
        ICurrentTenant currentTenant,
        ICurrentCompany currentCompany,
        CancellationToken ct)
    {
        if (ownerId is null) return null;

        if (!TryResolveScope(currentTenant, currentCompany, out var tenantId, out var companyId))
            return Results.Forbid();

        if (!await IsActiveCompanyMemberAsync(ownerId.Value, tenantId, companyId, identityDb, ct))
            return Results.BadRequest(InvalidMemberMessage);

        return null;
    }

    /// <summary>
    /// 当前公司的有效成员列表（团队成员下拉）。
    /// 谓词与 <see cref="IsActiveCompanyMemberAsync"/> 同源：同租户 + 用户启用 + 成员关系启用。
    /// </summary>
    public static async Task<IReadOnlyList<CrmTeamMemberDto>> ListActiveCompanyMembersAsync(
        long tenantId,
        long companyId,
        IdentityDbContext identityDb,
        CancellationToken ct)
    {
        var members = identityDb.UserCompanyMemberships.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.CompanyId == companyId &&
                m.Status == MembershipStatus.Active)
            .Select(m => m.UserId);
        return await identityDb.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.Status == UserStatus.Active && members.Contains(u.Id))
            .OrderBy(u => u.Id)
            .Select(u => new CrmTeamMemberDto(u.Id, u.UserName, u.DisplayName))
            .ToListAsync(ct);
    }
}

/// <summary>
/// 团队成员条目。属性名显式固定，保证响应 JSON 形状与改造前一致
/// （改造前是匿名类型 <c>new { id, userName, displayName }</c>）。
/// </summary>
public sealed record CrmTeamMemberDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("userName")] string? UserName,
    [property: JsonPropertyName("displayName")] string? DisplayName);
