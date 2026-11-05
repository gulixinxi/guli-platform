using System.Text.RegularExpressions;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>清洗判定结果。</summary>
public enum QuakeCleanVerdict
{
    /// <summary>像真实企业站点，可进入线索流程。</summary>
    Valid,

    /// <summary>证据不足，需人工核验。</summary>
    Suspect,

    /// <summary>基础设施噪音，直接丢弃。</summary>
    Rejected,
}

/// <summary>清洗判定明细。</summary>
public sealed record QuakeCleanResult(QuakeCleanVerdict Verdict, int Score, IReadOnlyList<string> Reasons)
{
    public bool IsValid => Verdict == QuakeCleanVerdict.Valid;
}

/// <summary>
/// Quake 结果清洗器（启发式，纯函数，无副作用）。
///
/// <para><b>为什么需要清洗</b>：实测 Quake 返回的绝大多数资产是基础设施而非企业站点
/// （FRP 隧道、CDN、对象存储、邮件系统、默认错误页、随机免费域名）。
/// 一批 50 条的实测样本里，可用标题的只有 34 条，而真正像企业站点的约 10 条（约 20%）。</para>
///
/// <para><b>规则的数据来源</b>：下列每一张表都是从实测样本归纳出来的，不是拍脑袋。
/// 若要调整，先跑 <c>Harvest-QuakeSample.ps1</c> 拿新样本，再改这里的表。</para>
///
/// <para><b>已知的信号陷阱（实测结论）</b>：
/// <list type="bullet">
/// <item><c>location.scene_cn</c> 不可用 —— 真实企业站点同样标为「数据中心」，无法区分。</item>
/// <item><c>location.city_cn</c> 是 IP 所在城市，不是企业注册城市（实测 28% 落在香港），
/// 不可直接写入 Company.City。</item>
/// <item><c>ip</c> 在当前 key 档位被脱敏（<c>120.*.*.*</c>），无法用于 IP 级去重。</item>
/// <item><c>service.http.title</c> 可能是错误页标题（403/404/301），必须先排除再当企业名用。</item>
/// </list></para>
///
/// <para>本类是启发式，不是判定真理。<see cref="Suspect"/> 就是留给人工核验的出口。</para>
/// </summary>
public sealed class QuakeAssetCleaner
{
    /// <summary>Web 业务常用端口。</summary>
    private static readonly HashSet<int> WebPorts = new() { 80, 443 };

    /// <summary>
    /// 对象存储 / 静态托管的 Server 头：这类响应根本不是「网站」。
    /// 实测出现 AliyunOSS、tencent-ci、OBS。
    /// </summary>
    private static readonly HashSet<string> ObjectStorageServers = new(StringComparer.OrdinalIgnoreCase)
    {
        "aliyunoss", "tencent-ci", "obs", "cos", "qiniu", "aliyun oss", "oss",
    };

