using GuliERP.Crm.Domain.Enums;

namespace GuliERP.Crm.Application;

// ===== 通用分页 =====
public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

// ===== 线索池（Lead）DTO =====
public record LeadDto(
    long Id,
    string CompanyName,
    string? CreditCode,
    string? WebsiteDomain,
    string? ContactName,
    string? ContactPosition,
    string? Mobile,
    string? Email,
    string? Wechat,
    string? Phone,
    CompanyCity? City,
    string? District,
    string? Address,
    CompanyIndustry? Industry,
    CompanyScale? Scale,
    SubjectType SubjectType,
    SourceChannel Source,
    string? SourceUrl,
    DateTimeOffset? CollectedAt,
    string? ExternalKey,
    string? ExternalSource,
    LeadStatus Status,
    int? AiScore,
    IntentRating? Rating,
    bool IsRatingSuggested,
    long? OwnerId,
    DateTimeOffset? ClaimedAt,
    long? ConvertedToCompanyId,
    DateTimeOffset? ConvertedAt,
    long? ConvertedBy,
    string? Remarks,
    List<string> Tags,
    DateTimeOffset CreatedAt,
    long? CreatedBy,
    long? SourceIdentityCandidateId = null);

public record LeadListQuery(
    string? Keyword,
    LeadStatus? Status,
    CompanyCity? City,
    CompanyIndustry? Industry,
    IntentRating? Rating,
    long? OwnerId,
    SourceChannel? Source,
    SubjectType? SubjectType,
    string? Tag,
    int Page,
    int PageSize);

public record CreateLeadRequest(
    string CompanyName,
    string? CreditCode,
    string? WebsiteDomain,
    string? ContactName,
    string? ContactPosition,
    string? Mobile,
    string? Email,
    string? Wechat,
    string? Phone,
    CompanyCity? City,
    string? District,
    string? Address,
    CompanyIndustry? Industry,
    CompanyScale? Scale,
    SourceChannel Source,
    string? SourceUrl,
    string? ExternalKey,
    string? ExternalSource,
    string? Remarks,
    List<string>? Tags = null,
    long? SourceReverseLookupRecordId = null);

/// <summary>
/// 一条结构化证据（PRD 第 4.1 节的 <c>evidence[]</c> 元素）。
/// </summary>
/// <param name="EvidenceType">见 <c>LeadEvidenceTypes</c>。</param>
/// <param name="Url">来源链接；口述类证据可空。</param>
/// <param name="CollectedAt">采集时间；不知道就留空，不要拿「填写时间」冒充。</param>
/// <param name="VerificationStatus">见 <c>LeadEvidenceVerifications</c>。</param>
/// <param name="Note">证据原文或说明。</param>
public record EvidenceItemInput(
    string EvidenceType,
    string? Url,
    DateTimeOffset? CollectedAt,
    string VerificationStatus,
    string? Note);

/// <summary>
/// 线索需求评估的保存输入。
/// </summary>
/// <param name="Evidence">
/// 【已废弃】旧的自由文本证据字段。仅在读取历史数据时仍有值。
/// 新写入请使用 <paramref name="EvidenceItems"/>；服务端会忽略本字段。
/// </param>
public record LeadDemandAssessmentInput(
    string State,
    string? Confidence,
    string? Evidence,
    string? Reason,
    string? NextAction,
    EvidenceItemInput[]? EvidenceItems = null);

/// <summary>批量导入结果统计</summary>
public record BatchImportResult(int Total, int Imported, int Skipped, int Failed);

public record UpdateLeadRequest(
    string CompanyName,
    string? CreditCode,
    string? WebsiteDomain,
    string? ContactName,
    string? ContactPosition,
    string? Mobile,
    string? Email,
    string? Wechat,
    string? Phone,    CompanyCity? City,
    string? District,
    string? Address,
    CompanyIndustry? Industry,
    CompanyScale? Scale,
    SourceChannel Source,
    string? SourceUrl,
    IntentRating? Rating,
    string? Remarks);

public record ConvertLeadRequest(
    long LeadId,
    string FullName,
    string? CreditCode,
    string? ShortName,
    CompanyCity City,
    string? District,
    string? Address,
    CompanyIndustry Industry,
    CompanyScale? Scale,
    CompanyRevenue? Revenue,
    bool HasWebsite,
    WebsiteStatus? WebsiteStatus,
    long? OwnerId,
    string? Remarks);

// ===== 企业/客户主档（Company）DTO =====
public record CompanyDto(
    long Id,
    string CompanyNo,
    string FullName,
    string? CreditCode,
    string? ShortName,
    string? WebsiteDomain,
    CompanyCity City,
    string? District,
    string? Address,
    CompanyIndustry Industry,
    CompanyScale? Scale,
    CompanyRevenue? Revenue,
    bool HasWebsite,
    WebsiteStatus? WebsiteStatus,
    SourceChannel Source,
    string? SourceUrl,
    DateTimeOffset? CollectedAt,
    long? OwnerId,
    CustomerStatus Status,
    IntentRating? Rating,
    bool IsRatingSuggested,
    string? ExternalKey,
    string? ExternalSource,
    string? Remarks,
    List<string> Tags,
    DateTimeOffset CreatedAt,
    long? CreatedBy);

