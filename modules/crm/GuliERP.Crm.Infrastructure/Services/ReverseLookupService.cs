using Microsoft.Extensions.Logging;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>
/// 单位反查服务：根据单位名称和可选网址，批量查询 ICP 备案、网站状态、GEO 评分。
/// </summary>
public class ReverseLookupService
{
    private readonly IcpLookupService _icpLookup;
    private readonly WebsiteCheckService _websiteCheck;
    private readonly GeoCheckService _geoCheck;
    private readonly ILogger<ReverseLookupService> _logger;

    public ReverseLookupService(
        IcpLookupService icpLookup,
        WebsiteCheckService websiteCheck,
        GeoCheckService geoCheck,
        ILogger<ReverseLookupService> logger)
    {
        _icpLookup = icpLookup;
        _websiteCheck = websiteCheck;
        _geoCheck = geoCheck;
        _logger = logger;
    }

    /// <summary>
    /// 批量反查单位信息。
    /// </summary>
    /// <param name="items">单位列表（名称+可选网址）</param>
    /// <param name="includeGeo">是否包含 GEO 检测（较慢，默认关闭）</param>
    public async Task<List<ReverseLookupResult>> LookupBatchAsync(
        List<ReverseLookupItem> items,
        bool includeGeo = false,
        CancellationToken ct = default)
    {
        var results = new List<ReverseLookupResult>();
        var index = 0;
        foreach (var item in items)
        {
            index++;
            _logger.LogInformation("反查 {Index}/{Total}: {Name}", index, items.Count, item.CompanyName);
            var result = await LookupSingleAsync(item, includeGeo, ct);
            results.Add(result);
            // 间隔 1 秒，避免请求过快
            await Task.Delay(1000, ct);
        }
        return results;
    }

    private async Task<ReverseLookupResult> LookupSingleAsync(
        ReverseLookupItem item,
        bool includeGeo,
        CancellationToken ct)
    {
        var result = new ReverseLookupResult
        {
            CompanyName = item.CompanyName,
            InputWebsite = item.Website
        };

        // 1. 如果有输入网址，提取域名
        string? domain = null;
        if (!string.IsNullOrWhiteSpace(item.Website))
        {
            domain = ExtractDomain(item.Website);
            result.Domain = domain;
        }

        // 2. 如果有域名，查 ICP 备案
        if (!string.IsNullOrEmpty(domain))
        {
            try
            {
                var icp = await _icpLookup.LookupAsync(domain, ct);
                if (icp?.QueryStatus == "found")
                {
                    result.HasIcp = true;
                    result.IcpCompanyName = icp.UnitName;
                    result.IcpLicense = icp.ServiceLicence;
                    result.IcpNature = icp.Nature;
                }
                else if (icp?.QueryStatus == "not_found")
                {
                    result.HasIcp = false;
                }
                else
                {
                    result.HasIcp = null;
                    result.Error = icp?.Error ?? "ICP 查询未完成";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ICP 查询失败 {Domain}: {ExceptionType}", domain, ex.GetType().Name);
                result.Error = "ICP 查询失败，请稍后重试。";
            }
        }
        else
        {
            result.WebsiteStatus = "网址未提供，待核验";
        }

        // 3. 如果有域名，做网站检测
        if (!string.IsNullOrEmpty(domain))
        {
            try
            {
                var url = domain.StartsWith("http") ? domain : $"https://{domain}";
                var check = await _websiteCheck.CheckAsync(url, ct);
                result.HasWebsite = check.Error is null ? check.Accessible : null;
                result.WebsiteTitle = check.Title;
                result.HasHttps = check.HttpsSupported;
                result.ResponseTimeMs = check.ResponseMs;
                result.MobileUsable = check.HasViewport;
                result.WebsiteStatus = check.Error is not null ? "检测失败" : check.Accessible ? "正常" : "无法访问";
                if (check.Error is not null) result.Error = check.Error;
                if (!check.HttpsSupported && check.Accessible)
                    result.WebsiteStatus = "无HTTPS";
            }
            catch (Exception ex)
            {
                _logger.LogWarning("网站检测失败 {Domain}: {ExceptionType}", domain, ex.GetType().Name);
                result.WebsiteStatus = "检测失败";
                result.Error = string.IsNullOrEmpty(result.Error) ? "网站检测失败，请稍后重试。" : result.Error;
            }
        }

        // 4. 可选 GEO 检测
        if (includeGeo && !string.IsNullOrEmpty(domain) && result.HasWebsite == true)
        {
            try
            {
                var url = domain.StartsWith("http") ? domain : $"https://{domain}";
                var geo = await _geoCheck.CheckAsync(url, ct);
                if (geo.Checked && geo.Error is null)
                {
                    result.GeoScore = geo.Score;
                    result.GeoBand = geo.Band;
                }
                else if (geo.Error is not null)
                    result.Error = geo.Error;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("GEO 检测失败 {Domain}: {ExceptionType}", domain, ex.GetType().Name);
                result.Error = "GEO 检测失败，请稍后重试。";
            }
        }

        // 5. 商机建议
        result.Suggestion = GenerateSuggestion(result);
        return result;
    }

    private static string GenerateSuggestion(ReverseLookupResult r)
    {
        if (string.IsNullOrWhiteSpace(r.Domain))
            return "官网信息未知：先核实官网，再评估建站需求";
        if (r.HasWebsite == false)
            return "输入网址当前无法访问：核实域名和网站状态后再评估建站需求";
        if (r.HasHttps == false)
            return "输入网址未检测到 HTTPS：核实配置后再评估网站升级需求";
        if (r.GeoScore.HasValue && r.GeoScore < 40)
            return "GEO初测评分偏低：复测并确认目标场景后再评估优化需求";
        if (r.WebsiteStatus == "无法访问")
            return "输入网址无法访问：核实原因后再评估网站修复需求";
        return "暂未发现明确需求：由销售结合企业资料继续核验";
    }

    private static string? ExtractDomain(string url)
    {
        try
        {
            url = url.Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = url[7..];
            else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = url[8..];
            // 去掉路径和参数
            var slashIndex = url.IndexOf('/');
            if (slashIndex > 0)
                url = url[..slashIndex];
            // 去掉端口
            var colonIndex = url.IndexOf(':');
            if (colonIndex > 0)
                url = url[..colonIndex];
            return url.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>反查输入项</summary>
public class ReverseLookupItem
{
    public string CompanyName { get; set; } = "";
    public string? Website { get; set; }
}

/// <summary>反查结果</summary>
public class ReverseLookupResult
{
    public string? RecordId { get; set; }
    public string CompanyName { get; set; } = "";
    public string? InputWebsite { get; set; }
    public string? Domain { get; set; }
    public bool? HasWebsite { get; set; }
    public string? WebsiteStatus { get; set; }
    public string? WebsiteTitle { get; set; }
    public bool? HasHttps { get; set; }
    public bool? MobileUsable { get; set; }
    public int? ResponseTimeMs { get; set; }
    public bool? HasIcp { get; set; }
    public string? IcpCompanyName { get; set; }
    public string? IcpLicense { get; set; }
    public string? IcpNature { get; set; }
    public int? GeoScore { get; set; }
    public string? GeoBand { get; set; }
    public string? Suggestion { get; set; }
    public string? Error { get; set; }
}