    /// <summary>
    /// CDN / 云厂商的域名后缀：域名主体属于厂商，不属于最终客户。
    /// 实测出现 cdn.dnsv1.com。
    /// </summary>
    private static readonly HashSet<string> InfrastructureDomainSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "aliyuncs.com", "aliyuncsslb.com", "aliyuncdn.com", "alikunlun.com",
        "myqcloud.com", "qcloud.com", "qiniucdn.com", "qiniudns.com",
        "bdydns.com", "bcebos.com", "ksyuncdn.com", "hwcdn.net",
        "cloudfront.net", "amazonaws.com", "azureedge.net", "akamaiedge.net",
        "akamai.net", "cdn20.com", "cdnjs.cloudflare.com", "dnsv1.com",
        "tcdnvod.com", "cdnhwc1.com", "cdnhwc2.com", "cdngslb.com",
    };

    /// <summary>
    /// 常见的基础设施子域名前缀：这些是主机的服务入口，不代表企业官网。
    /// </summary>
    private static readonly HashSet<string> InfrastructureFirstLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "mail", "webmail", "smtp", "imap", "pop", "ftp", "sftp", "ns", "ns1", "ns2", "mx",
        "cdn", "static", "img", "image", "assets", "download", "dl",
        "api", "apis", "gateway", "gw", "proxy", "cdn2",
        "git", "gitlab", "jenkins", "ci", "cd", "deploy", "test", "staging",
        "monitor", "grafana", "kibana", "elastic", "log", "logs", "status",
        "vpn", "remote", "rdp", "ssh", "erp", "crm", "oa", "sso", "auth",
    };

    /// <summary>
    /// 免费网盘 / 停放页域名：实测出现 lanzov.com（蓝奏云）。
    /// </summary>
    private static readonly HashSet<string> FreeHostingDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "lanzou.com", "lanzov.com", "lanzoup.com", "lanzoui.com",
        "547349.com", "95.lanzou.com",
    };

    /// <summary>
    /// 服务器默认页 / 错误页标题。这些标题说明「没拿到真实页面」，绝不能当企业名。
    /// 实测样本中出现过 301/404/410/503/400、OpenResty 欢迎页、IIS 默认页、「没有找到站点」。
    /// </summary>
    private static readonly HashSet<string> DefaultPageTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "301 Moved Permanently", "302 Found", "303 See Other", "307 Temporary Redirect",
        "308 Permanent Redirect",
        "400 Bad Request", "400",
        "401 Unauthorized", "401",
        "403 Forbidden", "403",
        "404 Not Found", "404",
        "410 Gone", "410",
        "500 Internal Server Error", "500",
        "502 Bad Gateway", "502",
        "503 Service Unavailable", "503",
        "Welcome to nginx!", "Welcome to OpenResty!", "It works!",
        "IIS Windows Server", "Test Page", "Directory listing for",
        "没有找到站点", "站点已到期", "域名停靠",
    };

    /// <summary>
    /// 企业类标题特征。命中则加分（但不单独构成 valid，仍需总分）。
    /// </summary>
    private static readonly string[] CompanyTitleMarkers =
    {
        "有限公司", "有限责任公司", "有限合伙", "有限", "合伙", "企业", "股份", "集团", "公司",
        "工厂", "厂", "实业", "科技", "贸易",
        "工会", "医院", "学校", "大学", "学院", "培训", "教育局", "政府", "委员会",
        "局", "中心", "银行", "保险", "协会", "研究院", "研究所", "合作社",
        "事务所", "商行", "门市", "门市部", "旗舰店", "官方网站", "官网",
    };

    /// <summary>
    /// 随机生成域名的形态：6 位十六进制子域 + 冷门后缀。
    /// 实测样本中出现 abc3708.ecwpqzm.info、6b6d35a.bhqako.asia。
    /// </summary>
    private static readonly Regex RandomSubdomainPattern =
        // 实测样本中 abc3708.ecwpqzm.info、6b6d35a.bhqako.asia 都是 7 位十六进制子域。
        new(@"^[0-9a-f]{6,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>冷门后缀，往往与批量注册/停放页相关。</summary>
    private static readonly HashSet<string> LowValueTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "info", "asia", "top", "xyz", "club", "site", "online", "icu", "buzz",
    };

    /// <summary>Quake 在当前 key 档位用这个值代替受版权保护的字段值。</summary>
    public const string NoPermissionMarker = "暂无权限";

    /// <summary>
    /// 对一条 Quake 资产做清洗判定。纯函数：同样的输入永远得到同样的输出。
    /// </summary>
    public QuakeCleanResult Evaluate(QuakeAssetCandidate c)
    {
        var reasons = new List<string>();
        var score = 50;

        var domain = (c.Domain ?? string.Empty).Trim().Trim('.');
        var host = string.IsNullOrWhiteSpace(c.Hostname) ? domain : c.Hostname.Trim().Trim('.');
        var title = (c.Title ?? string.Empty).Trim();

        // ---------- R1 没有任何域名：无法作为企业线索 ----------
        if (string.IsNullOrWhiteSpace(host))
        {
            return new QuakeCleanResult(QuakeCleanVerdict.Rejected, 0,
                new[] { "R1:无域名" });
        }

        // ---------- R2 非 Web 端口 ----------
        if (c.Port is int port && !WebPorts.Contains(port))
        {
            score -= 25;
            reasons.Add($"R2:非Web端口({port})");
        }

        // ---------- R3 对象存储 / 静态托管 ----------
        if (!string.IsNullOrWhiteSpace(c.ServerHeader)
            && ObjectStorageServers.Contains(c.ServerHeader.Trim()))
        {
            score -= 50;
            reasons.Add("R3:对象存储响应");
        }

        // ---------- R4 CDN / 云厂商域名 ----------
        if (InfrastructureDomainSuffixes.Any(s =>
                domain.EndsWith("." + s, StringComparison.OrdinalIgnoreCase)
                || string.Equals(domain, s, StringComparison.OrdinalIgnoreCase)))
        {
            score -= 40;
            reasons.Add("R4:CDN/云厂商域名");
        }

        // ---------- R5 基础设施子域名前缀 ----------
        var firstLabel = host.Split('.')[0];
        if (InfrastructureFirstLabels.Contains(firstLabel))
        {
            score -= 30;
            reasons.Add($"R5:基础设施子域名({firstLabel})");
        }

        // ---------- R6 免费网盘 / 停放页 ----------
        var labels = host.Split('.');
        var registrable = labels.Length >= 2 ? string.Join('.', labels[^2..]) : host;
        if (FreeHostingDomains.Any(f => registrable.EndsWith(f, StringComparison.OrdinalIgnoreCase)))
        {
            score -= 40;
            reasons.Add("R6:免费网盘/停放页");
        }

        // ---------- R7 随机生成域名 ----------
        if (RandomSubdomainPattern.IsMatch(firstLabel)
            && labels.Length >= 2
            && LowValueTlds.Contains(labels[^1]))
        {
            score -= 35;
            reasons.Add("R7:随机生成域名");
        }

        // ---------- R8 默认页 / 错误页标题 ----------
        if (DefaultPageTitles.Contains(title))
        {
            score -= 60;
            reasons.Add("R8:默认页/错误页标题");
        }

        // ---------- R9 标题不可用 ----------
        var titleUsable = title.Length > 0
            && !string.Equals(title, NoPermissionMarker, StringComparison.Ordinal);
        if (!titleUsable)
        {
            score -= 20;
            reasons.Add("R9:标题缺失或无权限");
        }
        else if (CompanyTitleMarkers.Any(m => title.Contains(m, StringComparison.Ordinal)))
        {
            // 企业类特征：这是最有价值的正向信号。
            score += 30;
            reasons.Add("P1:标题含企业特征");
        }

        // ---------- R10 归属地信息缺失 ----------
        if (string.IsNullOrWhiteSpace(c.CountryCn)) { score -= 10; reasons.Add("R10:缺国家"); }
        if (string.IsNullOrWhiteSpace(c.ProvinceCn)) { score -= 5; reasons.Add("R11:缺省份"); }

        // ---------- 评分收敛 ----------
        score = Math.Clamp(score, 0, 100);

        // ---------- 判定 ----------
        // 任一硬否决规则命中即直接 Rejected，不让加分把它救回来。
        var hardReject = reasons.Any(r =>
            r.StartsWith("R1", StringComparison.Ordinal)
            || r.StartsWith("R3", StringComparison.Ordinal)
            || r.StartsWith("R4", StringComparison.Ordinal)   // CDN/云厂商域名：主体属于厂商，不属于客户
            || r.StartsWith("R6", StringComparison.Ordinal)
            || r.StartsWith("R7", StringComparison.Ordinal)
            || r.StartsWith("R8", StringComparison.Ordinal));

        QuakeCleanVerdict verdict;
        if (hardReject) verdict = QuakeCleanVerdict.Rejected;
        else if (score >= 60) verdict = QuakeCleanVerdict.Valid;
        else verdict = QuakeCleanVerdict.Suspect;

        if (reasons.Count == 0) reasons.Add("无命中规则");
        return new QuakeCleanResult(verdict, score, reasons);
    }
}

/// <summary>参与清洗判定的最小字段集（与持久化实体解耦，便于单测）。</summary>
public sealed class QuakeAssetCandidate
{
    public string? IpAddress { get; set; }
    public string? Domain { get; set; }
    public string? Hostname { get; set; }
    public string? Title { get; set; }
    public string? ServerHeader { get; set; }
    public string? CountryCn { get; set; }
    public string? ProvinceCn { get; set; }
    public string? CityCn { get; set; }
    public int? Port { get; set; }
}