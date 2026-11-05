using System.Globalization;
using System.Text.RegularExpressions;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record VisitHandoffPreviewInput(string Category, string ReferenceKind, string ReferenceId);

/// <summary>Read-only P2-2 handoff contract until visit/NAS/project decisions are made.</summary>
public sealed class VisitHandoffPreviewService(CrmDbContext db)
{
    private static readonly Regex OpaqueId = new("^[A-Za-z0-9:_-]{1,100}$", RegexOptions.Compiled);

    public async Task<object> PreviewAsync(long leadId, VisitHandoffPreviewInput input, CancellationToken ct)
    {
        if (leadId <= 0 || input.Category is not ("website" or "geo")
            || input.ReferenceKind is not ("visit" or "website_report")
            || string.IsNullOrWhiteSpace(input.ReferenceId))
            throw new ArgumentException("请选择有效线索、方向和引用编号。");
        var reference = input.ReferenceId.Trim();
        if (input.ReferenceKind == "visit" && !OpaqueId.IsMatch(reference))
            throw new ArgumentException("拜访引用只能填写外部系统的不透明编号，不能填写路径、URL 或文件内容。");
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该线索。");
        var category = input.Category == "website" ? BusinessCategory.WebsiteBuild : BusinessCategory.GEOOpt;
        var assessment = await db.LeadDemandAssessments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.LeadId == leadId && a.Category == category, ct);
        var blockers = new List<string>();
        var sourceVerified = false;
        string? reportId = null;

        if (input.ReferenceKind == "visit")
        {
            blockers.Add("拜访资料源、访问权限和 NAS/文件策略尚未决定；引用编号仅供预览，未核验且不保存。");
        }
        else
        {
            if (!long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ArgumentException("网站报告编号必须为正整数。");
            var report = await db.WebsiteDiagnosticReports.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id && r.Status == "completed", ct)
                ?? throw new KeyNotFoundException("当前公司找不到已完成的报告。");
            if (lead.SourceIdentityCandidateId != report.CandidateId)
                throw new InvalidOperationException("报告未关联到该线索已审核的企业身份。");
            sourceVerified = true;
            reportId = report.Id.ToString(CultureInfo.InvariantCulture);
            blockers.Add("网站技术报告不证明客户有建站或 GEO 购买意愿，仍需销售核对需求。");
        }
        var assessmentConfirmed = assessment?.State == "confirmed";
        if (!assessmentConfirmed) blockers.Add("该线索的对应需求评估尚未确认。");
        if (lead.ConvertedToCompanyId is null) blockers.Add("线索尚未转为客户主档。");
        var existingDeal = assessment is not null && await db.Deals.AsNoTracking()
            .AnyAsync(d => d.SourceLeadDemandAssessmentId == assessment.Id, ct);
        if (existingDeal) blockers.Add("这项已确认需求已有商机，不能重复创建。");
        blockers.Add("建站工单、NAS 和 ERP/项目系统接口未接入；交接保持待同步。");

        return new
        {
            leadId = lead.Id.ToString(CultureInfo.InvariantCulture), input.Category,
            input.ReferenceKind, referenceId = reference, reportId,
            sourceVerification = sourceVerified ? "confirmed_internal_report" : "unverified_external",
            assessmentId = assessment?.Id.ToString(CultureInfo.InvariantCulture),
            assessmentVersion = assessment?.Version,
            assessmentConfirmed, existingDeal,
            canUseExistingDealFlow = sourceVerified && assessmentConfirmed
                && lead.ConvertedToCompanyId is not null && !existingDeal,
            handoffStatus = "pending_sync", createsSignal = false, createsDeal = false,
            createsTicket = false, copiesAttachment = false, performsExternalRequest = false,
            blockers
        };
    }
}
