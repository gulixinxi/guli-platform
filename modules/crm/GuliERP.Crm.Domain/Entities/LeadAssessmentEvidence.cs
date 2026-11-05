using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;
using GuliERP.Crm.Domain.Enums;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>
/// 一条需求评估的结构化证据（PRD 第 4.1 节）。
///
/// <para><b>为什么拆表</b>：评估原本只有一个 <c>Evidence</c> 字段，类型是
/// <c>MaxLength(2000)</c> 的自由文本。于是「已确认」只意味着「某人写了一段话」，
/// 无法回答三个问题：依据是哪条链接、什么时候取的、谁核验的。这三条正是 PRD
/// 对阶段 B 的验收条件（「一条建议可追溯来源与核验状态」）。</para>
///
/// <para><b>为什么保留 <c>Category</c> 冗余</b>：它和 <see cref="LeadDemandAssessment"/>
/// 里的 Category 一致，但把它复制到证据行上，使得「按需求方向汇总某条线索的全部
/// 证据」只需要一张表，不必 join 回评估表。写入时由服务保证两者一致。</para>
/// </summary>
public sealed class LeadAssessmentEvidence : ICompanyScoped
{
    [Key] public long Id { get; set; }

    public long TenantId { get; set; }
    public long CompanyId { get; set; }

    /// <summary>所属线索。</summary>
    public long LeadId { get; set; }

    /// <summary>所属评估记录。</summary>
    public long AssessmentId { get; set; }

    /// <summary>冗余的需求方向，取值与评估记录一致（website/geo/social_media/foreign_trade）。</summary>
    public BusinessCategory Category { get; set; }

    /// <summary>
    /// 当前证据对应的评估版本号。重新保存时旧证据会被替换；本表不作为
    /// 历史证据档案，历史评估的元数据保存在 Revision 表。
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// 证据类型。约定取值见 <c>LeadEvidenceTypes</c>（website / icp / geo /
    /// signal / quake / reverse_lookup / manual / legacy_text）。
    /// </summary>
    [Required, MaxLength(40)] public string EvidenceType { get; set; } = "";

    /// <summary>证据来源链接。人工口述类证据可为空。</summary>
    [MaxLength(1000)] public string? Url { get; set; }

    /// <summary>采集时间。没有来源可考时留空——空值表示「未知」，不表示「当时」。</summary>
    public DateTimeOffset? CollectedAt { get; set; }

    /// <summary>
    /// 核验状态，取值见 <c>LeadEvidenceVerifications</c>。
    /// <para><b>not_found 与 confirmed_absent 必须分开</b>：前者是「查了没查到」，
    /// 后者是「确认不存在」。PRD 第 4 节把这条列为必须避免的误判。</para>
    /// </summary>
    [Required, MaxLength(30)] public string VerificationStatus { get; set; } = "not_verified";

    /// <summary>证据原文或说明。</summary>
    [MaxLength(2000)] public string? Note { get; set; }
}