public record CompanyListQuery(
    string? Keyword,
    CustomerStatus? Status,
    CompanyCity? City,
    CompanyIndustry? Industry,
    IntentRating? Rating,
    long? OwnerId,
    bool? HasWebsite,
    int Page,
    int PageSize);

public record CreateCompanyRequest(
    string FullName,
    string? CreditCode,
    string? ShortName,
    string? WebsiteDomain,
    CompanyCity City,
    string? District,
    string? Address,
    CompanyIndustry Industry,
    CompanyScale? Scale,
    CompanyRevenue? Revenue,
    bool HasWebsite,
    WebsiteStatus? WebsiteStatus,
    SourceChannel Source,
    string? SourceUrl,
    long? OwnerId,
    CustomerStatus? Status,
    IntentRating? Rating,
    string? ExternalKey,
    string? Remarks);

public record UpdateCompanyRequest(
    string FullName,
    string? CreditCode,
    string? ShortName,
    string? WebsiteDomain,
    CompanyCity City,
    string? District,
    string? Address,
    CompanyIndustry Industry,
    CompanyScale? Scale,
    CompanyRevenue? Revenue,
    bool HasWebsite,
    WebsiteStatus? WebsiteStatus,
    SourceChannel Source,
    string? SourceUrl,
    long? OwnerId,
    CustomerStatus Status,
    IntentRating? Rating,
    string? Remarks);

// ===== 今日待办（下次动作逾期查询） =====
public record TodayTaskDto(
    long ActivityId,
    long CompanyId,
    string CompanyName,
    string NextAction,
    DateOnly NextActionDate,
    long? OwnerId,
    bool IsOverdue);

// ===== 联系人（Contact）DTO =====
public record ContactDto(
    long Id,
    long CrmCompanyId,
    string Name,
    string? Position,
    DecisionRole? DecisionRole,
    string? Mobile,
    string? Phone,
    string? Wechat,
    string? Email,
    bool IsKeyPerson,
    string? Remarks,
    DateTimeOffset CreatedAt);

public record CreateContactRequest(
    long CrmCompanyId,
    string Name,
    string? Position,
    DecisionRole? DecisionRole,
    string? Mobile,
    string? Phone,
    string? Wechat,
    string? Email,
    bool IsKeyPerson,
    string? Remarks);

public record UpdateContactRequest(
    string Name,
    string? Position,
    DecisionRole? DecisionRole,
    string? Mobile,
    string? Phone,
    string? Wechat,
    string? Email,
    bool IsKeyPerson,
    string? Remarks);

// ===== 商机（Deal）DTO =====
public record DealDto(
    long Id,
    string DealNo,
    string Name,
    long CrmCompanyId,
    string? CompanyName,
    long? ContactId,
    BusinessCategory Category,
    decimal ExpectedAmount,
    DealStage Stage,
    string? ExpectedCloseMonth,
    int? WinRate,
    long? OwnerId,
    DealStatus Status,
    LostReason? LostReason,
    string? Remarks,
    DateTimeOffset CreatedAt,
    long? CreatedBy);

public record DealListQuery(
    string? Keyword,
    DealStatus? Status,
    DealStage? Stage,
    BusinessCategory? Category,
    long? OwnerId,
    long? CrmCompanyId,
    int Page,
    int PageSize);

public record CreateDealRequest(
    string Name,
    long CrmCompanyId,
    long? ContactId,
    BusinessCategory Category,
    decimal ExpectedAmount,
    DealStage? Stage,
    string? ExpectedCloseMonth,
    int? WinRate,
    long? OwnerId,
    string? Remarks,
    long? SourceLeadDemandAssessmentId = null,
    int? SourceLeadDemandAssessmentVersion = null);

public record UpdateDealRequest(
    string Name,
    BusinessCategory Category,
    decimal ExpectedAmount,
    DealStage Stage,
    string? ExpectedCloseMonth,
    int? WinRate,
    long? OwnerId,
    DealStatus Status,
    LostReason? LostReason,
    string? Remarks);

// ===== 跟进记录（Activity）DTO =====
public record ActivityDto(
    long Id,
    long CrmCompanyId,
    string CompanyName,
    long? DealId,
    string? DealName,
    FollowType FollowType,
    DateTimeOffset FollowAt,
    string Content,
    CustomerAttitude? Attitude,
    string NextAction,
    DateOnly? NextActionDate,
    long? OwnerId,
    string? Attachments,
    string? Remarks,
    DateTimeOffset CreatedAt,
    long? CreatedBy);

