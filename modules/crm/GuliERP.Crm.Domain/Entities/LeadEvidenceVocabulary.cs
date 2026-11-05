namespace GuliERP.Crm.Domain.Entities;

/// <summary>
/// 需求评估证据的受控词表。与 <c>LeadAssessmentEvidence</c> 的字符串列一一对应。
///
/// <para>用封闭集合而不是自由文本，是因为这些值会被查询和校验；
/// 往开放集合里加词会让「这条证据到底核验过没有」变得不可判定。</para>
/// </summary>
public static class LeadEvidenceTypes
{
    /// <summary>官网可访问性/HTTPS/移动端等直接检测。</summary>
    public const string Website = "website";

    /// <summary>ICP 备案查询结果。</summary>
    public const string Icp = "icp";

    /// <summary>GEO 可见性诊断结果。</summary>
    public const string Geo = "geo";

    /// <summary>情报信号（SignalCenter）。</summary>
    public const string Signal = "signal";

    /// <summary>Quake 资产采集记录。</summary>
    public const string Quake = "quake";

    /// <summary>单位反查快照。</summary>
    public const string ReverseLookup = "reverse_lookup";

    /// <summary>销售人工录入。</summary>
    public const string Manual = "manual";

    /// <summary>
    /// 迁移回填专用：结构化上线前写进自由文本字段的历史证据。
    /// 只在迁移时产生，UI 应当把它显示为「历史记录（未结构化）」而不是当成有效证据。
    /// </summary>
    public const string LegacyText = "legacy_text";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Website, Icp, Geo, Signal, Quake, ReverseLookup, Manual, LegacyText
    };
}

/// <summary>
/// 证据核验状态。命名刻意区分「查了没查到」和「确认不存在」。
/// </summary>
public static class LeadEvidenceVerifications
{
    /// <summary>尚未核验（默认）。</summary>
    public const string NotVerified = "not_verified";

    /// <summary>已核验，证据成立。</summary>
    public const string Verified = "verified";

    /// <summary>已核验，证据不成立（例如官网实际可访问，推翻了「无官网」假设）。</summary>
    public const string Refuted = "refuted";

    /// <summary>查询过但没有结果。<b>不等于</b> <see cref="ConfirmedAbsent"/>。</summary>
    public const string NotFound = "not_found";

    /// <summary>确认不存在。必须有人工判断支撑。</summary>
    public const string ConfirmedAbsent = "confirmed_absent";

    /// <summary>来源已失效或数据过期。</summary>
    public const string Outdated = "outdated";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NotVerified, Verified, Refuted, NotFound, ConfirmedAbsent, Outdated
    };
}