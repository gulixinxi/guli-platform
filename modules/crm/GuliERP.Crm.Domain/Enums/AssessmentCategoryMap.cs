namespace GuliERP.Crm.Domain.Enums;

/// <summary>
/// Temporary wire-name compatibility for the four demand-assessment categories.
/// The assessment tables still store these strings until the B2 database
/// migration is reviewed and applied. Unknown values are never mapped to Other.
/// </summary>
public static class AssessmentCategoryMap
{
    public static IReadOnlyList<string> Ordered { get; } = Array.AsReadOnly(
        new[] { "website", "geo", "social_media", "foreign_trade" });

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(Ordered, StringComparer.Ordinal);

    public static BusinessCategory ToBusinessCategory(string category) => category switch
    {
        "website" => BusinessCategory.WebsiteBuild,
        "geo" => BusinessCategory.GEOOpt,
        "social_media" => BusinessCategory.SocialMedia,
        "foreign_trade" => BusinessCategory.ForeignTradeLeads,
        _ => throw new ArgumentException($"未知的需求评估类别：{category}", nameof(category))
    };

    public static string ToWireCategory(BusinessCategory category) => category switch
    {
        BusinessCategory.WebsiteBuild => "website",
        BusinessCategory.GEOOpt => "geo",
        BusinessCategory.SocialMedia => "social_media",
        BusinessCategory.ForeignTradeLeads => "foreign_trade",
        _ => throw new ArgumentException($"不支持的需求评估业务品类：{category}", nameof(category))
    };
}