public record CreateActivityRequest(
    long CrmCompanyId,
    long? DealId,
    FollowType FollowType,
    DateTimeOffset? FollowAt,
    string Content,
    CustomerAttitude? Attitude,
    string NextAction,
    DateOnly? NextActionDate,
    long? OwnerId,
    string? Attachments,
    string? Remarks);

public record UpdateActivityRequest(
    FollowType FollowType,
    DateTimeOffset FollowAt,
    string Content,
    CustomerAttitude? Attitude,
    string NextAction,
    DateOnly? NextActionDate,
    long? OwnerId,
    string? Attachments,
    string? Remarks);

// ===== 负责人分配（专门操作） =====

/// <summary>
/// 专门分配／清空负责人的请求。
///
/// <para>
/// <c>ownerId</c> 与 <c>clear</c> 互斥：
/// <list type="bullet">
///   <item><c>ownerId</c> 有值 ⇒ 分配给当前公司的有效成员（写入前做成员资格校验）。</item>
///   <item><c>clear = true</c> ⇒ 清空负责人，必须显式声明。</item>
/// </list>
/// 两者都不给 ⇒ 400。理由：<c>ownerId</c> 省略与显式 null 在现有 JSON 绑定下不可区分，
/// 若把「没写」当成清空，就等于把 R41 记录的静默清空缺陷换个入口重演一遍。
/// </para>
/// </summary>
public sealed record AssignOwnerRequest(long? OwnerId, bool Clear = false);

// ===== 导入导出留痕（ImportExportLog）DTO =====
public record ImportExportLogDto(
    long Id,
    OperationType OperationType,
    string Scope,
    long? OperatorId,
    DateTimeOffset OperatedAt,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    LogStatus Status,
    string? SourceFile,
    string? FailureDetails,
    string? Remarks);

public record ImportExportLogListQuery(
    OperationType? OperationType,
    LogStatus? Status,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);

// ===== 企业情报信号 =====
public record SignalDto(
    long Id,
    long? LeadId,
    long? CompanyRefId,
    string CompanyName,
    SignalType Type,
    string Title,
    string? Detail,
    string Source,
    string? SourceUrl,
    DateTimeOffset? EventAt,
    DateTimeOffset CollectedAt,
    int Confidence,
    SignalStatus Status,
    long? HandledBy,
    DateTimeOffset? HandledAt,
    string? HandleNote,
    string? EvidenceType,
    string? QueryParams,
    string? RawContent,
    int VerificationStatus,
    long? VerifiedBy,
    DateTimeOffset? VerifiedAt);

public record SignalListQuery(
    SignalType? Type,
    SignalStatus? Status,
    string? Keyword,
    int Page,
    int PageSize);

public record CreateSignalCommand(
    long? LeadId,
    long? CompanyRefId,
    string CompanyName,
    SignalType Type,
    string Title,
    string? Detail,
    string Source,
    string? SourceUrl,
    DateTimeOffset? EventAt,
    int Confidence,
    string? EvidenceType,
    string? QueryParams,
    string? RawContent);

public record UpdateSignalStatusCommand(
    SignalStatus Status,
    string? HandleNote);

public record UpdateVerificationCommand(
    int VerificationStatus,
    string? RawContent,
    string? VerifyNote);

// 单位反查
public record ReverseLookupRequestItem(string CompanyName, string? Website);
public record ReverseLookupRequest(List<ReverseLookupRequestItem> Items, bool IncludeGeo = false);

// ============================================================
// ERP 客商候选匹配（只读，不写关联关系）
// 查询结果统一称"候选匹配"，不显示"已同步"或"已关联"。
// ============================================================

/// <summary>
/// 单个ERP客商候选匹配项。来自MDM BusinessPartner的只读投影。
/// </summary>
public sealed record ErpMatchCandidateDto(
    long ErpPartnerId,
    string ErpPartnerCode,
    string ErpPartnerName,
    string? ErpPartnerShortName,
    int ErpPartnerRole,
    string? TaxNumber,
    string? ContactPerson,
    string? Phone,
    string? Email,
    string? City,
    string? AddressLine1,
    int Status,
    /// <summary>匹配方式：TaxNumber（信用代码精确匹配）或 Name（名称模糊匹配）</summary>
    string MatchMethod,
    /// <summary匹配置信度 0-100，TaxNumber精确=100</summary>
    int MatchConfidence);

/// <summary>
/// CRM客户 ↔ ERP客商候选匹配结果。
/// 只读查询结果，不创建任何关联关系。
/// </summary>
public sealed record ErpMatchResultDto(
    long CrmCompanyId,
    string CrmCompanyName,
    string? CrmCreditCode,
    /// <summary>信用代码是否已提供（用于精确匹配）</summary>
    bool CreditCodeProvided,
    /// <summary>候选匹配数量，可能为0</summary>
    int CandidateCount,
    IReadOnlyList<ErpMatchCandidateDto> Candidates,
    /// <summary>查询时间（UTC）</summary>
    DateTimeOffset QueriedAt);
