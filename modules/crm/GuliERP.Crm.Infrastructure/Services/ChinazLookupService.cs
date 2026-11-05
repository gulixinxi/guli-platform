using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using GuliERP.Foundation.Kernel;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record ChinazBatchRequest(List<string> AssetIds);
public sealed record ChinazLookupRow(string AssetId, string Domain, string Status, string? ProposedName,
    string? ReportedIcpNumber, string SourceReference, DateTime? ObservedAt, string Note);
public sealed record ChinazBatchResult(string Source, bool Configured, List<ChinazLookupRow> Items);

/// <summary>Optional licensed connector. Its default state performs zero vendor requests.</summary>
public sealed class ChinazLookupService(HttpClient http, ChinazConnectorSettings settings, CrmDbContext db,
    ICurrentTenant tenant, ICurrentCompany company, LegacyRequestTelemetry? telemetry = null)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private const int MaxResponseBytes = 512 * 1024;
    private static readonly Regex License = new(@"\b(?:var|let|const)\s+license\s*=\s*['"" ](?<value>[^'"";<>]{1,100})['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    public bool Configured => tenant.Id is long tenantId && company.Id is long companyId
        && settings.GetStatus(tenantId, companyId).Enabled;

    public async Task<ChinazBatchResult> LookupAsync(ChinazBatchRequest request, CancellationToken ct)
    {
        if (request?.AssetIds is null || request.AssetIds.Count is < 1 or > 10
            || request.AssetIds.Any(id => !long.TryParse(id, out var parsed) || parsed <= 0))
            throw new ArgumentException("每批须选择 1–10 条不同的待审资产。");
        var ids = request.AssetIds.Select(long.Parse).ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("同一资产不能重复查询。");
        if (!Configured)
            return new("chinaz", false, ids.Select(id =>
                new ChinazLookupRow(id.ToString(), "", "not_configured", null, null, "", null,
                    "尚未配置站长之家自动访问授权；未发送网络请求。")).ToList());

        var assets = await db.QuakeAssetRecords.AsNoTracking()
            .Where(asset => ids.Contains(asset.Id))
            .Select(asset => new { asset.Id, asset.Domain, asset.Hostname }).ToListAsync(ct);
        var byId = assets.ToDictionary(asset => asset.Id);
        var results = new List<ChinazLookupRow>();
        foreach (var id in ids)
        {
            if (!Configured)
            {
                results.Add(new(id.ToString(), "", "not_configured", null, null, "", null,
                    "站长之家自动访问已关闭；未发送网络请求。"));
                continue;
            }
            if (!byId.TryGetValue(id, out var asset))
            {
                results.Add(new(id.ToString(), "", "no_asset", null, null, "", null, "当前公司没有该资产。"));
                continue;
            }
            var rawDomain = asset.Domain ?? asset.Hostname ?? "";
            string domain;
            try { domain = PublicWebTarget.Host(rawDomain); }
            catch (ArgumentException)
            {
                results.Add(new(id.ToString(), rawDomain, "invalid_domain", null, null, "", null, "资产没有可查询的公开域名。"));
                continue;
            }
            results.Add(await FetchAsync(id, domain, ct));
        }
        return new("chinaz", Configured, results);
    }

    private async Task<ChinazLookupRow> FetchAsync(long assetId, string domain, CancellationToken ct)
    {
        var url = "https://seo.chinaz.com/" + Uri.EscapeDataString(domain);
        await Gate.WaitAsync(ct);
        var observedResponse = false;
        try
        {
            var wait = _lastRequestUtc.AddSeconds(3) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            // An administrator may turn the connector off while a batch waits
            // for the rate limit. Recheck immediately before sending.
            if (!Configured)
                return new(assetId.ToString(), domain, "not_configured", null, null, "", null,
                    "站长之家自动访问已关闭；未发送网络请求。");
            _lastRequestUtc = DateTime.UtcNow;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("GuliCRM/1.2 (+authorized source connector)");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (telemetry is not null) await telemetry.ObserveAsync(tenant?.Id, company?.Id, "chinaz", "batch_lookup", $"http_{(int)response.StatusCode}", ct);
            observedResponse = true;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new(assetId.ToString(), domain, "rate_limited", null, null, url, DateTime.UtcNow, "来源站点返回 429；本批不重试。 ");
            if (!response.IsSuccessStatusCode)
                return new(assetId.ToString(), domain, "http_error", null, null, url, DateTime.UtcNow,
                    $"来源站点返回 HTTP {(int)response.StatusCode}。");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return new(assetId.ToString(), domain, "too_large", null, null, url, DateTime.UtcNow, "页面超过大小限制。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int n;
            while ((n = await stream.ReadAsync(bytes, ct)) != 0)
            {
                if (buffer.Length + n > MaxResponseBytes)
                    return new(assetId.ToString(), domain, "too_large", null, null, url, DateTime.UtcNow, "页面超过大小限制。");
                buffer.Write(bytes, 0, n);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            var parsed = Parse(domain, encoding.GetString(buffer.ToArray()));
            return new(assetId.ToString(), domain, parsed.Name is null ? "no_subject" : "found",
                parsed.Name, parsed.Icp, url, DateTime.UtcNow,
                parsed.Name is null ? "页面没有可确认的主体归属语句；备案号不能推断公司名。" : "第三方页面候选；仍须人工核对。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!observedResponse && telemetry is not null) await telemetry.ObserveAsync(tenant?.Id, company?.Id, "chinaz", "batch_lookup", "uncertain", CancellationToken.None);
            return new(assetId.ToString(), domain, "timeout", null, null, url, DateTime.UtcNow, "来源请求超时。");
        }
        catch (HttpRequestException)
        {
            if (!observedResponse && telemetry is not null) await telemetry.ObserveAsync(tenant?.Id, company?.Id, "chinaz", "batch_lookup", "uncertain", CancellationToken.None);
            return new(assetId.ToString(), domain, "network_error", null, null, url, DateTime.UtcNow, "来源连接失败。");
        }
        finally { Gate.Release(); }
    }

    public static (string? Name, string? Icp) Parse(string domain, string html)
    {
        var text = WebUtility.HtmlDecode(Tags.Replace(html, " "));
        text = Regex.Replace(text, @"\s+", " ");
        var match = Regex.Match(text, Regex.Escape(domain) + @"\s*是\s*(?<name>.{2,100}?)\s*旗下网站",
            RegexOptions.IgnoreCase);
        var name = match.Success ? match.Groups["name"].Value.Trim() : null;
        if (name is not null && (name.Length > 100 || name.Contains('*') || name.Contains('＊')
            || name.Contains("代理") || name.Contains("经销") || name.Contains("合作伙伴"))) name = null;
        var licence = License.Match(html);
        var icp = licence.Success ? WebUtility.HtmlDecode(licence.Groups["value"].Value.Trim()) : null;
        return (name, icp);
    }
}
