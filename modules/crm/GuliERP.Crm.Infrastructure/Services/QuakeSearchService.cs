using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Foundation.Kernel;
using Microsoft.Extensions.Logging;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>
/// Quake 360 网络空间测绘搜索服务。
/// 调用 Quake API 搜索目标站点，返回域名、IP、标题、位置等线索候选。
/// </summary>
public class QuakeSearchService
{
    private readonly HttpClient _http;
    private readonly QuakeRuntimeCredentialStore _credentials;
    private readonly ICurrentTenant _tenant;
    private readonly ICurrentCompany _company;
    private readonly ILogger<QuakeSearchService> _logger;
    private readonly LegacyRequestTelemetry? _telemetry;
    private const string QuakeApiUrl = "https://quake.360.net/api/v3/search/quake_service";

    public QuakeSearchService(HttpClient http, QuakeRuntimeCredentialStore credentials,
        ICurrentTenant tenant, ICurrentCompany company, ILogger<QuakeSearchService> logger,
        LegacyRequestTelemetry? telemetry = null)
    {
        _http = http;
        _credentials = credentials;
        _tenant = tenant;
        _company = company;
        _logger = logger;
        _telemetry = telemetry;
    }

    /// <summary>
    /// 搜索 Quake，返回简化后的线索候选列表。
    /// </summary>
    public async Task<List<QuakeSiteResult>> SearchAsync(string query, int size = 20, CancellationToken ct = default)
    {
        var candidates = _credentials.GetCandidates(_tenant.Id, _company.Id);

        // 手动构造 JSON，避免 JsonContent 自动加 charset 导致 Quake 报错
        var jsonBody = System.Text.Json.JsonSerializer.Serialize(new
        {
            query,
            start = 0,
            size,
            ignore_cache = false
        });

        try
        {
            var raw = await QuakeRequestSender.SendAsync(_http, QuakeApiUrl, jsonBody, candidates, ct,
                outcome => _telemetry?.ObserveAsync(_tenant.Id, _company.Id, "quake", "search", outcome, ct) ?? Task.CompletedTask);
            _logger.LogInformation("Quake API response length {Len}", raw.Length);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var code = GetInt(root, "code");
            if (code.HasValue && code.Value != 0)
            {
                throw new InvalidOperationException($"Quake API business error code {code.Value}.");
            }

            var results = new List<QuakeSiteResult>();
            if (!root.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var item in dataArr.EnumerateArray())
            {
                var domain = GetString(item, "domain") ?? GetString(item, "hostname") ?? GetString(item, "ip") ?? "";
                if (string.IsNullOrWhiteSpace(domain)) continue;

                string? title = null;
                if (item.TryGetProperty("service", out var svc) && svc.ValueKind == JsonValueKind.Object
                    && svc.TryGetProperty("http", out var http) && http.ValueKind == JsonValueKind.Object)
                {
                    title = GetString(http, "title");
                }

                string? city = null;
                string? province = null;
                string? isp = null;
                if (item.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
                {
                    city = GetString(loc, "city_cn");
                    province = GetString(loc, "province_cn");
                    isp = GetString(loc, "isp");
                }

                results.Add(new QuakeSiteResult
                {
                    Domain = domain,
                    Ip = GetString(item, "ip"),
                    Port = GetInt(item, "port"),
                    Title = title,
                    Service = GetString(item, "service_name"),
                    City = city,
                    Province = province,
                    Isp = isp,
                    Os = GetString(item, "os")
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError("Quake 搜索异常 {ExceptionType}", ex.GetType().Name);
            throw;
        }
    }

    private static string? GetString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static int? GetInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
        return null;
    }
}

/// <summary>Quake 搜索结果（简化后的线索候选）</summary>
public class QuakeSiteResult
{
    public string Domain { get; set; } = "";
    public string? Ip { get; set; }
    public int? Port { get; set; }
    public string? Title { get; set; }
    public string? Service { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? Isp { get; set; }
    public string? Os { get; set; }
}
