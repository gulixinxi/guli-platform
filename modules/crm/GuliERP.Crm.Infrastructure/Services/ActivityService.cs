using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public class ActivityService : IActivityService
{
    private readonly CrmDbContext _db;

    public ActivityService(CrmDbContext db) => _db = db;

    public async Task<IReadOnlyList<ActivityDto>> GetByCompanyAsync(long companyId, CancellationToken ct)
    {
        return await _db.Activities
            .Include(a => a.CrmCompany)
            .Include(a => a.Deal)
            .Where(x => x.CrmCompanyId == companyId)
            .OrderByDescending(x => x.FollowAt)
            .Select(x => ToDto(x))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ActivityDto>> GetByDealAsync(long dealId, CancellationToken ct)
    {
        return await _db.Activities
            .Include(a => a.CrmCompany)
            .Include(a => a.Deal)
            .Where(x => x.DealId == dealId)
            .OrderByDescending(x => x.FollowAt)
            .Select(x => ToDto(x))
            .ToListAsync(ct);
    }

    public async Task<ActivityDto?> GetByIdAsync(long id, CancellationToken ct)
    {
        var activity = await _db.Activities
            .Include(a => a.CrmCompany)
            .Include(a => a.Deal)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        return activity is null ? null : ToDto(activity);
    }

    public async Task<ActivityDto> CreateAsync(CreateActivityRequest request, long? userId, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == request.CrmCompanyId, ct);
        if (company is null)
            throw new InvalidOperationException($"CRM company {request.CrmCompanyId} not found.");

        // 关联商机必须与本条跟进记录的 CRM 客户同租户、同公司，且商机本身属于该 CRM 客户。
        // 不满足时拒绝保存（不落库）。
        if (request.DealId is { } dealId)
        {
            // 归属判定走 _db.Deals：CrmDbContext 的全局查询过滤器已把结果限定在当前
            // TenantId/CompanyId，所以「跨租户/跨公司的商机」与「根本不存在的商机」
            // 在这里同样表现为查不到，报错文案也完全一致，
            // 不会因为报错差异泄露该商机在其他租户是否存在（不存在性预言机）。
            var deal = await _db.Deals.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == dealId, ct);

            // 下面三项是显式归属断言：即使将来全局过滤器被改动或绕过，
            // 也不会退化成「只比较全局 ID」就放行写入。
            if (deal is null
                || deal.TenantId != company.TenantId
                || deal.CompanyId != company.CompanyId
                || deal.CrmCompanyId != company.Id)
            {
                throw new InvalidOperationException(DealScopeErrorMessage);
            }
        }

        var activity = new Activity
        {
            Id = SnowflakeId(),
            CrmCompanyId = request.CrmCompanyId,
            DealId = request.DealId,
            FollowType = request.FollowType,
            FollowAt = request.FollowAt ?? DateTimeOffset.UtcNow,
            Content = request.Content?.Trim() ?? string.Empty,
            Attitude = request.Attitude,
            NextAction = request.NextAction?.Trim() ?? string.Empty,
            NextActionDate = request.NextActionDate,
            OwnerId = request.OwnerId,
            Attachments = request.Attachments,
            Remarks = request.Remarks,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = userId,
            ModifiedAt = DateTimeOffset.UtcNow,
            ModifiedBy = userId
        };

        _db.Activities.Add(activity);
        await _db.SaveChangesAsync(ct);
        return ToDto(activity);
    }

    /// <summary>关联商机不可用时使用相同提示，不暴露商机是否存在于其他范围。</summary>
    public const string DealScopeErrorMessage = "所选商机不存在或不属于当前客户。";

    public async Task<ActivityDto?> UpdateAsync(long id, UpdateActivityRequest request, CancellationToken ct)
    {
        var activity = await _db.Activities.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (activity is null) return null;

        // 负责人只能通过专门分配操作变更：普通编辑不得更换或清空。
        // 判定放在任何赋值之前，抛错时不留下半写状态。
        if (CrmOwnerRules.IsOwnerChange(activity.OwnerId, request.OwnerId))
            throw new CrmOwnerChangeNotAllowedException(CrmOwnerEntityKind.Activity);

        activity.FollowType = request.FollowType;
        activity.FollowAt = request.FollowAt;
        activity.Content = request.Content?.Trim() ?? string.Empty;
        activity.Attitude = request.Attitude;
        activity.NextAction = request.NextAction?.Trim() ?? string.Empty;
        activity.NextActionDate = request.NextActionDate;
        // 负责人不在普通编辑里写入：省略 ownerId 或传 null 保持原负责人。
        activity.Attachments = request.Attachments;
        activity.Remarks = request.Remarks;
        activity.ModifiedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToDto(activity);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        var activity = await _db.Activities.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (activity is null) return false;
        _db.Activities.Remove(activity);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>标记跟进任务完成：清空下次动作和日期，使其从今日待办移除</summary>
    public async Task<bool> CompleteTaskAsync(long id, CancellationToken ct)
    {
        var activity = await _db.Activities.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (activity is null) return false;
        activity.NextAction = string.Empty;
        activity.NextActionDate = null;
        activity.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// 专门分配操作写入负责人。<paramref name="ownerId"/> 为 null 表示清空。
    /// 有效成员校验由 API 层在写入前完成（成员资格判断需要 Identity 查询，
    /// 不能在 CRM 服务层直接引用 Identity.Infrastructure）。
    /// </summary>
    public async Task<bool> AssignOwnerAsync(long id, long? ownerId, long? actorId, CancellationToken ct)
    {
        var activity = await _db.Activities.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (activity is null) return false;
        activity.OwnerId = ownerId;
        activity.ModifiedAt = DateTimeOffset.UtcNow;
        activity.ModifiedBy = actorId;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static ActivityDto ToDto(Activity x) => new(
        x.Id, x.CrmCompanyId, x.CrmCompany?.FullName ?? string.Empty,
        x.DealId, x.Deal?.Name, x.FollowType, x.FollowAt, x.Content,
        x.Attitude, x.NextAction, x.NextActionDate, x.OwnerId,
        x.Attachments, x.Remarks, x.CreatedAt, x.CreatedBy);

    private static long SnowflakeId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
}
