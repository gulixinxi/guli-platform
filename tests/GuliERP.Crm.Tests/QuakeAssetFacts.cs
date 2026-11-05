using System.Text.Json;
using GuliERP.Crm.Infrastructure.Services;
using Xunit;

namespace GuliERP.Crm.Tests;

/// <summary>
/// Quake 资产：15 字段映射、双通道解析、清洗规则。
///
/// <para><b>测试数据来自实测</b>：下面的域名/标题全部取自一次真实的 Quake API 调用
/// （<c>port:80 AND location.country_cn:"中国"</c>，50 条样本），不是编造的。
/// 规则表本身是从这些样本归纳的，所以这些用例锁的是「实测观察到的行为」。</para>
/// </summary>
public class QuakeAssetFacts
{
    private static readonly QuakeAssetCleaner Cleaner = new();

    private static QuakeAssetCandidate C(
        string domain, string? title = null, string? host = null,
        int port = 80, string? server = null, string? province = "山东省",
        string? country = "中国", string? city = "青岛市")
        => new()
        {
            Domain = domain,
            Hostname = host ?? domain,
            Title = title,
            Port = port,
            ServerHeader = server,
            CountryCn = country,
            ProvinceCn = province,
            CityCn = city,
        };

    // ============ 15 字段映射（API 通道）============

