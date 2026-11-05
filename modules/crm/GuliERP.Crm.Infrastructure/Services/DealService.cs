using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public class DealService : IDealService
{
    private readonly CrmDbContext _db;
    private readonly ICrmNumberGenerator _numbers;

    public DealService(CrmDbContext db, ICrmNumberGenerator numbers)
    {
        _db = db;
        _numbers = numbers;
    }

    public async Task<PagedResult<DealDto>> ListAsync(DealListQuery query, CancellationToken ct)
    {
        var q = _db.Deals.Include(d => d.CrmCompany).AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            q = q.Where(x => x.Name.Contains(kw) || x.CrmCompany!.FullName.Contains(kw));
        }
        if (query.Status.HasValue) q = q.Where(x => x.Status == query.Status.Value);
        if (query.Stage.HasValue) q = q.Where(x => x.Stage == query.Stage.Value);
        if (query.Category.HasValue) q = q.Where(x => x.Category == query.Category.Value);
        if (query.OwnerId.HasValue) q = q.Where(x => x.OwnerId == query.OwnerId.Value);
        if (query.CrmCompanyId.HasValue) q = q.Where(x => x.CrmCompanyId == query.CrmCompanyId.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => ToDto(x))
            .ToListAsync(ct);

        return new PagedResult<DealDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<DealDto?> GetByIdAsync(long id, CancellationToken ct)
    {
        var deal = await _db.Deals.Include(d => d.CrmCompany).FirstOrDefaultAsync(d => d.Id == id, ct);
        return deal is null ? null : ToDto(deal);
    }

    public async Task<DealDto> CreateAsync(CreateDealRequest request, long? userId, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == request.CrmCompanyId, ct);
        if (company is null)
            throw new InvalidOperationException($"CRM company {request.CrmCompanyId} not found.");

        if (request.ContactId is long contactId
            && !await _db.Contacts.AnyAsync(c => c.Id == contactId
                && c.CrmCompanyId == request.CrmCompanyId, ct))
            throw new InvalidOperationException("所选联系人不存在或不属于当前客户。");

        if (request.SourceLeadDemandAssessmentId is not long assessmentId
            || request.SourceLeadDemandAssessmentVersion is not int)
            throw new InvalidOperationException("正式商机必须关联已确认的需求评估及当前版本，请从线索需求评估创建。");

        {
            var assessment = await _db.LeadDemandAssessments.FirstOrDefaultAsync(a => a.Id == assessmentId, ct);
            if (assessment is null || assessment.State != "confirmed"
                || assessment.Version != request.SourceLeadDemandAssessmentVersion)
                throw new InvalidOperationException("来源需求评估不存在、未确认或版本已变化，请重新核对。");
            var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == assessment.LeadId, ct);
            if (lead?.ConvertedToCompanyId != request.CrmCompanyId)
                throw new InvalidOperationException("来源线索尚未转为当前客户。");
            var expectedCategory = assessment.Category;
            if (request.Category != expectedCategory)
                throw new InvalidOperationException("商机业务品类与确认的需求方向不一致。");
            if (await _db.Deals.AnyAsync(d => d.SourceLeadDemandAssessmentId == assessmentId, ct))
                throw new InvalidOperationException("该确认需求已有商机，请在商机管理中继续跟进。");
        }

        var deal = new Deal
        {
            Id = SnowflakeId(),
            DealNo = await _numbers.NextDealNoAsync(ct),
            Name = request.Name.Trim(),
            CrmCompanyId = request.CrmCompanyId,
            ContactId = request.ContactId,
            Category = request.Category,
            SourceLeadDemandAssessmentId = request.SourceLeadDemandAssessmentId,
            SourceLeadDemandAssessmentVersion = request.SourceLeadDemandAssessmentVersion,
            ExpectedAmount = request.ExpectedAmount,
            Stage = request.Stage ?? DealStage.InitialContact,
            ExpectedCloseMonth = request.ExpectedCloseMonth,
            WinRate = request.WinRate,
            OwnerId = request.OwnerId,
            Status = DealStatus.InProgress,
            Remarks = request.Remarks,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = userId,
            ModifiedAt = DateTimeOffset.UtcNow,
            ModifiedBy = userId
        };

        _db.Deals.Add(deal);
        await _db.SaveChangesAsync(ct);
        return ToDto(deal);
    }

    public async Task<DealDto?> UpdateAsync(long id, UpdateDealRequest request, CancellationToken ct)
    {
        var deal = await _db.Deals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deal is null) return null;

        // 负责人只能通过专门分配操作变更：普通编辑不得更换或清空。
        // 判定放在任何赋值之前，抛错时不留下半写状态。
        if (CrmOwnerRules.IsOwnerChange(deal.OwnerId, request.OwnerId))
            throw new CrmOwnerChangeNotAllowedException(CrmOwnerEntityKind.Deal);

        // A sourced deal cannot be relabelled into a different business line
        // after the creation gate has checked the confirmed assessment.
        // Historical deals without an assessment retain their existing edit path.
        if (deal.SourceLeadDemandAssessmentId is long assessmentId)
        {
            var assessment = await _db.LeadDemandAssessments
                .FirstOrDefaultAsync(a => a.Id == assessmentId, ct);
            if (assessment is null || assessment.Category != request.Category)
                throw new InvalidOperationException("商机业务品类必须与来源需求评估一致；请从相应需求评估创建新商机。");
        }

        deal.Name = request.Name.Trim();
        deal.Category = request.Category;
        deal.ExpectedAmount = request.ExpectedAmount;
        deal.Stage = request.Stage;
        deal.ExpectedCloseMonth = request.ExpectedCloseMonth;
        deal.WinRate = request.WinRate;
        // 负责人不在普通编辑里写入：省略 ownerId 或传 null 保持原负责人。
        deal.Status = request.Status;
        deal.LostReason = request.LostReason;
        deal.Remarks = request.Remarks;
        deal.ModifiedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToDto(deal);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        var deal = await _db.Deals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deal is null) return false;
        _db.Deals.Remove(deal);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateStageAsync(long id, DealStage stage, CancellationToken ct)
    {
        var deal = await _db.Deals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deal is null) return false;
        deal.Stage = stage;
        deal.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> WinAsync(long id, CancellationToken ct)
    {
        var deal = await _db.Deals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deal is null) return false;
        deal.Status = DealStatus.Won;
        deal.Stage = DealStage.Won;
        deal.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> LoseAsync(long id, LostReason reason, CancellationToken ct)
    {
        var deal = await _db.Deals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deal is null) return false;
        deal.Status = DealStatus.Lost;
        deal.Stage = DealStage.Lost;
        deal.LostReason = reason;
        deal.ModifiedAt = DateTimeOffset.UtcNow;
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
        var deal = await _db.Deals.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (deal is null) return false;
        deal.OwnerId = ownerId;
        deal.ModifiedAt = DateTimeOffset.UtcNow;
        deal.ModifiedBy = actorId;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static DealDto ToDto(Deal x) => new(
        x.Id, x.DealNo, x.Name, x.CrmCompanyId, x.CrmCompany?.FullName,
        x.ContactId, x.Category, x.ExpectedAmount, x.Stage, x.ExpectedCloseMonth,
        x.WinRate, x.OwnerId, x.Status, x.LostReason, x.Remarks, x.CreatedAt, x.CreatedBy);

    private static long SnowflakeId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);
}
