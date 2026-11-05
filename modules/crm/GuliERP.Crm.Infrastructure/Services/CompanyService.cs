using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public class CompanyService : ICompanyService
{
    /// <summary>crm.companies.CreditCode 的列宽上限（varchar(18)）。</summary>
    private const int MaxCreditCodeLength = 18;

    private readonly CrmDbContext _db;
    private readonly ICrmNumberGenerator _numbers;

    public CompanyService(CrmDbContext db, ICrmNumberGenerator numbers)
    {
        _db = db;
        _numbers = numbers;
    }

    public async Task<PagedResult<CompanyDto>> ListAsync(CompanyListQuery query, CancellationToken ct)
    {
        var q = _db.Companies.AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword.Trim();
            q = q.Where(x => x.FullName.Contains(kw) || x.ShortName!.Contains(kw) || x.CreditCode!.Contains(kw));
        }
        if (query.Status.HasValue)
            q = q.Where(x => x.Status == query.Status.Value);
        if (query.City.HasValue)
            q = q.Where(x => x.City == query.City.Value);
        if (query.Industry.HasValue)
            q = q.Where(x => x.Industry == query.Industry.Value);
        if (query.Rating.HasValue)
            q = q.Where(x => x.Rating == query.Rating.Value);
        if (query.OwnerId.HasValue)
            q = q.Where(x => x.OwnerId == query.OwnerId.Value);
        if (query.HasWebsite.HasValue)
            q = q.Where(x => x.HasWebsite == query.HasWebsite.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => ToDto(x))
            .ToListAsync(ct);

        return new PagedResult<CompanyDto>(items, total, query.Page, query.PageSize);
    }

    public async Task<CompanyDto?> GetByIdAsync(long id, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
        return company is null ? null : ToDto(company);
    }

    public async Task<CompanyDto> CreateAsync(CreateCompanyRequest request, long? userId, CancellationToken ct)
    {
        var normalizedCreditCode = NormalizeCreditCode(request.CreditCode);

        // 去重检查（国内企业）：优先统一社会信用代码，其次公司全称
        var exists = await _db.Companies.AnyAsync(x =>
            (!string.IsNullOrEmpty(normalizedCreditCode) && x.CreditCode == normalizedCreditCode) ||
            (string.IsNullOrEmpty(normalizedCreditCode) && x.FullName == request.FullName.Trim()), ct);

        if (exists)
        {
            // TASK 3 B-4: a duplicate customer is a client-side conflict, not a
            // server fault. The message is a FIXED string on purpose - it must
            // not echo back the caller's own credit code / full name.
            throw new CrmValidationException(
                CrmErrorCodes.DuplicateCompany,
                !string.IsNullOrEmpty(normalizedCreditCode)
                    ? "客户已存在（按统一社会信用代码判定），请勿重复建档。"
                    : "客户已存在（按公司全称判定），请勿重复建档。");
        }

        var company = new Company
        {
            Id = SnowflakeId(),
            CompanyNo = await _numbers.NextCompanyNoAsync(ct),
            FullName = request.FullName.Trim(),
            CreditCode = normalizedCreditCode,
            ShortName = request.ShortName,
            WebsiteDomain = request.WebsiteDomain,
            City = request.City,
            District = request.District,
            Address = request.Address,
            Industry = request.Industry,
            Scale = request.Scale,
            Revenue = request.Revenue,
            HasWebsite = request.HasWebsite,
            WebsiteStatus = request.WebsiteStatus,
            Source = request.Source,
            SourceUrl = request.SourceUrl,
            CollectedAt = DateTimeOffset.UtcNow,
            OwnerId = request.OwnerId,
            Status = request.Status ?? CustomerStatus.ToContact,
            Rating = request.Rating,
            IsRatingSuggested = false,
            ExternalKey = request.ExternalKey,
            Remarks = request.Remarks,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = userId,
            ModifiedAt = DateTimeOffset.UtcNow,
            ModifiedBy = userId
        };

        _db.Companies.Add(company);
        await _db.SaveChangesAsync(ct);
        return ToDto(company);
    }

    public async Task<CompanyDto?> UpdateAsync(long id, UpdateCompanyRequest request, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (company is null) return null;

        // 负责人只能通过专门分配操作变更：普通编辑不得更换或清空。
        // 判定放在任何赋值之前，抛错时不留下半写状态。
        if (CrmOwnerRules.IsOwnerChange(company.OwnerId, request.OwnerId))
            throw new CrmOwnerChangeNotAllowedException(CrmOwnerEntityKind.Company);

        company.FullName = request.FullName.Trim();
        company.CreditCode = NormalizeCreditCode(request.CreditCode);
        company.ShortName = request.ShortName;
        company.WebsiteDomain = request.WebsiteDomain;
        company.City = request.City;
        company.District = request.District;
        company.Address = request.Address;
        company.Industry = request.Industry;
        company.Scale = request.Scale;
        company.Revenue = request.Revenue;
        company.HasWebsite = request.HasWebsite;
        company.WebsiteStatus = request.WebsiteStatus;
        company.Source = request.Source;
        company.SourceUrl = request.SourceUrl;
        // 负责人不在普通编辑里写入：省略 ownerId 或传 null 保持原负责人。
        company.Status = request.Status;
        company.Rating = request.Rating;
        company.Remarks = request.Remarks;
        company.ModifiedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        return ToDto(company);
    }

    // Existing company relationships cascade to contacts, deals and activities.
    // Keep deletion closed until the retention policy and FK behavior are reviewed.
    public Task<bool> DeleteAsync(long id, CancellationToken ct) =>
        throw new NotSupportedException("CRM company deletion is disabled to protect related records.");

    public async Task<bool> UpdateTagsAsync(long id, List<string> tags, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (company is null) return false;
        company.Tags = tags.Distinct().ToList();
        company.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AssignAsync(long id, long ownerId, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (company is null) return false;
        company.OwnerId = ownerId;
        company.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ClearOwnerAsync(long id, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (company is null) return false;
        company.OwnerId = null;
        company.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateStatusAsync(long id, CustomerStatus status, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (company is null) return false;
        company.Status = status;
        company.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UpdateRatingAsync(long id, IntentRating rating, bool isSuggested, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (company is null) return false;
        company.Rating = rating;
        company.IsRatingSuggested = isSuggested;
        company.ModifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<TodayTaskDto>> GetTodayTasksAsync(long? ownerId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var q = _db.Activities
            .Where(a => a.NextActionDate.HasValue && a.NextActionDate <= today)
            .Where(a => !string.IsNullOrEmpty(a.NextAction));

        if (ownerId.HasValue)
            q = q.Where(a => a.OwnerId == ownerId.Value);

        var tasks = await q
            .OrderBy(a => a.NextActionDate)
            .Select(a => new TodayTaskDto(
                a.Id,
                a.CrmCompanyId,
                a.CrmCompany!.FullName,
                a.NextAction,
                a.NextActionDate!.Value,
                a.OwnerId,
                a.NextActionDate < today
            ))
            .ToListAsync(ct);

        return tasks;
    }

    /// <summary>
    /// 意向评级计算（可解释，4个字段加权）
    /// ≥70=A | 50-69=B | 30-49=C | <30=D
    /// </summary>
    public async Task<int> CalculateSuggestedRatingAsync(long id, CancellationToken ct)
    {
        var company = await _db.Companies
            .Include(c => c.Contacts)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (company is null) return 0;

        int score = 0;

        // 有明确联系人且是关键决策人 +30
        var hasKeyDecisionMaker = company.Contacts.Any(c => c.IsKeyPerson && c.DecisionRole == DecisionRole.DecisionMaker);
        if (hasKeyDecisionMaker) score += 30;

        // 有官网且需要优化（GEO天然需求）+25
        if (company.HasWebsite && company.WebsiteStatus is Domain.Enums.WebsiteStatus.CannotOpen or Domain.Enums.WebsiteStatus.NoHttps or Domain.Enums.WebsiteStatus.MobileUnusable or Domain.Enums.WebsiteStatus.Outdated)
            score += 25;

        // 所在地市=潍坊（本地服务能力最强）+15
        if (company.City == CompanyCity.Weifang) score += 15;

        // 企业规模≥101人 +15
        if (company.Scale is CompanyScale.Large or CompanyScale.XLarge or CompanyScale.XXLarge)
            score += 15;

        // 来源渠道=转介绍 +15
        if (company.Source == SourceChannel.Referral) score += 15;

        return score;
    }

    private static CompanyDto ToDto(Company x) => new(
        x.Id, x.CompanyNo, x.FullName, x.CreditCode, x.ShortName, x.WebsiteDomain,
        x.City, x.District, x.Address, x.Industry, x.Scale, x.Revenue,
        x.HasWebsite, x.WebsiteStatus, x.Source, x.SourceUrl, x.CollectedAt,
        x.OwnerId, x.Status, x.Rating, x.IsRatingSuggested, x.ExternalKey, x.ExternalSource,
        x.Remarks, x.Tags.ToList(), x.CreatedAt, x.CreatedBy);

    private static long SnowflakeId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(1000);

    /// <summary>
    /// 统一社会信用代码标准化：去空格、转大写，并校验长度上限。
    ///
    /// <para>
    /// crm.companies.CreditCode 是 varchar(18)。TASK 3 B-4 之前这里只做
    /// trim+大写，超长值会一路送到 PostgreSQL 触发 22001，而没有任何一层
    /// 把它映射成 4xx ⇒ 调用方拿到 HTTP 500（纯客户端错误被报成服务端故障）。
    /// 这里提前拦截，抛 <see cref="CrmValidationException"/>，由 API 层翻译成 400。
    /// </para>
    /// </summary>
    private static string? NormalizeCreditCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = code.Trim().ToUpperInvariant();
        if (normalized.Length > MaxCreditCodeLength)
        {
            throw new CrmValidationException(
                CrmErrorCodes.InvalidCreditCode,
                $"统一社会信用代码长度不能超过 {MaxCreditCodeLength} 位。");
        }
        return normalized;
    }
}