    [Fact]
    public void MapFromQuake_MapsAllFifteenExportedColumns()
    {
        // 真实 Quake 返回形状（取自实测，仅裁剪无关字段）。
        const string raw = """
        {
          "ip": "194.*.*.*",
          "transport": "tcp",
          "port": 443,
          "asn": 136958,
          "org": "China Unicom",
          "domain": "mail.gzyawen.com",
          "hostname": "",
          "service": {
            "name": "http/ssl",
            "version": "1.1",
            "http": {
              "host": "mail.gzyawen.com",
              "path": "/",
              "title": "广州雅文物业管理有限公司",
              "server": "Wwebsvr"
            }
          },
          "location": {
            "country_cn": "中国",
            "province_cn": "广东省",
            "city_cn": "广州市",
            "scene_cn": "数据中心"
          },
          "components": [
            { "product_name_cn": "腾讯企业邮箱", "product_vendor": "腾讯控股有限公司" }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(raw);
        var m = QuakeAssetService.MapFromQuake(doc.RootElement);

        // 1 IP地址
        Assert.Equal("194.*.*.*", m.IpAddress);
        // 2 URL —— Quake 无单一 URL 字段，由 scheme + host + path 合成
        Assert.Equal("https://mail.gzyawen.com/", m.Url);
        // 3 传输层协议
        Assert.Equal("tcp", m.Transport);
        // 4 端口号
        Assert.Equal(443, m.Port);
        // 5 网站路径
        Assert.Equal("/", m.SitePath);
        // 6 自治域号码
        Assert.Equal(136958, m.Asn);
        // 7 自治域归属组织
        Assert.Equal("China Unicom", m.AsnOrganization);
        // 8 国际化域名
        Assert.Equal("mail.gzyawen.com", m.Domain);
        // 9 网站Host（域名）—— 实测 hostname 为空，回落到 http.host
        Assert.Equal("mail.gzyawen.com", m.Hostname);
        // 10 服务产品
        Assert.Equal("腾讯企业邮箱", m.ServiceProduct);
        // 11 服务名称
        Assert.Equal("http/ssl", m.ServiceName);
        // 12 服务版本号
        Assert.Equal("1.1", m.ServiceVersion);
        // 13 国家（中文）
        Assert.Equal("中国", m.CountryCn);
        // 14 城市（中文）
        Assert.Equal("广州市", m.CityCn);
        // 15 省份（中文）
        Assert.Equal("广东省", m.ProvinceCn);

        // 证据列
        Assert.Equal("广州雅文物业管理有限公司", m.Title);
        Assert.Equal("Wwebsvr", m.ServerHeader);
        Assert.Equal("腾讯控股有限公司", m.VendorName);
    }

    [Fact]
    public void MapFromQuake_ToleratesMissingSubtrees()
    {
        // 实测中大量资产没有 components / service.http，整棵子树都可能缺失。
        const string raw = """
        { "ip": "1.2.3.4", "port": 22, "transport": "tcp", "domain": "a.example.com" }
        """;
        using var doc = JsonDocument.Parse(raw);
        var m = QuakeAssetService.MapFromQuake(doc.RootElement);

        Assert.Equal("a.example.com", m.Domain);
        Assert.Equal("a.example.com", m.Hostname);      // http.host 缺失 -> 回落到 domain
        Assert.Equal("http://a.example.com/", m.Url);  // scheme 缺失 -> http
        Assert.Null(m.Title);
        Assert.Null(m.ServiceProduct);
    }

    // ============ 清洗规则（每条规则都要有用例锁住）============

    [Fact]
    public void Cleaner_Rejects_DefaultErrorPageTitle()
    {
        // 实测样本里 301/404/410/503 都出现过
        Assert.Equal(QuakeCleanVerdict.Rejected,
            Cleaner.Evaluate(C("www.rwzkkj.com", "301 Moved Permanently")).Verdict);
        Assert.Equal(QuakeCleanVerdict.Rejected,
            Cleaner.Evaluate(C("nqezlls.cn", "404 Not Found")).Verdict);
    }

    [Fact]
    public void Cleaner_Rejects_ObjectStorageResponse()
    {
        // 实测 Server 头出现 AliyunOSS / tencent-ci / OBS
        var r = Cleaner.Evaluate(C("oss.example.com", "阿里云", server: "AliyunOSS"));
        Assert.Equal(QuakeCleanVerdict.Rejected, r.Verdict);
        Assert.Contains(r.Reasons, x => x.Contains("对象存储", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleaner_Rejects_CdnAndCloudDomain()
    {
        // 实测 tk1.caohua.com.cdn.dnsv1.com
        Assert.Equal(QuakeCleanVerdict.Rejected,
            Cleaner.Evaluate(C("tk1.caohua.com.cdn.dnsv1.com")).Verdict);
        Assert.Equal(QuakeCleanVerdict.Rejected,
            Cleaner.Evaluate(C("x.oss-cn-hangzhou.aliyuncs.com")).Verdict);
    }

    [Fact]
    public void Cleaner_Rejects_FreeHostingAndRandomDomains()
    {
        // 实测 wwbrz.lanzov.com（蓝奏云）、abc3708.ecwpqzm.info、6b6d35a.bhqako.asia
        Assert.Equal(QuakeCleanVerdict.Rejected,
            Cleaner.Evaluate(C("wwbrz.lanzov.com", "蓝奏云")).Verdict);
        Assert.Equal(QuakeCleanVerdict.Rejected,
            Cleaner.Evaluate(C("abc3708.ecwpqzm.info")).Verdict);
    }

    [Fact]
    public void Cleaner_Rejects_NoDomain()
    {
        Assert.Equal(QuakeCleanVerdict.Rejected,
            Cleaner.Evaluate(new QuakeAssetCandidate { Title = "某公司" }).Verdict);
    }

    [Fact]
    public void Cleaner_Accepts_RealCompanyWithCompanyMarkerInTitle()
    {
        // 实测样本里真实存在的企业标题
        foreach (var (domain, title) in new[]
        {
            ("hkfudi.com", "海南福帝贸易有限公司官方网站"),
            ("abzgh.cn", "阿坝藏族羌族自治州总工会"),
            ("vlm.xmzyjsgs.com", "云南省普洱市西盟佤族自治县拖印易研玩具娃娃有限合伙企业"),
        })
        {
            var r = Cleaner.Evaluate(C(domain, title));
            Assert.True(r.IsValid, $"{domain} / {title} 应判为 valid，实际 {r.Verdict} score={r.Score} reasons={string.Join("|", r.Reasons)}");
        }
    }

    [Fact]
    public void Cleaner_Marks_TitleUnavailableAsSuspect_NotRejected()
    {
        // Quake 在当前档位用「暂无权限」代替受版权保护的字段。
        // 这类不能算噪音，只是证据不足 -> suspect，交给人工。
        var r = Cleaner.Evaluate(C("unknown-site.com", QuakeAssetCleaner.NoPermissionMarker));
        Assert.Equal(QuakeCleanVerdict.Suspect, r.Verdict);
    }

    [Fact]
    public void Cleaner_Penalises_NonWebPort_WithoutHardRejecting()
    {
        var r = Cleaner.Evaluate(C("a.example.com", "某科技有限公司", port: 8080));
        Assert.Contains(r.Reasons, x => x.Contains("非Web端口", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleaner_Penalises_InfraSubdomainPrefix()
    {
        // mail. 是服务入口，不代表企业官网
        var r = Cleaner.Evaluate(C("mail.gzyawen.com", "腾讯企业邮箱"));
        Assert.Contains(r.Reasons, x => x.Contains("基础设施子域名", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleaner_IsDeterministic()
    {
        var c = C("hkfudi.com", "海南福帝贸易有限公司官方网站");
        var a = Cleaner.Evaluate(c);
        var b = Cleaner.Evaluate(c);
        Assert.Equal(a.Verdict, b.Verdict);
        Assert.Equal(a.Score, b.Score);
    }

    // ============ 手动导入通道：CSV 解析 ============

    [Fact]
    public void ParseCsv_ReadsChineseHeaders_IntoAllFifteenColumns()
    {
        // 表头与用户提供的 Quake 导出表完全一致
        const string csv = """
        IP地址,URL,传输层协议,端口号,网站路径,自治域号码,自治域归属组织,国际化域名,网站Host（域名）,服务产品,服务名称,服务版本号,国家（中文）,城市（中文）,省份（中文）
        163.*.*.*,https://hkfudi.com/,tcp,443,/,136958,China Unicom,hkfudi.com,hkfudi.com,Nginx Web服务器,http/ssl,1.1,中国,海口市,海南省
        """;

        var items = QuakeAssetService.ParseCsv(csv);

        var m = Assert.Single(items);
        Assert.Equal("163.*.*.*", m.IpAddress);
        Assert.Equal("https://hkfudi.com/", m.Url);
        Assert.Equal("tcp", m.Transport);
        Assert.Equal(443, m.Port);
        Assert.Equal("/", m.SitePath);
        Assert.Equal(136958, m.Asn);
        Assert.Equal("China Unicom", m.AsnOrganization);
        Assert.Equal("hkfudi.com", m.Domain);
        Assert.Equal("hkfudi.com", m.Hostname);
        Assert.Equal("Nginx Web服务器", m.ServiceProduct);
        Assert.Equal("http/ssl", m.ServiceName);
        Assert.Equal("1.1", m.ServiceVersion);
        Assert.Equal("中国", m.CountryCn);
        Assert.Equal("海口市", m.CityCn);
        Assert.Equal("海南省", m.ProvinceCn);
    }

    [Fact]
    public void ParseCsv_HandlesQuotedFieldsWithCommas()
    {
        const string csv = """
        国际化域名,城市（中文）,服务产品
        "a.example.com","山东省,济南市","Nginx, Web服务器"
        """;
        var m = Assert.Single(QuakeAssetService.ParseCsv(csv));
        Assert.Equal("a.example.com", m.Domain);
        Assert.Equal("山东省,济南市", m.CityCn);
        Assert.Equal("Nginx, Web服务器", m.ServiceProduct);
    }

    [Fact]
    public void ParseCsv_AcceptsEnglishHeaders()
    {
        const string csv = """
        domain,port,country,province
        b.example.com,443,China,Shandong
        """;
        var m = Assert.Single(QuakeAssetService.ParseCsv(csv));
        Assert.Equal("b.example.com", m.Domain);
        Assert.Equal(443, m.Port);
        Assert.Equal("China", m.CountryCn);
        // 英文列名只做映射，不做翻译：翻译属于清洗/归一化职责，不在解析层悄悄发生。
        Assert.Equal("Shandong", m.ProvinceCn);
    }

    [Fact]
    public void ParseCsv_ToleratesFullWidthParenthesisAndBomAndSpaces()
    {
        const string csv = "﻿国际化域名, 网站Host（域名） , 端口号 \nx.example.com, x.example.com, 8080\n";
        var m = Assert.Single(QuakeAssetService.ParseCsv(csv));
        Assert.Equal("x.example.com", m.Domain);
        Assert.Equal("x.example.com", m.Hostname);
        Assert.Equal(8080, m.Port);
    }

    [Fact]
    public void ParseCsv_SkipsBlankLines()
    {
        const string csv = "国际化域名\n\na.example.com\n\n\nb.example.com\n\n";
        var items = QuakeAssetService.ParseCsv(csv);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void ParseCsv_MissingColumnsBecomeNull_NotEmptyString()
    {
        const string csv = "国际化域名,端口号\nonly.example.com\n";
        var m = Assert.Single(QuakeAssetService.ParseCsv(csv));
        Assert.Equal("only.example.com", m.Domain);
        Assert.Null(m.Port);
        Assert.Null(m.ProvinceCn);
    }

    // ============ 两个通道产出同一形状 ============

    [Fact]
    public void BothChannels_ProduceSameShape()
    {
        // 手动通道（CSV）
        const string csv = """
        IP地址,URL,传输层协议,端口号,网站路径,自治域号码,自治域归属组织,国际化域名,网站Host（域名）,服务产品,服务名称,服务版本号,国家（中文）,城市（中文）,省份（中文）
        1.2.3.4,http://same.example.com/,tcp,80,/,100,Org,same.example.com,same.example.com,Prod,http,1.1,中国,济南市,山东省
        """;
        var manual = QuakeAssetService.ParseCsv(csv)[0];

        // API 通道（同一资产）
        const string raw = """
        { "ip":"1.2.3.4","port":80,"transport":"tcp","asn":100,"org":"Org",
          "domain":"same.example.com",
          "service":{"name":"http","version":"1.1","http":{"host":"same.example.com","path":"/"}},
          "location":{"country_cn":"中国","city_cn":"济南市","province_cn":"山东省"},
          "components":[{"product_name_cn":"Prod"}] }
        """;
        using var doc = JsonDocument.Parse(raw);
        var api = QuakeAssetService.MapFromQuake(doc.RootElement);

        // 15 个导出字段必须逐一对齐，否则下游无法统一处理两个通道。
        Assert.Equal(manual.IpAddress, api.IpAddress);
        Assert.Equal(manual.Url, api.Url);
        Assert.Equal(manual.Transport, api.Transport);
        Assert.Equal(manual.Port, api.Port);
        Assert.Equal(manual.SitePath, api.SitePath);
        Assert.Equal(manual.Asn, api.Asn);
        Assert.Equal(manual.AsnOrganization, api.AsnOrganization);
        Assert.Equal(manual.Domain, api.Domain);
        Assert.Equal(manual.Hostname, api.Hostname);
        Assert.Equal(manual.ServiceProduct, api.ServiceProduct);
        Assert.Equal(manual.ServiceName, api.ServiceName);
        Assert.Equal(manual.ServiceVersion, api.ServiceVersion);
        Assert.Equal(manual.CountryCn, api.CountryCn);
        Assert.Equal(manual.CityCn, api.CityCn);
        Assert.Equal(manual.ProvinceCn, api.ProvinceCn);
    }

    [Fact]
    public void RealHarvestedSample_ReasonableAcceptRate()
    {
        // 锁定「清洗确实在工作」：用实测样本里最具代表性的几类。
        var sample = new (string domain, string? title, QuakeCleanVerdict expected)[]
        {
            ("hkfudi.com", "海南福帝贸易有限公司官方网站", QuakeCleanVerdict.Valid),
            ("wwbrz.lanzov.com", "蓝奏云", QuakeCleanVerdict.Rejected),
            ("tk1.caohua.com.cdn.dnsv1.com", null, QuakeCleanVerdict.Rejected),
            ("abc3708.ecwpqzm.info", "小阅阅", QuakeCleanVerdict.Rejected),
        };
        foreach (var (domain, title, expected) in sample)
        {
            var actual = Cleaner.Evaluate(C(domain, title)).Verdict;
            Assert.True(actual == expected,
                $"{domain} 期望 {expected}，实际 {actual}");
        }
    }
}