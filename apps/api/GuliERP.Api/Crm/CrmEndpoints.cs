using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Services;
using GuliERP.Foundation.Kernel;
using GuliERP.Identity.Application.Authorization;
using GuliERP.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using System.Runtime.ExceptionServices;

namespace GuliERP.Api.Crm;

public static class CrmEndpoints
{
    public const int MaxExternalLookupBatchSize = 50;
    public const string CompanyDeletionDisabledMessage = "为保护关联的联系人、商机和跟进记录，当前不支持删除客户。";

    public static IResult CompanyDeletionBlocked() =>
        Results.Conflict(new { detail = CompanyDeletionDisabledMessage });

    public static bool IsValidExternalLookupBatch(IReadOnlyCollection<string>? domains) =>
        domains is { Count: >= 1 and <= MaxExternalLookupBatchSize };

    public static IEndpointRouteBuilder MapCrmEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/crm").WithTags("CRM");

        group.MapGet("/backup-monitor/status", (IConfiguration configuration) =>
                Results.Ok(BackupMonitorStatus.Read(configuration)))
            .RequireAuthorization(GuliErpAuthorizationPolicies.IdentityRoleAssign);

        // ===== 团队成员（供分配/负责人显示）=====
        group.MapGet("/team-members", async (
            IdentityDbContext identityDb, ICurrentTenant currentTenant, ICurrentCompany currentCompany,
            CancellationToken ct) =>
        {
            if (!CrmOwnerGuard.TryResolveScope(currentTenant, currentCompany, out var tenantId, out var companyId))
                return Results.Forbid();

            // 成员谓词与 CrmOwnerGuard 共用同一事实来源（改造前此处是规则的第 3 份拷贝）。
            var members = await CrmOwnerGuard.ListActiveCompanyMembersAsync(tenantId, companyId, identityDb, ct);
            return Results.Ok(members);
        }).RequireAuthorization(CrmPolicies.LeadRead);

        // ===== 线索池（Lead）=====
        var leads = group.MapGroup("/leads");

