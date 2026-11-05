using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Domain.Entities;

/// <summary>
/// One Quake asset observation, company-scoped, stored BEFORE any lead is
/// created so the raw evidence survives the cleaning decision.
///
/// <para>
/// The 15 <c>Exported*</c>-aligned properties mirror the columns of the Quake
/// export exactly (IP / URL / transport / port / path / ASN / ASN org / domain
/// / host / service product / service name / service version / country /
/// city / province). Both ingestion channels — live Quake API and manual
/// CSV/JSON import — write the SAME shape, so downstream code cannot tell
/// (and does not care) where a row came from.
/// </para>
///
/// <para>
/// <see cref="Source"/> records the channel, <see cref="CleanVerdict"/> and
/// <see cref="CleanReasons"/> record the cleaning verdict, and
/// <see cref="RawJson"/> keeps the original payload for audit. Nothing is
/// discarded: a rejected asset is still stored, it is only marked.
/// </para>
/// </summary>
[Table("quake_asset_records", Schema = "crm")]
public sealed class QuakeAssetRecord : ICompanyScoped
{
    [Key] public long Id { get; set; }
    public long TenantId { get; set; }
    public long CompanyId { get; set; }

    /// <summary>Groups one import or one API pull.</summary>
    public Guid BatchId { get; set; }

    // ================= the 15 exported columns =================

    /// <summary>IP地址</summary>
    [MaxLength(64)] public string? IpAddress { get; set; }

    /// <summary>URL（Quake 无单一 URL 字段，由 scheme + host + path 合成）</summary>
    [MaxLength(500)] public string? Url { get; set; }

    /// <summary>传输层协议（tcp / udp）</summary>
    [MaxLength(16)] public string? Transport { get; set; }

    /// <summary>端口号</summary>
    public int? Port { get; set; }

    /// <summary>网站路径</summary>
    [MaxLength(500)] public string? SitePath { get; set; }

    /// <summary>自治域号码（ASN）</summary>
    public long? Asn { get; set; }

    /// <summary>自治域归属组织</summary>
    [MaxLength(200)] public string? AsnOrganization { get; set; }

    /// <summary>国际化域名</summary>
    [MaxLength(253)] public string? Domain { get; set; }

    /// <summary>网站Host（域名）</summary>
    [MaxLength(253)] public string? Hostname { get; set; }

    /// <summary>服务产品</summary>
    [MaxLength(200)] public string? ServiceProduct { get; set; }

    /// <summary>服务名称</summary>
    [MaxLength(100)] public string? ServiceName { get; set; }

    /// <summary>服务版本号</summary>
    [MaxLength(100)] public string? ServiceVersion { get; set; }

    /// <summary>国家（中文）</summary>
    [MaxLength(100)] public string? CountryCn { get; set; }

    /// <summary>城市（中文）</summary>
    [MaxLength(100)] public string? CityCn { get; set; }

    /// <summary>省份（中文）</summary>
    [MaxLength(100)] public string? ProvinceCn { get; set; }

    // ================= evidence retained for the cleaner =================

    /// <summary>首页标题。注意：该字段可能是错误页标题（如 403/404），不能当作企业名直接使用。</summary>
    [MaxLength(500)] public string? Title { get; set; }

    /// <summary>Server 响应头</summary>
    [MaxLength(200)] public string? ServerHeader { get; set; }

    /// <summary>厂商（来自 components[].product_vendor）</summary>
    [MaxLength(200)] public string? VendorName { get; set; }

    /// <summary>场景（location.scene_cn）。注意：企业站点同样可能是「数据中心」，不能作为清洗依据。</summary>
    [MaxLength(50)] public string? SceneCn { get; set; }

    /// <summary>AS 名称（location.asname）</summary>
    [MaxLength(200)] public string? AsnName { get; set; }

    /// <summary>操作系统</summary>
    [MaxLength(50)] public string? OsName { get; set; }

    /// <summary>Quake 自身资产 id，可用于回溯</summary>
    [MaxLength(200)] public string? QuakeAssetId { get; set; }

    // ================= ingestion + cleaning verdict =================

    /// <summary>api | manual</summary>
    [MaxLength(20)] public string Source { get; set; } = "api";

    /// <summary>valid | suspect | rejected</summary>
    [MaxLength(20)] public string CleanVerdict { get; set; } = "unknown";

    /// <summary>清洗评分（越高越像真实企业站点）</summary>
    public int CleanScore { get; set; }

    /// <summary>命中的清洗规则，逗号分隔</summary>
    [MaxLength(1000)] public string? CleanReasons { get; set; }

    /// <summary>是否已转为线索</summary>
    public bool Imported { get; set; }

    /// <summary>转为线索后的线索 id</summary>
    public long? ImportedLeadId { get; set; }

    /// <summary>原始返回/原始行，jsonb，用于审计</summary>
    [Column(TypeName = "jsonb")]
    public string RawJson { get; set; } = "{}";

    /// <summary>观测时间</summary>
    public DateTime ObservedAt { get; set; } = DateTime.UtcNow;
}