        leads.MapGet("", async (
                [FromQuery] string? keyword,
                [FromQuery] LeadStatus? status,
                [FromQuery] CompanyCity? city,
                [FromQuery] CompanyIndustry? industry,
                [FromQuery] IntentRating? rating,
                [FromQuery] long? ownerId,
                [FromQuery] SourceChannel? source,
                [FromQuery] SubjectType? subjectType,
                [FromQuery] string? tag,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                ILeadService svc,
                CancellationToken ct) =>
            {
                var query = new LeadListQuery(keyword, status, city, industry, rating, ownerId, source, subjectType, tag, page ?? 1, pageSize ?? 20);
                return Results.Ok(await svc.ListAsync(query, ct));
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        leads.MapGet("/{id:long}", async (long id, ILeadService svc, CancellationToken ct) =>
            {
                var dto = await svc.GetByIdAsync(id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        leads.MapGet("/{id:long}/demand-assessments", async (
                long id, LeadDemandAssessmentService assessments, CancellationToken ct) =>
            {
                var result = await assessments.GetAsync(id, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        leads.MapPost("/{id:long}/visit-handoff-preview", async (
                long id, VisitHandoffPreviewInput input, VisitHandoffPreviewService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.PreviewAsync(id, input, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return Results.BadRequest(new { detail = CrmClientErrorDetails.Safe(400, ex.Message) }); }
                catch (InvalidOperationException ex) { return Results.Conflict(new { detail = CrmClientErrorDetails.Safe(409, ex.Message) }); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        leads.MapGet("/{id:long}/demand-assessments/{category}/history", async (
                long id, string category, LeadDemandAssessmentService assessments, CancellationToken ct) =>
            {
                try
                {
                    var result = await assessments.GetHistoryAsync(id, category, ct);
                    return result is null ? Results.NotFound() : Results.Ok(result);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { detail = CrmClientErrorDetails.Safe(400, ex.Message) });
                }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        leads.MapPut("/{id:long}/demand-assessments/{category}", async (
                long id, string category, LeadDemandAssessmentInput input,
                HttpContext http, LeadDemandAssessmentService assessments, CancellationToken ct) =>
            {
                var reviewerId = GetUserId(http);
                if (reviewerId is null) return Results.Unauthorized();
                try
                {
                    var result = await assessments.SaveAsync(id, category, input, reviewerId.Value, ct);
                    return result is null ? Results.NotFound() : Results.Ok(result);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { detail = CrmClientErrorDetails.Safe(400, ex.Message) });
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        leads.MapPost("", async (
                [FromBody] CreateLeadRequest request,
                HttpContext http,
                ILeadService svc,
                CancellationToken ct) =>
            {
                if (request.SourceReverseLookupRecordId is not null)
                    return Results.Conflict(new { detail = "单位反查结果需先完成企业身份审核；当前不能直接导入线索。" });
                if (request.ExternalSource == "quake_identity")
                    return Results.Conflict(new { detail = "已审身份转线索须使用专用预检与转入接口。" });
                var userId = GetUserId(http);
                try
                {
                    var created = await svc.CreateAsync(request, userId, ct);
                    return Results.Created($"/api/v1/crm/leads/{created.Id}", created);
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("企业身份冲突", StringComparison.Ordinal))
                {
                    return Results.Conflict(new { detail = "企业身份冲突，请核对已有线索。" });
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        leads.MapPut("/{id:long}", async (
                long id,
                [FromBody] UpdateLeadRequest request,
                ILeadService svc,
                CancellationToken ct) =>
            {
                var updated = await svc.UpdateAsync(id, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        leads.MapDelete("/{id:long}", async (long id, ILeadService svc, CancellationToken ct) =>
            {
                var ok = await svc.DeleteAsync(id, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 认领
        leads.MapPost("/{id:long}/claim", async (
                long id,
                HttpContext http,
                ILeadService svc,
                CancellationToken ct) =>
            {
                var userId = GetUserId(http);
                if (!userId.HasValue) return Results.Unauthorized();
                var ok = await svc.ClaimAsync(id, userId.Value, ct);
                return ok ? Results.NoContent() : Results.BadRequest("线索不存在或已被认领");
            })
            .RequireAuthorization(CrmPolicies.LeadClaim);

        // 管理员分配线索给指定成员
        leads.MapPost("/{id:long}/assign", async (
                long id,
                [FromBody] long ownerId,
                ILeadService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                CancellationToken ct) =>
            {
                var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                    ownerId, identityDb, currentTenant, currentCompany, ct);
                if (guard is not null) return guard;
                var ok = await svc.AssignAsync(id, ownerId, ct);
                return ok ? Results.NoContent() : Results.BadRequest("线索不存在");
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 废弃
        leads.MapPost("/{id:long}/discard", async (long id, ILeadService svc, CancellationToken ct) =>
            {
                var ok = await svc.DiscardAsync(id, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 更新标签
        leads.MapPut("/{id:long}/tags", async (
                long id,
                [FromBody] List<string> tags,
                ILeadService svc,
                CancellationToken ct) =>
            {
                var ok = await svc.UpdateTagsAsync(id, tags, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 转化为客户主档
        leads.MapPost("/{id:long}/convert", async (
                long id,
                [FromBody] ConvertLeadRequest request,
                HttpContext http,
                ILeadService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                CancellationToken ct) =>
            {
                var userId = GetUserId(http);
                var lead = await svc.GetByIdAsync(id, ct);
                if (lead is not null)
                {
                    // 新负责人优先；未指定则继承线索原负责人。
                    // 继承值同样必须过有效成员校验，不能静默写入无效旧 ID。
                    var effectiveOwnerId = CrmOwnerRules.ResolveEffectiveOwner(request.OwnerId, lead.OwnerId);
                    var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                        effectiveOwnerId, identityDb, currentTenant, currentCompany, ct);
                    if (guard is not null) return guard;
                }
                var req = request with { LeadId = id };
                var company = await svc.ConvertAsync(req, userId, ct);
                return Results.Ok(company);
            })
            .RequireAuthorization(CrmPolicies.LeadConvert);

        // 重新计算AI评分
        leads.MapPost("/{id:long}/score", async (long id, ILeadService svc, CancellationToken ct) =>
            {
                var score = await svc.CalculateAiScoreAsync(id, ct);
                return Results.Ok(new { score });
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 批量导入
        leads.MapPost("/batch-import", async (
                [FromBody] IEnumerable<CreateLeadRequest> requests,
                HttpContext http,
                ILeadService svc,
                CancellationToken ct) =>
            {
                var rows = requests.ToArray();
                if (rows.Any(r => r.SourceReverseLookupRecordId is not null || r.ExternalSource == "quake_identity"))
                    return Results.Conflict(new { detail = "来源记录须先经过企业身份审核和专用转入预检。" });
                var userId = GetUserId(http);
                var result = await svc.BatchImportAsync(rows, userId, ct);
                return Results.Ok(result);
            })
            .RequireAuthorization(CrmPolicies.LeadImport);

        // ===== 客户主档（Company）=====
        var companies = group.MapGroup("/companies");

        companies.MapGet("", async (
                [FromQuery] string? keyword,
                [FromQuery] CustomerStatus? status,
                [FromQuery] CompanyCity? city,
                [FromQuery] CompanyIndustry? industry,
                [FromQuery] IntentRating? rating,
                [FromQuery] long? ownerId,
                [FromQuery] bool? hasWebsite,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                ICompanyService svc,
                CancellationToken ct) =>
            {
                var query = new CompanyListQuery(keyword, status, city, industry, rating, ownerId, hasWebsite, page ?? 1, pageSize ?? 20);
                return Results.Ok(await svc.ListAsync(query, ct));
            })
            .RequireAuthorization(CrmPolicies.CompanyRead);

        companies.MapGet("/{id:long}", async (long id, ICompanyService svc, CancellationToken ct) =>
            {
                var dto = await svc.GetByIdAsync(id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            })
            .RequireAuthorization(CrmPolicies.CompanyRead);

        companies.MapPost("", async (
                [FromBody] CreateCompanyRequest request,
                HttpContext http,
                ICompanyService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                CancellationToken ct) =>
            {
                // 建档时可指定初始负责人：null 允许，非 null 必须是本公司有效成员。
                var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                    request.OwnerId, identityDb, currentTenant, currentCompany, ct);
                if (guard is not null) return guard;
                var userId = GetUserId(http);
                var created = await svc.CreateAsync(request, userId, ct);
                return Results.Created($"/api/v1/crm/companies/{created.Id}", created);
            })
            .RequireAuthorization(CrmPolicies.CompanyManage);

        companies.MapPut("/{id:long}", async (
                long id,
                [FromBody] UpdateCompanyRequest request,
                ICompanyService svc,
                CancellationToken ct) =>
            {
                try
                {
                    var updated = await svc.UpdateAsync(id, request, ct);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }
                catch (CrmOwnerChangeNotAllowedException ex)
                {
                    return OwnerChangeNotAllowed(ex);
                }
            })
            .RequireAuthorization(CrmPolicies.CompanyManage);

        companies.MapDelete("/{id:long}", (long id) => CompanyDeletionBlocked())
            .RequireAuthorization(CrmPolicies.CompanyManage);

        // 分配负责人
        companies.MapPost("/{id:long}/assign", async (
                long id,
                [FromBody] long ownerId,
                ICompanyService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                CancellationToken ct) =>
            {
                var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                    ownerId, identityDb, currentTenant, currentCompany, ct);
                if (guard is not null) return guard;
                var ok = await svc.AssignAsync(id, ownerId, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.CompanyAssign);

        // 显式清空客户负责人，保留原有裸数字分配接口的兼容性。
        companies.MapPost("/{id:long}/assign/clear", async (
                long id,
                ICompanyService svc,
                ILoggerFactory loggerFactory,
                HttpContext http,
                CancellationToken ct) =>
            {
                var current = await svc.GetByIdAsync(id, ct);
                if (current is null) return Results.NotFound();
                var ok = await svc.ClearOwnerAsync(id, ct);
                if (!ok) return Results.NotFound();
                loggerFactory.CreateLogger("GuliERP.Api.Crm.Owner").LogWarning(
                    "CRM owner cleared: entity=company id={EntityId} actor={ActorId} previousOwnerId={PreviousOwnerId}",
                    id, GetUserId(http), current.OwnerId);
                return Results.NoContent();
            })
            .RequireAuthorization(CrmPolicies.CompanyAssign);

        // 更新标签
        companies.MapPut("/{id:long}/tags", async (
                long id,
                [FromBody] List<string> tags,
                ICompanyService svc,
                CancellationToken ct) =>
            {
                var ok = await svc.UpdateTagsAsync(id, tags, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.CompanyManage);

        // 更新状态
        companies.MapPost("/{id:long}/status", async (
                long id,
                [FromBody] CustomerStatus status,
                ICompanyService svc,
                CancellationToken ct) =>
            {
                var ok = await svc.UpdateStatusAsync(id, status, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.CompanyManage);

        // 更新评级
        companies.MapPost("/{id:long}/rating", async (
                long id,
                [FromBody] IntentRating rating,
                ICompanyService svc,
                CancellationToken ct) =>
            {
                var ok = await svc.UpdateRatingAsync(id, rating, false, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.CompanyManage);

        // 今日待办
        companies.MapGet("/tasks/today", async (
                [FromQuery] long? ownerId,
                ICompanyService svc,
                CancellationToken ct) =>
            {
                return Results.Ok(await svc.GetTodayTasksAsync(ownerId, ct));
            })
            .RequireAuthorization(CrmPolicies.CompanyRead, CrmPolicies.ActivityRead);

        // ERP客商候选匹配（只读，不写关联关系）
        // 双重权限：crm.company.read + mdm.business-partner.read
        // 查询结果统一称"候选匹配"，不显示"已同步"或"已关联"
        companies.MapGet("/{id:long}/erp-match", async (
                long id,
                ICompanyErpMatchService svc,
                CancellationToken ct) =>
            {
                var result = await svc.GetCandidatesAsync(id, ct);
                return result is null
                    ? Results.NotFound(new { error = "客户不存在或无权访问" })
                    : Results.Ok(result);
            })
            .RequireAuthorization(CrmPolicies.CompanyErpMatch);

        // ===== 联系人（Contact）=====
        var contacts = group.MapGroup("/contacts");

        contacts.MapPost("/candidates/by-identity/{identityId:long}/view", async (long identityId,
                HttpContext http, ContactCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.ListAsync(identityId, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapPost("/candidates", async (ContactCandidateInput input, HttpContext http,
                ContactCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(new { id = await svc.CreateAsync(input, actorId, ct) }); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapPost("/candidates/{id:long}/reveal", async (long id, ContactCandidateReasonInput input,
                HttpContext http, ContactCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.RevealAsync(id, input.Reason, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapGet("/candidates/{id:long}/events", async (long id,
                ContactCandidateService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.EventsAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapPost("/candidates/{id:long}/review", async (long id, ContactCandidateReviewInput input,
                HttpContext http, ContactCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { await svc.ReviewAsync(id, input, actorId, ct); return Results.NoContent(); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapPost("/candidates/{id:long}/promote", async (long id, ContactCandidateReasonInput input,
                HttpContext http, ContactCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(new { contactId = (await svc.PromoteAsync(id, input.Reason, actorId, ct)).ToString() }); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapPost("/candidates/{id:long}/redact", async (long id, ContactCandidateReasonInput input,
                HttpContext http, ContactCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { await svc.RedactAsync(id, input.Reason, actorId, ct); return Results.NoContent(); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapPost("/candidates/{id:long}/correct", async (long id, ContactCandidateCorrectionInput input,
                HttpContext http, ContactCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(new { replacementId = await svc.CorrectAsync(id, input, actorId, ct) }); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            }).RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapGet("/by-company/{companyId:long}", async (long companyId, IContactService svc, CancellationToken ct) =>
            {
                return Results.Ok(await svc.GetByCompanyAsync(companyId, ct));
            })
            .RequireAuthorization(CrmPolicies.ContactRead);

        contacts.MapGet("/{id:long}", async (long id, IContactService svc, CancellationToken ct) =>
            {
                var dto = await svc.GetByIdAsync(id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            })
            .RequireAuthorization(CrmPolicies.ContactRead);

        contacts.MapPost("", async (
                [FromBody] CreateContactRequest request,
                IContactService svc,
                CancellationToken ct) =>
            {
                return await CreateWithBadRequestAsync(
                    () => svc.CreateAsync(request, ct),
                    c => c.Id,
                    "/api/v1/crm/contacts");
            })
            .RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapPut("/{id:long}", async (
                long id,
                [FromBody] UpdateContactRequest request,
                IContactService svc,
                CancellationToken ct) =>
            {
                var updated = await svc.UpdateAsync(id, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            })
            .RequireAuthorization(CrmPolicies.ContactManage);

        contacts.MapDelete("/{id:long}", async (long id, IContactService svc, CancellationToken ct) =>
            {
                var ok = await svc.DeleteAsync(id, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.ContactManage);

        // 设为关键人
        contacts.MapPost("/{id:long}/key-person", async (long id, IContactService svc, CancellationToken ct) =>
            {
                var ok = await svc.SetKeyPersonAsync(id, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.ContactManage);

        // ===== 商机（Deal）=====
        var deals = group.MapGroup("/deals");

        deals.MapGet("", async (
                [FromQuery] string? keyword,
                [FromQuery] DealStatus? status,
                [FromQuery] DealStage? stage,
                [FromQuery] BusinessCategory? category,
                [FromQuery] long? ownerId,
                [FromQuery] long? companyId,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                IDealService svc,
                CancellationToken ct) =>
            {
                var query = new DealListQuery(keyword, status, stage, category, ownerId, companyId, page ?? 1, pageSize ?? 20);
                return Results.Ok(await svc.ListAsync(query, ct));
            })
            .RequireAuthorization(CrmPolicies.DealRead);

        deals.MapGet("/{id:long}", async (long id, IDealService svc, CancellationToken ct) =>
            {
                var dto = await svc.GetByIdAsync(id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            })
            .RequireAuthorization(CrmPolicies.DealRead);

        deals.MapPost("", async (
                [FromBody] CreateDealRequest request,
                HttpContext http,
                IDealService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                CancellationToken ct) =>
            {
                // 建档时可指定初始负责人：null 允许，非 null 必须是本公司有效成员。
                var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                    request.OwnerId, identityDb, currentTenant, currentCompany, ct);
                if (guard is not null) return guard;
                var userId = GetUserId(http);
                return await CreateWithBadRequestAsync(
                    () => svc.CreateAsync(request, userId, ct),
                    d => d.Id,
                    "/api/v1/crm/deals");
            })
            .RequireAuthorization(CrmPolicies.DealManage);

        deals.MapPut("/{id:long}", async (
                long id,
                [FromBody] UpdateDealRequest request,
                IDealService svc,
                CancellationToken ct) =>
            {
                try
                {
                    var updated = await svc.UpdateAsync(id, request, ct);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }
                catch (CrmOwnerChangeNotAllowedException ex)
                {
                    return OwnerChangeNotAllowed(ex);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { detail = CrmClientErrorDetails.Safe(400, ex.Message) });
                }
            })
            .RequireAuthorization(CrmPolicies.DealManage);

        deals.MapDelete("/{id:long}", async (long id, IDealService svc, CancellationToken ct) =>
            {
                var ok = await svc.DeleteAsync(id, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.DealManage);

        // 专门分配/清空商机负责人（普通编辑接口不得更换或清空负责人）
        deals.MapPost("/{id:long}/assign", async (
                long id,
                [FromBody] AssignOwnerRequest request,
                HttpContext http,
                IDealService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                var shapeError = ValidateAssignOwnerRequest(request);
                if (shapeError is not null) return shapeError;

                var actorId = GetUserId(http);
                var current = await svc.GetByIdAsync(id, ct);
                if (current is null) return Results.NotFound();

                long? newOwnerId = null;
                if (!request.Clear)
                {
                    var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                        request.OwnerId, identityDb, currentTenant, currentCompany, ct);
                    if (guard is not null) return guard;
                    newOwnerId = request.OwnerId;
                }

                var ok = await svc.AssignOwnerAsync(id, newOwnerId, actorId, ct);
                if (!ok) return Results.NotFound();

                var logger = loggerFactory.CreateLogger("GuliERP.Api.Crm.Owner");
                if (request.Clear)
                {
                    logger.LogWarning(
                        "CRM owner cleared: entity=deal id={EntityId} actor={ActorId} previousOwnerId={PreviousOwnerId}",
                        id, actorId, current.OwnerId);
                }
                else
                {
                    logger.LogInformation(
                        "CRM owner assigned: entity=deal id={EntityId} actor={ActorId} previousOwnerId={PreviousOwnerId} newOwnerId={NewOwnerId}",
                        id, actorId, current.OwnerId, newOwnerId);
                }
                return Results.NoContent();
            })
            .RequireAuthorization(CrmPolicies.DealManage);

        // 更新阶段
        deals.MapPost("/{id:long}/stage", async (
                long id,
                [FromBody] DealStage stage,
                IDealService svc,
                CancellationToken ct) =>
            {
                var ok = await svc.UpdateStageAsync(id, stage, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.DealManage);

        // 成交
        deals.MapPost("/{id:long}/win", async (long id, IDealService svc, CancellationToken ct) =>
            {
                var ok = await svc.WinAsync(id, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.DealManage);

        // 丢单
        deals.MapPost("/{id:long}/lose", async (
                long id,
                [FromBody] LostReason reason,
                IDealService svc,
                CancellationToken ct) =>
            {
                var ok = await svc.LoseAsync(id, reason, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.DealManage);

        // ===== 跟进记录（Activity）=====
        var activities = group.MapGroup("/activities");

        activities.MapGet("/by-company/{companyId:long}", async (long companyId, IActivityService svc, CancellationToken ct) =>
            {
                return Results.Ok(await svc.GetByCompanyAsync(companyId, ct));
            })
            .RequireAuthorization(CrmPolicies.ActivityRead);

        activities.MapGet("/by-deal/{dealId:long}", async (long dealId, IActivityService svc, CancellationToken ct) =>
            {
                return Results.Ok(await svc.GetByDealAsync(dealId, ct));
            })
            .RequireAuthorization(CrmPolicies.ActivityRead);

        activities.MapGet("/{id:long}", async (long id, IActivityService svc, CancellationToken ct) =>
            {
                var dto = await svc.GetByIdAsync(id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            })
            .RequireAuthorization(CrmPolicies.ActivityRead);

        activities.MapPost("", async (
                [FromBody] CreateActivityRequest request,
                HttpContext http,
                IActivityService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                CancellationToken ct) =>
            {
                // 建档时可指定初始负责人：null 允许，非 null 必须是本公司有效成员。
                var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                    request.OwnerId, identityDb, currentTenant, currentCompany, ct);
                if (guard is not null) return guard;
                var userId = GetUserId(http);
                return await CreateWithBadRequestAsync(
                    () => svc.CreateAsync(request, userId, ct),
                    a => a.Id,
                    "/api/v1/crm/activities");
            })
            .RequireAuthorization(CrmPolicies.ActivityManage);

        activities.MapPut("/{id:long}", async (
                long id,
                [FromBody] UpdateActivityRequest request,
                IActivityService svc,
                CancellationToken ct) =>
            {
                try
                {
                    var updated = await svc.UpdateAsync(id, request, ct);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }
                catch (CrmOwnerChangeNotAllowedException ex)
                {
                    return OwnerChangeNotAllowed(ex);
                }
            })
            .RequireAuthorization(CrmPolicies.ActivityManage);

        activities.MapDelete("/{id:long}", async (long id, IActivityService svc, CancellationToken ct) =>
            {
                var ok = await svc.DeleteAsync(id, ct);
                return ok ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.ActivityManage);

        // 专门分配/清空跟进记录负责人（普通编辑接口不得更换或清空负责人）
        activities.MapPost("/{id:long}/assign", async (
                long id,
                [FromBody] AssignOwnerRequest request,
                HttpContext http,
                IActivityService svc,
                IdentityDbContext identityDb,
                ICurrentTenant currentTenant,
                ICurrentCompany currentCompany,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                var shapeError = ValidateAssignOwnerRequest(request);
                if (shapeError is not null) return shapeError;

                var actorId = GetUserId(http);
                var current = await svc.GetByIdAsync(id, ct);
                if (current is null) return Results.NotFound();

                long? newOwnerId = null;
                if (!request.Clear)
                {
                    var guard = await CrmOwnerGuard.EnsureValidOwnerAsync(
                        request.OwnerId, identityDb, currentTenant, currentCompany, ct);
                    if (guard is not null) return guard;
                    newOwnerId = request.OwnerId;
                }

                var ok = await svc.AssignOwnerAsync(id, newOwnerId, actorId, ct);
                if (!ok) return Results.NotFound();

                var logger = loggerFactory.CreateLogger("GuliERP.Api.Crm.Owner");
                if (request.Clear)
                {
                    logger.LogWarning(
                        "CRM owner cleared: entity=activity id={EntityId} actor={ActorId} previousOwnerId={PreviousOwnerId}",
                        id, actorId, current.OwnerId);
                }
                else
                {
                    logger.LogInformation(
                        "CRM owner assigned: entity=activity id={EntityId} actor={ActorId} previousOwnerId={PreviousOwnerId} newOwnerId={NewOwnerId}",
                        id, actorId, current.OwnerId, newOwnerId);
                }
                return Results.NoContent();
            })
            .RequireAuthorization(CrmPolicies.ActivityManage);

        activities.MapPost("/{id:long}/complete-task", async (long id, IActivityService svc, CancellationToken ct) =>
            {
                var ok = await svc.CompleteTaskAsync(id, ct);
                return ok ? Results.Ok(new { success = true }) : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.ActivityManage);

        // ===== 导入导出日志（ImportExportLog）=====
        var logs = group.MapGroup("/logs");

        logs.MapGet("", async (
                [FromQuery] OperationType? operationType,
                [FromQuery] LogStatus? status,
                [FromQuery] DateTimeOffset? from,
                [FromQuery] DateTimeOffset? to,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                IImportExportLogService svc,
                CancellationToken ct) =>
            {
                var query = new ImportExportLogListQuery(operationType, status, from, to, page ?? 1, pageSize ?? 20);
                return Results.Ok(await svc.ListAsync(query, ct));
            })
            .RequireAuthorization(CrmPolicies.LogRead);

        logs.MapGet("/{id:long}", async (long id, IImportExportLogService svc, CancellationToken ct) =>
            {
                var dto = await svc.GetByIdAsync(id, ct);
                return dto is null ? Results.NotFound() : Results.Ok(dto);
            })
            .RequireAuthorization(CrmPolicies.LogRead);

        // ===== 经营看板 =====
        group.MapGet("/dashboard/stats", async (
                ILeadService leadSvc,
                ICompanyService companySvc,
                IDealService dealSvc,
                CancellationToken ct) =>
            {
                var leads = await leadSvc.ListAsync(new LeadListQuery(null, null, null, null, null, null, null, null, null, 1, 10000), ct);
                var companies = await companySvc.ListAsync(new CompanyListQuery(null, null, null, null, null, null, null, 1, 10000), ct);
                var deals = await dealSvc.ListAsync(new DealListQuery(null, null, null, null, null, null, 1, 10000), ct);

                var leadCount = leads.Total;
                var companyCount = companies.Total;
                var activeDeals = deals.Items.Where(d => d.Status == DealStatus.InProgress).ToList();
                var wonDeals = deals.Items.Where(d => d.Status == DealStatus.Won).ToList();
                var lostDeals = deals.Items.Where(d => d.Status == DealStatus.Lost).ToList();

                var activeAmount = activeDeals.Sum(d => d.ExpectedAmount);
                var wonAmount = wonDeals.Sum(d => d.ExpectedAmount);

                // 按阶段统计
                var stageStats = Enum.GetValues<DealStage>()
                    .Where(s => s <= DealStage.Negotiation)
                    .Select(s => new
                    {
                        stage = (int)s,
                        label = s.ToString(),
                        count = activeDeals.Count(d => d.Stage == s)
                    })
                    .ToList();

                // 按负责人统计
                var ownerStats = activeDeals
                    .Where(d => d.OwnerId.HasValue)
                    .GroupBy(d => d.OwnerId!.Value)
                    .Select(g => new
                    {
                        ownerId = g.Key,
                        dealCount = g.Count(),
                        amount = g.Sum(d => d.ExpectedAmount)
                    })
                    .ToList();

                // 逾期跟进任务统计
                var todayTasks = await companySvc.GetTodayTasksAsync(null, ct);
                var overdueTasks = todayTasks.Where(t => t.IsOverdue).ToList();

                return Results.Ok(new
                {
                    funnel = new
                    {
                        leads = leadCount,
                        companies = companyCount,
                        activeDeals = activeDeals.Count,
                        wonDeals = wonDeals.Count
                    },
                    conversion = new
                    {
                        leadToCompany = companyCount > 0 ? Math.Round((double)companyCount / leadCount * 100, 1) : 0,
                        companyToDeal = companyCount > 0 ? Math.Round((double)activeDeals.Count / companyCount * 100, 1) : 0,
                        dealToWin = deals.Items.Count > 0 ? Math.Round((double)wonDeals.Count / deals.Items.Count * 100, 1) : 0
                    },
                    amount = new
                    {
                        active = activeAmount,
                        won = wonAmount,
                        lost = lostDeals.Sum(d => d.ExpectedAmount)
                    },
                    byStage = stageStats,
                    byOwner = ownerStats,
                    summary = new
                    {
                        totalDeals = deals.Total,
                        lostDeals = lostDeals.Count
                    },
                    overdue = new
                    {
                        count = overdueTasks.Count,
                        todayCount = todayTasks.Count - overdueTasks.Count,
                        items = overdueTasks.Take(10).Select(t => new
                        {
                            activityId = t.ActivityId,
                            companyId = t.CompanyId,
                            companyName = t.CompanyName,
                            nextAction = t.NextAction,
                            nextActionDate = t.NextActionDate.ToString("yyyy-MM-dd")
                        }).ToList()
                    }
                });
            })
            .RequireAuthorization(CrmPolicies.LeadRead, CrmPolicies.CompanyRead,
                CrmPolicies.DealRead, CrmPolicies.ActivityRead);

        // ===== 商机建议（规则引擎）=====
        group.MapGet("/opportunity-suggestions", async (
                ILeadService leadSvc,
                ICompanyService companySvc,
                CancellationToken ct) =>
            {
                var leads = await leadSvc.ListAsync(new LeadListQuery(null, null, null, null, null, null, null, null, null, 1, 10000), ct);
                var companies = await companySvc.ListAsync(new CompanyListQuery(null, null, null, null, null, null, null, 1, 10000), ct);

                var suggestions = new List<object>();

                // 规则R1: 官网资料缺失 → 建站方向待核验，不代表已证实无官网
                // 规则R2: 有官网+来源是网站测绘 → GEO方向待核验
                // 规则R3: 食品/机械/化工行业+有官网 → AI数字化机会
                // 规则R4: 高意向评分 → 优先级加成
                foreach (var lead in leads.Items.Where(l => l.Status == LeadStatus.Pending || l.Status == LeadStatus.Claimed))
                {
                    var hasWebsite = !string.IsNullOrWhiteSpace(lead.WebsiteDomain);
                    var isQuakeSource = lead.Source == SourceChannel.Other || lead.Source == SourceChannel.Map;
                    var isTargetIndustry = lead.Industry is CompanyIndustry.Machinery or CompanyIndustry.Food or CompanyIndustry.Chemical or CompanyIndustry.Agriculture;
                    var isHighIntent = lead.AiScore >= 70 || lead.Rating == IntentRating.A;
                    var hasBaiduTag = lead.Tags != null && lead.Tags.Contains("百度推广");
                    var isIndividualBusiness = lead.SubjectType == SubjectType.IndividualBusiness;
                    var hasPoiTag = lead.Tags != null && (lead.Tags.Contains("POI客户") || lead.Tags.Contains("待标注") || lead.Tags.Contains("已下架") || lead.Tags.Contains("待重新上架") || lead.Tags.Contains("抖音地址"));
                    var hasDelistedTag = lead.Tags != null && lead.Tags.Contains("已下架");
                    var hasDouyinTag = lead.Tags != null && lead.Tags.Contains("抖音地址");

                    if (!hasWebsite)
                    {
                        suggestions.Add(new
                        {
                            targetType = "lead", targetId = lead.Id, targetName = lead.CompanyName,
                            opportunityType = "建站需求待核验", reason = "线索尚未记录官网，需先核实官网及实际建站需求",
                            evidence = $"官网字段未填写; 来源={lead.Source}",
                            assessmentStatus = "pending",
                            priority = isHighIntent ? "中" : "低",
                            suggestedAmount = "5,000-20,000",
                            productCategory = (int)BusinessCategory.WebsiteBuild
                        });
                    }
                    else if (isQuakeSource || hasBaiduTag)
                    {
                        var geoPriority = hasBaiduTag ? "高" : (isHighIntent ? "高" : "中");
                        var geoReason = hasBaiduTag
                            ? "企业已投放百度推广（有付费习惯），GEO优化可降低获客成本、提升AI搜索可见性"
                            : "企业有官网且来自网站测绘渠道，需实际检测 AI 可见性并确认需求";
                        suggestions.Add(new
                        {
                            targetType = "lead", targetId = lead.Id, targetName = lead.CompanyName,
                            opportunityType = "GEO优化", reason = geoReason,
                            evidence = $"域名={lead.WebsiteDomain}; 来源={lead.Source}; 百度推广标签={hasBaiduTag}",
                            assessmentStatus = "pending",
                            priority = geoPriority,
                            suggestedAmount = "3,000-10,000",
                            productCategory = (int)BusinessCategory.GEOOpt
                        });
                    }
                    if (hasWebsite && isTargetIndustry)
                    {
                        suggestions.Add(new
                        {
                            targetType = "lead", targetId = lead.Id, targetName = lead.CompanyName,
                            opportunityType = "AI数字化服务", reason = $"{lead.Industry}行业企业，可试点AI应用/数字化服务",
                            evidence = $"行业={lead.Industry}; 有官网={hasWebsite}",
                            assessmentStatus = "pending",
                            priority = "中",
                            suggestedAmount = "10,000-50,000",
                            productCategory = (int)BusinessCategory.AIApp
                        });
                    }
                    // 规则R5: 个体商户/门店 → 地图标注服务
                    if (isIndividualBusiness || hasPoiTag)
                    {
                        var poiPriority = hasDelistedTag ? "高" : (isHighIntent ? "高" : "中");
                        var poiReason = hasDelistedTag
                            ? "店铺已被地图平台下架，有重新上架需求"
                            : (isIndividualBusiness ? "个体商户/门店客户，地图信息完善可提升到店客流" : "标记为POI客户，需要地图标注服务");
                        suggestions.Add(new
                        {
                            targetType = "lead", targetId = lead.Id, targetName = lead.CompanyName,
                            opportunityType = "地图标注", reason = poiReason,
                            evidence = $"主体类型={lead.SubjectType}; 标签={string.Join(",", lead.Tags ?? new List<string>())}",
                            assessmentStatus = "pending",
                            priority = poiPriority,
                            suggestedAmount = "300-1,500",
                            productCategory = (int)BusinessCategory.PoiAnnotation
                        });
                    }
                    // 规则R6: 抖音地址标签 → 抖音/快手地址添加服务
                    if (hasDouyinTag)
                    {
                        suggestions.Add(new
                        {
                            targetType = "lead", targetId = lead.Id, targetName = lead.CompanyName,
                            opportunityType = "抖音地址添加", reason = "客户需要在抖音/快手等短视频平台添加店铺地址定位",
                            evidence = $"标签包含抖音地址",
                            assessmentStatus = "pending",
                            priority = "中",
                            suggestedAmount = "200-800",
                            productCategory = (int)BusinessCategory.DouyinAddress
                        });
                    }
                }

                foreach (var comp in companies.Items.Where(c => c.Status == CustomerStatus.ToContact || c.Status == CustomerStatus.Following))
                {
                    var hasWebsite = comp.HasWebsite || !string.IsNullOrWhiteSpace(comp.WebsiteDomain);
                    var isTargetIndustry = comp.Industry is CompanyIndustry.Machinery or CompanyIndustry.Food or CompanyIndustry.Chemical or CompanyIndustry.Agriculture;
                    var isHighIntent = comp.Rating == IntentRating.A;
                    var siteAbnormal = comp.WebsiteStatus == WebsiteStatus.CannotOpen || comp.WebsiteStatus == WebsiteStatus.Outdated || comp.WebsiteStatus == WebsiteStatus.NoWebsite;

                    if (!hasWebsite)
                    {
                        suggestions.Add(new
                        {
                            targetType = "company", targetId = comp.Id, targetName = comp.FullName,
                            opportunityType = "建站需求待核验", reason = "客户主档未确认官网，需先核实官网及实际建站需求",
                            evidence = $"官网字段未确认; HasWebsite={comp.HasWebsite}; WebsiteDomain={comp.WebsiteDomain}",
                            assessmentStatus = "pending",
                            priority = isHighIntent ? "中" : "低",
                            suggestedAmount = "5,000-20,000",
                            productCategory = (int)BusinessCategory.WebsiteBuild
                        });
                    }
                    if (siteAbnormal)
                    {
                        suggestions.Add(new
                        {
                            targetType = "company", targetId = comp.Id, targetName = comp.FullName,
                            opportunityType = "网站重建", reason = "官网状态异常或无法访问，需要重建或修复",
                            evidence = $"WebsiteStatus={comp.WebsiteStatus}",
                            assessmentStatus = "pending",
                            priority = "高",
                            suggestedAmount = "8,000-30,000",
                            productCategory = (int)BusinessCategory.WebsiteOpt
                        });
                    }
                    if (hasWebsite && isTargetIndustry && !siteAbnormal)
                    {
                        suggestions.Add(new
                        {
                            targetType = "company", targetId = comp.Id, targetName = comp.FullName,
                            opportunityType = "AI数字化服务", reason = $"{comp.Industry}行业客户，可试点AI应用",
                            evidence = $"行业={comp.Industry}; 有官网={hasWebsite}",
                            assessmentStatus = "pending",
                            priority = "中",
                            suggestedAmount = "10,000-50,000",
                            productCategory = (int)BusinessCategory.AIApp
                        });
                    }
                }

                return Results.Ok(new { total = suggestions.Count, items = suggestions });
            })
            .RequireAuthorization(CrmPolicies.LeadRead, CrmPolicies.CompanyRead);

        // ===== 企业情报信号 =====
        group.MapGet("/signals/website-pending-summary", async (
                WebsiteSignalWorkflowService svc, CancellationToken ct) =>
            Results.Ok(await svc.PendingSummaryAsync(ct)))
            .RequireAuthorization(CrmPolicies.LeadRead);

        group.MapGet("/signals", async (
                ISignalService svc,
                [AsParameters] SignalListQuery query,
                CancellationToken ct) =>
            {
                var result = await svc.ListAsync(query with { Page = query.Page < 1 ? 1 : query.Page, PageSize = query.PageSize is < 1 or > 200 ? 20 : query.PageSize }, ct);
                return Results.Ok(result);
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        group.MapGet("/signals/{id:long}/website-target", async (
                long id, WebsiteSignalWorkflowService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.TargetAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        group.MapGet("/signals/{id:long}/website-detail", async (
                long id, WebsiteSignalWorkflowService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.GetSignalAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        group.MapPost("/signals/{id:long}/website-decision", async (
                long id, WebsiteSignalDecisionInput input, HttpContext http,
                WebsiteSignalWorkflowService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.DecideAsync(id, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        group.MapPost("/signals", async (
                ISignalService svc,
                CreateSignalCommand cmd,
                CancellationToken ct) =>
            {
                try
                {
                    var result = await svc.CreateAsync(cmd, ct);
                    return Results.Created($"/api/v1/crm/signals/{result.Id}", result);
                }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        group.MapPut("/signals/{id:long}/status", async (
                long id,
                ISignalService svc,
                UpdateSignalStatusCommand cmd,
                CancellationToken ct) =>
            {
                try
                {
                    var result = await svc.UpdateStatusAsync(id, cmd, ct);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new { detail = "信号不存在或不属于当前公司。" });
                }
                catch (InvalidOperationException ex)
                {
                    return IdentityError(409, ex.Message);
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        group.MapPut("/signals/{id:long}/verification", async (
                long id,
                ISignalService svc,
                UpdateVerificationCommand cmd,
                CancellationToken ct) =>
            {
                try
                {
                    var result = await svc.UpdateVerificationAsync(id, cmd, ct);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException)
                {
                    return Results.NotFound(new { detail = "信号不存在或不属于当前公司。" });
                }
                catch (InvalidOperationException ex)
                {
                    return IdentityError(409, ex.Message);
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // ===== Quake 线索采集 =====
        var quake = group.MapGroup("/quake");
        quake.MapGet("/configuration", (
                QuakeRuntimeCredentialStore credentials,
                ICurrentTenant tenant,
                ICurrentCompany company) =>
            {
                if (tenant.Id is not > 0 || company.Id is not > 0) return Results.Forbid();
                var (configured, source) = credentials.GetStatus(tenant.Id, company.Id);
                return Results.Ok(new { configured, source, persistent = source is "environment" or "windows_credential",
                    runtimeKeys = credentials.GetPublicEntries(tenant.Id.Value, company.Id.Value) });
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        quake.MapPut("/configuration", (
                QuakeRuntimeKeyInput input,
                QuakeRuntimeCredentialStore credentials,
                ICurrentTenant tenant,
                ICurrentCompany company) =>
            {
                if (tenant.Id is not long tenantId || tenantId <= 0
                    || company.Id is not long companyId || companyId <= 0) return Results.Forbid();
                try
                {
                    credentials.SetKey(tenantId, companyId, input.ApiKey);
                    return Results.Ok(new { configured = true, source = "windows_credential", persistent = true,
                        runtimeKeys = credentials.GetPublicEntries(tenantId, companyId) });
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { detail = "Quake key is missing or invalid." });
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);

        quake.MapDelete("/configuration/{id:guid}", (
                Guid id, QuakeRuntimeCredentialStore credentials,
                ICurrentTenant tenant, ICurrentCompany company) =>
            {
                if (tenant.Id is not long tenantId || tenantId <= 0
                    || company.Id is not long companyId || companyId <= 0) return Results.Forbid();
                return credentials.RemoveKey(tenantId, companyId, id)
                    ? Results.NoContent() : Results.NotFound();
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);

        quake.MapGet("/search", async (
                string query,
                int? size,
                bool? force,
                QuakeSearchService svc,
                QuakeQueryHistoryService historySvc,
                HttpContext http,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                try
                {
                    // 检查冷却期（非强制查询时）
                    if (force != true)
                    {
                        var cooldown = await historySvc.CheckCooldownAsync(query, 30, ct);
                        if (cooldown != null)
                        {
                            return Results.Ok(new
                            {
                                total = cooldown.ResultCount,
                                items = Array.Empty<object>(),
                                fromCache = true,
                                lastQueriedAt = cooldown.QueriedAt,
                                message = $"该条件已于 {cooldown.QueriedAt:yyyy-MM-dd} 查询过（{cooldown.ResultCount}条结果），30天冷却期内可使用\"强制重新查询\""
                            });
                        }
                    }
                    var results = await svc.SearchAsync(query, size ?? 20, ct);
                    // 记录查询历史
                    await historySvc.RecordAsync(query, results.Count, 0, ct);
                    return Results.Ok(new { total = results.Count, items = results, fromCache = false });
                }
                catch (Exception ex)
                {
                    return ExternalLookupError(ex, http, loggerFactory, "quake_search");
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // ===== Quake 资产：API 通道 + 手动导入通道（统一 15 字段 + 清洗）=====

        // API 通道：实时查询 Quake，映射 15 字段并清洗后入库
        quake.MapPost("/assets/fetch", async (
                [FromBody] QuakeFetchRequest request,
                QuakeAssetService svc,
                ICurrentTenant tenant,
                ICurrentCompany company,
                HttpContext http,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                try
                {
                    var result = await svc.FetchAsync(
                        request.Query, tenant.Id, company.Id, request.Size ?? 20, ct);
                    return Results.Ok(result);
                }
                catch (Exception ex)
                {
                    return ExternalLookupError(ex, http, loggerFactory, "quake_fetch");
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 手动通道 1：直接贴 15 字段（JSON）
        quake.MapPost("/assets/preview", async (
                [FromBody] List<QuakeAssetInput> items,
                QuakeAssetService svc,
                CancellationToken ct) =>
            {
                if (items.Count is < 1 or > 1000)
                    return Results.BadRequest(new { detail = "每次请预检 1–1000 行。" });
                return Results.Ok(await svc.PreviewAsync(items, ct));
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        quake.MapPost("/assets/import", async (
                [FromBody] List<QuakeAssetInput> items,
                [FromQuery] Guid? batchId,
                QuakeAssetService svc,
                ICurrentTenant tenant,
                ICurrentCompany company,
                CancellationToken ct) =>
            {
                if (items.Count is < 1 or > 1000)
                    return Results.BadRequest(new { detail = "每次请导入 1–1000 行。" });
                var result = await svc.ImportAsync(items, tenant.Id, company.Id, ct, batchId);
                return Results.Ok(result);
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 手动通道 2：贴 Quake 导出的 CSV（中文表头即可）
        quake.MapPost("/assets/import-csv", async (
                [FromBody] QuakeCsvImportRequest request,
                QuakeAssetService svc,
                ICurrentTenant tenant,
                ICurrentCompany company,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Csv))
                    return Results.BadRequest(new { detail = "CSV 内容为空。" });
                List<QuakeAssetInput> items;
                try { items = QuakeAssetService.ParseCsv(request.Csv); }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    return QuakeCsvParseError();
                }
                if (items.Count is < 1 or > 1000)
                    return Results.BadRequest(new { detail = $"解析到 {items.Count} 行，请控制在 1–1000 行。" });
                var result = await svc.ImportAsync(items, tenant.Id, company.Id, ct);
                return Results.Ok(result);
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 查询（可按清洗判定或批次过滤）
        quake.MapGet("/assets", async (
                [FromQuery] string? verdict,
                [FromQuery] Guid? batchId,
                [FromQuery] string? keyword,
                [FromQuery] int? skip,
                [FromQuery] int? take,
                QuakeAssetService svc,
                CancellationToken ct) =>
            {
                if (keyword?.Length > 200 || skip < 0)
                    return Results.BadRequest(new { detail = "搜索词最多 200 字，偏移量不能为负数。" });
                var search = await svc.SearchAsync(keyword, verdict, batchId, skip ?? 0, take ?? 200, ct);
                var items = search.Items;
                return Results.Ok(new
                {
                    total = search.Total,
                    pageCount = items.Count,
                    valid = items.Count(i => i.CleanVerdict == "valid"),
                    suspect = items.Count(i => i.CleanVerdict == "suspect"),
                    rejected = items.Count(i => i.CleanVerdict == "rejected"),
                    items,
                });
            })
            .RequireAuthorization(CrmPolicies.LeadRead);

        // 转线索：只允许 valid，且不重复导入
        // Identity review is required before promotion; retain routes for old clients with an explicit response.
        quake.MapPost("/assets/{id:long}/to-lead", (long id) =>
                Results.Conflict(new { detail = "资产转线索需先完成企业身份审核；请在候选资产中留存并核验。" }))
            .RequireAuthorization(CrmPolicies.LeadManage);

        quake.MapPost("/import", () =>
                Results.Conflict(new { detail = "直接导入线索已暂停；请先留存 Quake 候选资产并完成企业身份审核。" }))
            .RequireAuthorization(CrmPolicies.LeadManage);

        // P0-2: candidate facts and review history stay separate from CRM leads/companies.
        var identity = group.MapGroup("/identity-candidates");
        identity.MapGet("/ai-provider/readiness", (IAiPreparationExecutionGate gate) =>
                Results.Ok(gate.Status()))
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapGet("/sources/status", (IntelligenceSourceCatalog catalog,
                ICurrentTenant tenant, ICurrentCompany company) =>
            {
                if (tenant.Id is not long tenantId || company.Id is not long companyId)
                    return Results.Forbid();
                return Results.Ok(new { items = catalog.List(tenantId, companyId) });
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapGet("/sources/authorizations", async (
                DataSourceAuthorizationService service, CancellationToken ct) =>
                Results.Ok(new { items = await service.ListAsync(ct) }))
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapGet("/sources/usage", async (
                DataSourceExecutionService service, CancellationToken ct) =>
                Results.Ok(new { items = await service.ListUsageAsync(ct) }))
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapGet("/sources/legacy-migration", async (
                LegacySourceMigrationService service, CancellationToken ct) =>
                Results.Ok(new { items = await service.ListAsync(ct) }))
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapGet("/sources/legacy-request-observation", async (
                LegacyRequestTelemetry telemetry, ICurrentTenant tenant, ICurrentCompany company, CancellationToken ct) =>
                Results.Ok(new { enabled = telemetry.Enabled, scope = "database", billingEligible = false,
                    items = await telemetry.SnapshotAsync(tenant.Id, company.Id, ct) }))
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapPost("/sources/legacy-quota-simulation", async (
                LegacyQuotaSimulationInput input, LegacyQuotaSimulationService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.PreviewAsync(input, ct)); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapPost("/sources/legacy-migration/preview", async (
                SourcePolicyPreviewInput input, LegacySourceMigrationService service, CancellationToken ct) =>
            {
                try { return Results.Ok(await service.PreviewAsync(input, ct)); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapGet("/sources/authorizations/{sourceCode}/history", async (
                string sourceCode, DataSourceAuthorizationService service, CancellationToken ct) =>
            {
                if (!DataSourceAuthorizationService.IsKnownSource(sourceCode))
                    return IdentityError(400, "未知数据源。");
                return Results.Ok(new { items = await service.HistoryAsync(sourceCode, ct) });
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapPut("/sources/authorizations/{sourceCode}", async (
                string sourceCode, DataSourceAuthorizationInput input, HttpContext http,
                DataSourceAuthorizationService service, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await service.SaveAsync(sourceCode, input, actorId, ct)); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapGet("/chinaz/status", (ChinazConnectorSettings settings,
            ICurrentTenant tenant, ICurrentCompany company) =>
        {
            if (tenant.Id is not long tenantId || company.Id is not long companyId) return Results.Forbid();
            var state = settings.GetStatus(tenantId, companyId);
            return Results.Ok(new { source = "chinaz", configured = state.Enabled,
                enabled = state.Enabled, authorizationConfigured = state.AuthorizationConfigured,
                authorizationReference = state.AuthorizationReference,
                status = state.Enabled ? "ready" : "not_configured" });
        })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapPut("/chinaz/status", (ChinazSettingsInput input, HttpContext http,
                ChinazConnectorSettings settings, ICurrentTenant tenant, ICurrentCompany company,
                ILogger<ChinazConnectorSettings> logger) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                if (tenant.Id is not long tenantId || company.Id is not long companyId) return Results.Forbid();
                try
                {
                    var saved = settings.Update(tenantId, companyId, input);
                    logger.LogInformation("Chinaz automatic lookup setting changed by user {ActorId} in tenant {TenantId} company {CompanyId}: enabled={Enabled}",
                        actorId, tenantId, companyId, saved.Enabled);
                    return Results.Ok(saved);
                }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapPost("/chinaz/batch-lookup", async (
                ChinazBatchRequest input, ChinazLookupService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.LookupAsync(input, ct)); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapPost("/batch-preview", async (
                IdentityBatchRequest input, HttpContext http, IdentityCandidateBatchService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.ProcessAsync(input, actorId, false, ct)); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapPost("/batch-import", async (
                IdentityBatchRequest input, HttpContext http, IdentityCandidateBatchService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.ProcessAsync(input, actorId, true, ct)); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapGet("/by-asset/{assetId:long}", async (
                long assetId, IdentityCandidateService svc, CancellationToken ct) =>
            {
                var result = await svc.GetForAssetAsync(assetId, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapPost("/by-asset/{assetId:long}", async (
                long assetId, CreateIdentityCandidateInput input, HttpContext http,
                IdentityCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Created($"/api/v1/crm/identity-candidates/by-asset/{assetId}",
                    await svc.CreateAsync(assetId, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapPost("/{id:long}/evidence", async (
                long id, IdentityEvidenceInput input, HttpContext http,
                IdentityCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Created($"/api/v1/crm/identity-candidates/{id}/evidence",
                    await svc.AddEvidenceAsync(id, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapPost("/{id:long}/review", async (
                long id, IdentityReviewInput input, HttpContext http,
                IdentityCandidateService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.ReviewAsync(id, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (DbUpdateConcurrencyException) { return IdentityError(409, "身份候选已变化，请刷新后重试。"); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.IdentityReview);
        identity.MapGet("/{id:long}/ai-preparations", async (
                long id, AiPreparationService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.ListAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapPost("/{id:long}/ai-preparations", async (
                long id, AiPreparationRequest input, HttpContext http, AiPreparationService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Created($"/api/v1/crm/identity-candidates/{id}/ai-preparations",
                    await svc.CreateAsync(id, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapPost("/{id:long}/ai-preparations/{draftId:long}/review", async (
                long id, long draftId, AiPreparationReview input, HttpContext http,
                AiPreparationService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.ReviewAsync(id, draftId, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, CrmPolicies.IdentityReview);
        identity.MapGet("/{id:long}/website-reports", async (
                long id, WebsiteDiagnosticService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.ListAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapGet("/{id:long}/website-reports/{reportId:long}", async (
                long id, long reportId, WebsiteDiagnosticService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.GetAsync(id, reportId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapGet("/{id:long}/content-evidence", async (
                long id, WebsiteContentEvidenceService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.ListAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapGet("/{id:long}/content-evidence/{evidenceId:long}", async (
                long id, long evidenceId, WebsiteContentEvidenceService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.GetAsync(id, evidenceId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapPost("/{id:long}/content-evidence", async (
                long id, WebsiteContentEvidenceInput input, HttpContext http,
                WebsiteContentEvidenceService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Created($"/api/v1/crm/identity-candidates/{id}/content-evidence",
                    await svc.RecordAsync(id, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapPost("/{id:long}/content-evidence/{evidenceId:long}/review", async (
                long id, long evidenceId, WebsiteContentReviewInput input, HttpContext http,
                WebsiteContentEvidenceService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(await svc.ReviewAsync(id, evidenceId, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.IdentityReview);
        identity.MapGet("/website-rechecks", async (string? stage, int? page,
                WebsiteRecheckService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.ListAsync(stage, page ?? 1, ct)); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapGet("/{id:long}/website-recheck", async (long id,
                WebsiteRecheckService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.GetAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapGet("/{id:long}/website-recheck-history", async (long id,
                WebsiteRecheckService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.HistoryAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapPut("/website-rechecks/batch", async (WebsiteRecheckBatchInput input,
                HttpContext http, WebsiteRecheckService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Ok(new { updated = await svc.UpdateBatchAsync(input, actorId, ct) }); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
                catch (DbUpdateException) { return IdentityError(409, "复检计划已被其他人员修改，请刷新重试。"); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, GuliErpAuthorizationPolicies.IdentityRoleAssign);
        identity.MapPost("/{id:long}/website-reports", async (
                long id, WebsiteDiagnosticRequest input, HttpContext http,
                WebsiteDiagnosticService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Accepted($"/api/v1/crm/identity-candidates/{id}/website-reports",
                    await svc.RequestAsync(id, input.Url, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);
        identity.MapGet("/{id:long}/lead-preview", async (
                long id, IdentityLeadPromotionService svc, CancellationToken ct) =>
            {
                try { return Results.Ok(await svc.PreviewAsync(id, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
            })
            .RequireAuthorization(CrmPolicies.LeadRead);
        identity.MapPost("/{id:long}/promote-lead", async (
                long id, PromoteIdentityLeadInput input, HttpContext http,
                IdentityLeadPromotionService svc, CancellationToken ct) =>
            {
                if (GetUserId(http) is not long actorId) return Results.Forbid();
                try { return Results.Created("/api/v1/crm/leads", await svc.PromoteAsync(id, input, actorId, ct)); }
                catch (KeyNotFoundException) { return Results.NotFound(); }
                catch (ArgumentException ex) { return IdentityError(400, ex.Message); }
                catch (DbUpdateConcurrencyException) { return IdentityError(409, "身份审核版本已变化，请重新预检。"); }
                catch (InvalidOperationException ex) { return IdentityError(409, ex.Message); }
            })
            .RequireAuthorization(CrmPolicies.LeadManage, CrmPolicies.IdentityReview);
        // Quake 查询冷却检查：相同条件30天内查过则返回最近记录
        quake.MapGet("/history/check", async (
                string query,
                int? cooldownDays,
                QuakeQueryHistoryService svc,
                CancellationToken ct) =>
            {
                var record = await svc.CheckCooldownAsync(query, cooldownDays ?? 30, ct);
                if (record == null)
                    return Results.Ok(new { inCooldown = false });
                return Results.Ok(new
                {
                    inCooldown = true,
                    queriedAt = record.QueriedAt,
                    resultCount = record.ResultCount,
                    importedCount = record.ImportedCount,
                    queryText = record.QueryText
                });
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // Quake 查询历史列表
        quake.MapGet("/history", async (
                int? limit,
                QuakeQueryHistoryService svc,
                CancellationToken ct) =>
            {
                var list = await svc.GetRecentAsync(limit ?? 20, ct);
                return Results.Ok(new { total = list.Count, items = list });
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // ===== ICP 备案查询 =====
        var icp = group.MapGroup("/icp");
        icp.MapPost("/lookup", async (
                List<string> domains,
                IcpLookupService svc,
                HttpContext http,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                if (!IsValidExternalLookupBatch(domains))
                    return Results.BadRequest(new { detail = "每次请检测 1–50 个网站。" });
                try
                {
                    var results = await svc.LookupBatchAsync(domains, ct);
                    var found = results.Count(r => r.Found);
                    return Results.Ok(new { total = results.Count, found, items = results });
                }
                catch (Exception ex)
                {
                    return ExternalLookupError(ex, http, loggerFactory, "icp_lookup");
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // ===== 网站检测 =====
        var wc = group.MapGroup("/website-check");
        wc.MapPost("", async (
                List<string> domains,
                WebsiteCheckService svc,
                HttpContext http,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                if (!IsValidExternalLookupBatch(domains))
                    return Results.BadRequest(new { detail = "每次请检测 1–50 个网站。" });
                try
                {
                    var results = await svc.CheckBatchAsync(domains, ct);
                    int accessible = results.Count(r => r.Accessible);
                    int hasIssues = results.Count(r => r.Issues.Count > 0);
                    // 不返回 HTML 原文
                    var items = results.Select(r => new
                    {
                        r.Domain,
                        r.Accessible,
                        r.HttpsSupported,
                        r.ResponseMs,
                        r.Title,
                        r.Description,
                        r.HasViewport,
                        r.HasFavicon,
                        r.ContentLength,
                        r.Issues,
                        r.Error,
                        r.HasBaiduAds,
                        r.BaiduAdsType
                    });
                    return Results.Ok(new { total = results.Count, accessible, hasIssues, items });
                }
                catch (Exception ex)
                {
                    return ExternalLookupError(ex, http, loggerFactory, "website_check");
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // ===== GEO 检测 =====
        var geo = group.MapGroup("/geo-check");
        geo.MapPost("", async (
                List<string> domains,
                GeoCheckService svc,
                HttpContext http,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                if (!IsValidExternalLookupBatch(domains))
                    return Results.BadRequest(new { detail = "每次请检测 1–50 个网站。" });
                try
                {
                    var results = await svc.CheckBatchAsync(domains, ct);
                    int checkedCount = results.Count(r => r.Checked);
                    int criticalCount = results.Count(r => r.Band == "critical");
                    return Results.Ok(new { total = results.Count, geoChecked = checkedCount, critical = criticalCount, items = results });
                }
                catch (Exception ex)
                {
                    return ExternalLookupError(ex, http, loggerFactory, "geo_check");
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        // 单位反查：根据单位名称+可选网址，批量查ICP/网站状态/GEO
        var reverse = group.MapGroup("/reverse-lookup");
        reverse.MapPost("", async (
                ReverseLookupRequest req,
                ReverseLookupService svc,
                ReverseLookupRecordService records,
                CancellationToken ct) =>
            {
                if (req.Items is null || req.Items.Count is < 1 or > 50
                    || req.Items.Any(i => string.IsNullOrWhiteSpace(i.CompanyName) || i.CompanyName.Length > 300
                        || i.Website?.Length > 500))
                    return Results.BadRequest(new { detail = "每次请输入 1–50 家单位，名称不超过 300 字，网址不超过 500 字。" });
                try
                {
                    var items = req.Items.Select(i => new ReverseLookupItem
                    {
                        CompanyName = i.CompanyName,
                        Website = i.Website
                    }).ToList();
                    var results = await svc.LookupBatchAsync(items, req.IncludeGeo, ct);
                    await records.SaveBatchAsync(results, ct);
                    return Results.Ok(new { total = results.Count, items = results });
                }
                catch (Exception)
                {
                    return Results.Problem("单位反查或结果保存失败，请稍后重试。", statusCode: 500);
                }
            })
            .RequireAuthorization(CrmPolicies.LeadManage);

        reverse.MapGet("/history", async (ReverseLookupRecordService records, CancellationToken ct) =>
            Results.Ok(new { items = await records.GetRecentAsync(ct) }))
            .RequireAuthorization(CrmPolicies.LeadRead);

        return routes;
    }

    public static IResult ExternalLookupError(Exception exception, HttpContext http,
        ILoggerFactory loggerFactory, string source)
    {
        if (exception is ArgumentException)
            return Results.Json(new { detail = "参数无效，请检查查询条件、网站地址和批量数量。" }, statusCode: 400);

        loggerFactory.CreateLogger("GuliERP.Api.Crm.ExternalLookup")
            .LogWarning("External lookup {Source} failed: {ExceptionType}; trace={TraceId}",
                source, exception.GetType().Name, http.TraceIdentifier);
        var status = exception switch
        {
            InvalidOperationException => 503,
            OperationCanceledException => 504,
            HttpRequestException or TimeoutException => 502,
            _ => 500
        };
        return Results.Json(new { detail = "查询暂时失败，请稍后重试。" }, statusCode: status);
    }

    public static IResult QuakeCsvParseError() =>
        Results.BadRequest(new { detail = "CSV 解析失败，请检查文件格式后重试。" });

    private static IResult IdentityError(int status, string detail) => Results.Json(new
    {
        type = "about:blank", title = "身份核查失败", status, code = "validation_failed",
        detail = CrmClientErrorDetails.Safe(status, detail)
    }, statusCode: status);

    private static long? GetUserId(HttpContext http)
    {
        // 与系统其他地方一致，使用 NameIdentifier（同时兼容 "sub"）
        var claim = http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? http.User.FindFirst("sub")?.Value;
        return long.TryParse(claim, out var id) ? id : null;
    }

    /// <summary>
    /// 普通编辑试图更换或清空负责人时的统一出口（400）。
    ///
    /// <para>
    /// 文案必须指向对应的专门分配入口：请求方不能只看到"被拒绝"，
    /// 还要知道该走哪条路，否则调用方会以为普通编辑接口坏了。
    /// </para>
    /// </summary>
    private static IResult OwnerChangeNotAllowed(CrmOwnerChangeNotAllowedException ex) =>
        Results.BadRequest(ex.Kind switch
        {
            CrmOwnerEntityKind.Company =>
                "普通编辑不能更换或清空客户负责人；请使用专门的分配操作 POST /api/v1/crm/companies/{id}/assign。",
            CrmOwnerEntityKind.Deal =>
                "普通编辑不能更换或清空商机负责人；请使用专门的分配操作 POST /api/v1/crm/deals/{id}/assign。",
            _ =>
                "普通编辑不能更换或清空跟进记录负责人；请使用专门的分配操作 POST /api/v1/crm/activities/{id}/assign。"
        });

    /// <summary>
    /// 专门分配接口的请求形状校验。
    ///
    /// <para>
    /// <c>ownerId</c> 省略与显式 null 在现有 JSON 绑定下不可区分，
    /// 因此「不写 ownerId」不能被当成清空，必须显式声明 <c>clear = true</c>；
    /// 两者同时给出也是歧义请求，一律拒绝。
    /// </para>
    /// </summary>
    private static IResult? ValidateAssignOwnerRequest(AssignOwnerRequest request)
    {
        if (request.Clear && request.OwnerId.HasValue)
            return Results.BadRequest("分配负责人与清空负责人不能同时请求。");
        if (!request.Clear && !request.OwnerId.HasValue)
            return Results.BadRequest("请指定负责人，或明确请求清空负责人。");
        return null;
    }

    /// <summary>
    /// Wraps a CRM create call so that InvalidOperationException
    /// (e.g. CrmCompanyId not found) returns 400 instead of 500.
    /// </summary>
    private static async Task<IResult> CreateWithBadRequestAsync<T>(
        Func<Task<T>> createFunc,
        Func<T, long> idSelector,
        string locationPrefix) where T : class
    {
        try
        {
            var created = await createFunc();
            return Results.Created($"{locationPrefix}/{idSelector(created)}", created);
        }
        catch (InvalidOperationException ex)
        {
            return CreateValidationError(ex);
        }
    }

    /// <summary>
    /// CRM 创建类校验失败的统一出口。
    ///
    /// <para>
    /// 命中 <see cref="IsKnownCreateValidationError"/> 的已知校验错误 → 400 +
    /// 固定对外文案；未命中的异常不是客户端输入问题，原样抛出交全局异常处理（500）。
    /// </para>
    ///
    /// <para>
    /// 对外暴露是为了让 API 契约测试直接断言状态码与对外文案，
    /// 而不是复制一份判定逻辑（复制品会与真实端点行为漂移）。
    /// </para>
    /// </summary>
    public static IResult CreateValidationError(InvalidOperationException ex)
    {
        if (!IsKnownCreateValidationError(ex.Message))
        {
            // 不降级成 400：保留原异常与堆栈，交 FoundationExceptionHandler → 500。
            ExceptionDispatchInfo.Capture(ex).Throw();
        }

        return Results.BadRequest(new { error = SafeCreateValidationMessage(ex.Message) });
    }

    private static bool IsKnownCreateValidationError(string message) =>
        message.StartsWith("CRM company ", StringComparison.Ordinal)
            && message.EndsWith(" not found.", StringComparison.Ordinal)
        || message is
            "正式商机必须关联已确认的需求评估及当前版本，请从线索需求评估创建。"
            or "来源需求评估不存在、未确认或版本已变化，请重新核对。"
            or "来源线索尚未转为当前客户。"
            or "商机业务品类与确认的需求方向不一致。"
            or "该确认需求已有商机，请在商机管理中继续跟进。"
            or ActivityService.DealScopeErrorMessage;

    private static string SafeCreateValidationMessage(string message) =>
        message.StartsWith("CRM company ", StringComparison.Ordinal)
            ? "关联的 CRM 客户不存在，请重新选择。"
            : message;
}

/// <summary>Only accepts a write-only runtime credential; responses never contain the key.</summary>
public sealed record QuakeRuntimeKeyInput(string? ApiKey);

/// <summary>Quake API 通道：实时拉取并映射 15 字段。</summary>
public sealed record QuakeFetchRequest(string Query, int? Size);

/// <summary>Quake 手动通道：粘贴 Quake 导出的 CSV 原文。</summary>
public sealed record QuakeCsvImportRequest(string Csv);

/// <summary>
/// Quake 结果落线索时的标题与归属地映射规则。
/// 单独成类，避免把映射逻辑散落在端点里。
/// </summary>
internal static class QuakeLeadMapping
{
/// <summary>
/// 判断标题是否为服务器默认页/错误页标题。
/// 这类标题绝不能当公司名，否则会把 "404 Not Found" 写进 CompanyName。
/// 规则与 QuakeAssetCleaner 的 R8 保持一致。
/// </summary>
public static bool IsDefaultPageTitle(string? title)
{
    if (string.IsNullOrWhiteSpace(title)) return true;
    var t = title.Trim();
    var exact = new[]
    {
        "301 Moved Permanently", "302 Found", "303 See Other", "307 Temporary Redirect",
        "308 Permanent Redirect", "400 Bad Request", "401 Unauthorized", "403 Forbidden",
        "404 Not Found", "410 Gone", "500 Internal Server Error", "502 Bad Gateway",
        "503 Service Unavailable", "Welcome to nginx!", "Welcome to OpenResty!", "It works!",
        "IIS Windows Server", "Test Page", "没有找到站点", "站点已到期", "域名停靠",
    };
    if (exact.Any(e => string.Equals(t, e, StringComparison.OrdinalIgnoreCase))) return true;
    return System.Text.RegularExpressions.Regex.IsMatch(
        t, @"^\d{3}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>
/// 把 Quake 的归属地映射到 CompanyCity。
///
/// <para><b>重要限制</b>：Quake 的 city_cn / province_cn 是 <b>服务器 IP 所在地</b>，
/// 不是企业注册地。实测一批 50 条样本里 28% 落在香港。把它直接写入企业城市会污染数据，
/// 因此这里只映射<b>明确可识别</b>的山东城市，其余一律回落到
/// <see cref="CompanyCity.OutOfProvince"/> 而不是编造。</para>
/// </summary>
public static CompanyCity MapCity(string? provinceCn, string? cityCn)
{
    var text = (provinceCn ?? string.Empty).Trim() + (cityCn ?? string.Empty).Trim();

    if (text.Contains("潍坊", StringComparison.Ordinal)) return CompanyCity.Weifang;
    if (text.Contains("青岛", StringComparison.Ordinal)) return CompanyCity.Qingdao;
    if (text.Contains("烟台", StringComparison.Ordinal)) return CompanyCity.Yantai;
    if (text.Contains("济南", StringComparison.Ordinal)) return CompanyCity.Jinan;
    if (text.Contains("淄博", StringComparison.Ordinal)) return CompanyCity.Zibo;
    if (text.Contains("临沂", StringComparison.Ordinal)) return CompanyCity.Linyi;
    if (text.Contains("山东", StringComparison.Ordinal)) return CompanyCity.ShandongOther;
    return CompanyCity.OutOfProvince;
}
}

/// <summary>Quake 批量导入项</summary>
public class QuakeImportItem
{
    public string Domain { get; set; } = "";
    public string? CompanyName { get; set; }
    public string? Title { get; set; }
    public string? Ip { get; set; }
    public string? City { get; set; }
    public bool HasBaiduAds { get; set; }
}
