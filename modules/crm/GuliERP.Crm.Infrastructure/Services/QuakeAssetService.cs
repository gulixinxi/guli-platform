using System.Globalization;
using System.Text;
using System.Text.Json;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>Quake 资产的一条记录（两个通道共用同一形状）。</summary>
public sealed class QuakeAssetInput
{
    public string? IpAddress { get; set; }
    public string? Url { get; set; }
    public string? Transport { get; set; }
    public int? Port { get; set; }
    public string? SitePath { get; set; }
    public long? Asn { get; set; }
    public string? AsnOrganization { get; set; }
    public string? Domain { get; set; }
    public string? Hostname { get; set; }
    public string? ServiceProduct { get; set; }
    public string? ServiceName { get; set; }
    public string? ServiceVersion { get; set; }
    public string? CountryCn { get; set; }
    public string? CityCn { get; set; }
    public string? ProvinceCn { get; set; }

    // 清洗证据（手动导入时可选）
    public string? Title { get; set; }
    public string? ServerHeader { get; set; }
    public string? VendorName { get; set; }
    public string? SceneCn { get; set; }
    public string? AsnName { get; set; }
    public string? OsName { get; set; }

    /// <summary>手动导入时的原始行，jsonb 审计用。</summary>
    public string RawJson { get; set; } = "{}";
}

/// <summary>Quake 资产的 15 字段 + 清洗判定。</summary>
public sealed class QuakeAssetDto
{
    public long Id { get; set; }
    // Snowflake IDs exceed JavaScript's safe integer range; use this for actions.
    public string IdText { get; set; } = "";
    public Guid BatchId { get; set; }
    public string? IpAddress { get; set; }
    public string? Url { get; set; }
    public string? Transport { get; set; }
    public int? Port { get; set; }
    public string? SitePath { get; set; }
    public long? Asn { get; set; }
    public string? AsnOrganization { get; set; }
    public string? Domain { get; set; }
    public string? Hostname { get; set; }
    public string? ServiceProduct { get; set; }
    public string? ServiceName { get; set; }
    public string? ServiceVersion { get; set; }
    public string? CountryCn { get; set; }
    public string? CityCn { get; set; }
    public string? ProvinceCn { get; set; }
    public string? Title { get; set; }
    public string Server { get; set; } = "api";
    public string CleanVerdict { get; set; } = "unknown";
    public int CleanScore { get; set; }
    public string? CleanReasons { get; set; }
    public bool Imported { get; set; }
    public long? ImportedLeadId { get; set; }
    public DateTime ObservedAt { get; set; }
}

/// <summary>导入结果摘要：分通道、分清洗判定统计。</summary>
public sealed class QuakeAssetImportResult
{
    public string Source { get; set; } = "manual";
    public Guid BatchId { get; set; }
    public int Received { get; set; }
    public int Stored { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Valid { get; set; }
    public int Suspect { get; set; }
    public int Rejected { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class QuakeAssetPreviewRow
{
    public int Index { get; set; }
    public string? Domain { get; set; }
    public int? Port { get; set; }
    public string CleanVerdict { get; set; } = "unknown";
    public int CleanScore { get; set; }
    public IReadOnlyList<string> CleanReasons { get; set; } = [];
    public bool DuplicateInRequest { get; set; }
    public long? ExistingAssetId { get; set; }
}

public sealed class QuakeAssetPreviewResult
{
    public int Total { get; set; }
    public int New { get; set; }
    public int Existing { get; set; }
    public int DuplicateInRequest { get; set; }
    public List<QuakeAssetPreviewRow> Items { get; set; } = [];
}

/// <summary>
/// Quake 资产服务：<b>API 调用</b>与<b>手动导入</b>双通道，统一落到
/// <see cref="QuakeAssetRecord"/>，并统一执行清洗。
///
/// <para>两个通道写入完全相同的 15 字段 + 证据列，因此下游（转线索、审核、
/// 报表）不需要关心数据来自哪里。</para>
///
/// <para>清洗在<b>入库前</b>执行，但<b>被拒绝的行同样入库</b>（标记 rejected），
/// 这样清洗规则未来调整时可以回溯重评，而不是丢数据。</para>
/// </summary>
public sealed class QuakeAssetService
{
    private const string QuakeApiUrl = "https://quake.360.net/api/v3/search/quake_service";

    private readonly HttpClient _http;
    private readonly QuakeRuntimeCredentialStore _credentials;
    private readonly CrmDbContext _db;
    private readonly QuakeAssetCleaner _cleaner;
    private readonly ILogger<QuakeAssetService> _logger;
    private readonly LegacyRequestTelemetry? _telemetry;

    public QuakeAssetService(
        HttpClient http,
        QuakeRuntimeCredentialStore credentials,
        CrmDbContext db,
        QuakeAssetCleaner cleaner,
        ILogger<QuakeAssetService> logger,
        LegacyRequestTelemetry? telemetry = null)
    {
        _http = http;
        _credentials = credentials;
        _db = db;
        _cleaner = cleaner;
        _logger = logger;
        _telemetry = telemetry;
    }

    // ================= 通道 1：Quake API 调用 =================

    /// <summary>
    /// 调用 Quake 实时搜索，把每条结果映射成 15 字段、清洗后入库。
    /// </summary>
    public async Task<QuakeAssetImportResult> FetchAsync(
        string query, long? tenantId, long? companyId, int size = 20, CancellationToken ct = default)
    {
        var result = new QuakeAssetImportResult { Source = "api", BatchId = Guid.NewGuid() };

        var candidates = _credentials.GetCandidates(tenantId, companyId);

        var jsonBody = JsonSerializer.Serialize(new
        {
            query,
            start = 0,
            size,
            ignore_cache = false,
        });

        var raw = await QuakeRequestSender.SendAsync(_http, QuakeApiUrl, jsonBody, candidates, ct,
            outcome => _telemetry?.ObserveAsync(tenantId, companyId, "quake", "asset_fetch", outcome, ct) ?? Task.CompletedTask);
        _logger.LogInformation("Quake fetch response length {Len} bytes", raw.Length);

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var code = Str(root, "code");
        if (code is not null && code != "0")
            throw new InvalidOperationException($"Quake API business error: {Str(root, "message") ?? code}");

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        var items = new List<QuakeAssetInput>();
        foreach (var el in data.EnumerateArray())
        {
            result.Received++;
            items.Add(MapFromQuake(el));
        }

        await StoreAsync(items, result, "api", tenantId, companyId, ct);
        return result;
    }

    /// <summary>
    /// 把一条 Quake 返回映射成 15 字段。
    /// 注意 Quake 没有单一 URL 字段，需要用 scheme + http.host + http.path 合成。
    /// </summary>
    public static QuakeAssetInput MapFromQuake(JsonElement el)
    {
        var service = Obj(el, "service");
        var http = Obj(service, "http");
        var location = Obj(el, "location");

        var serviceName = Str(service, "name");
        var scheme = serviceName is not null && serviceName.Contains("ssl", StringComparison.OrdinalIgnoreCase)
            ? "https" : "http";
        var host = Str(http, "host");
        var domain = Str(el, "domain");
        var siteHost = string.IsNullOrWhiteSpace(host) ? domain : host;
        var path = Str(http, "path");
        if (string.IsNullOrWhiteSpace(path)) path = "/";

        // 服务产品 / 厂商来自 components[0]
        string? productCn = null, vendor = null;
        if (el.TryGetProperty("components", out var comps)
            && comps.ValueKind == JsonValueKind.Array
            && comps.GetArrayLength() > 0)
        {
            var first = comps[0];
            productCn = Str(first, "product_name_cn");
            vendor = Str(first, "product_vendor");
        }

        return new QuakeAssetInput
        {
            IpAddress = Str(el, "ip"),
            Url = $"{scheme}://{siteHost}{path}",
            Transport = Str(el, "transport"),
            Port = Int(el, "port"),
            SitePath = path,
            Asn = Long(el, "asn"),
            AsnOrganization = Str(el, "org"),
            Domain = domain,
            Hostname = siteHost,
            ServiceProduct = productCn,
            ServiceName = serviceName,
            ServiceVersion = Str(service, "version"),
            CountryCn = Str(location, "country_cn"),
            CityCn = Str(location, "city_cn"),
            ProvinceCn = Str(location, "province_cn"),

            Title = Str(http, "title"),
            ServerHeader = Str(http, "server"),
            VendorName = vendor,
            SceneCn = Str(location, "scene_cn"),
            AsnName = Str(location, "asname"),
            OsName = Str(el, "os_name"),
            RawJson = el.GetRawText(),
        };
    }

    // ================= 通道 2：手动导入 =================

    /// <summary>
    /// 手动导入：接受与 API 通道完全相同的 15 字段结构。
    /// </summary>
    public async Task<QuakeAssetImportResult> ImportAsync(
        List<QuakeAssetInput> items, long? tenantId, long? companyId, CancellationToken ct = default, Guid? batchId = null)
    {
        var result = new QuakeAssetImportResult { Source = "manual", BatchId = batchId ?? Guid.NewGuid(), Received = items.Count };
        await StoreAsync(items, result, "manual", tenantId, companyId, ct);
        return result;
    }

    /// <summary>Read-only preview. It does not reserve a row or guarantee that a concurrent import cannot win.</summary>
    public async Task<QuakeAssetPreviewResult> PreviewAsync(
        IReadOnlyList<QuakeAssetInput> items, CancellationToken ct = default)
    {
        var keys = items.Select(NormalizeDomainKey).Where(k => k.Length > 0).Distinct().ToArray();
        var existing = keys.Length == 0
            ? []
            : await _db.QuakeAssetRecords.AsNoTracking()
                .Where(r => r.Domain != null && keys.Contains(r.Domain.ToLower()))
                .Select(r => new { r.Id, r.Domain, r.Port }).ToListAsync(ct);
        var result = new QuakeAssetPreviewResult { Total = items.Count };
        var seen = new HashSet<(string Domain, int? Port)>();
        for (var i = 0; i < items.Count; i++)
        {
            var input = items[i];
            var domain = NormalizeDomainKey(input);
            var clean = _cleaner.Evaluate(new QuakeAssetCandidate
            {
                IpAddress = input.IpAddress,
                Domain = input.Domain,
                Hostname = input.Hostname,
                Title = input.Title,
                ServerHeader = input.ServerHeader,
                CountryCn = input.CountryCn,
                ProvinceCn = input.ProvinceCn,
                CityCn = input.CityCn,
                Port = input.Port,
            });
            var duplicate = !seen.Add((domain, input.Port));
            var prior = existing.FirstOrDefault(r =>
                string.Equals(r.Domain, domain, StringComparison.OrdinalIgnoreCase) && r.Port == input.Port);
            var row = new QuakeAssetPreviewRow
            {
                Index = i,
                Domain = domain.Length == 0 ? null : domain,
                Port = input.Port,
                CleanVerdict = clean.Verdict.ToString().ToLowerInvariant(),
                CleanScore = clean.Score,
                CleanReasons = clean.Reasons,
                DuplicateInRequest = duplicate,
                ExistingAssetId = prior?.Id,
            };
            result.Items.Add(row);
            if (duplicate) result.DuplicateInRequest++;
            else if (prior is not null) result.Existing++;
            else result.New++;
        }
        return result;
    }

    private static string NormalizeDomainKey(QuakeAssetInput input) =>
        (input.Domain ?? input.Hostname ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>
    /// 解析 Quake 导出的 CSV。表头支持中文列名（与用户导出表一致），
    /// 也接受英文列名，忽略大小写与空白。
    /// </summary>
    public static List<QuakeAssetInput> ParseCsv(string csv)
    {
        var rows = ParseCsvRows(csv);
        if (rows.Count == 0) return new List<QuakeAssetInput>();

        var header = rows[0];
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
            index[NormalizeHeader(header[i])] = i;

        var items = new List<QuakeAssetInput>();
        foreach (var row in rows.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            string Get(params string[] names)
            {
                foreach (var n in names)
                    if (index.TryGetValue(NormalizeHeader(n), out var i) && i < row.Count)
                        return row[i].Trim();
                return string.Empty;
            }

            var input = new QuakeAssetInput
            {
                IpAddress = Null(Get("IP地址", "ip", "ipaddress")),
                Url = Null(Get("URL", "url")),
                Transport = Null(Get("传输层协议", "transport", "protocol")),
                Port = ParseInt(Get("端口号", "port")),
                SitePath = Null(Get("网站路径", "path", "sitepath")),
                Asn = ParseLong(Get("自治域号码", "asn")),
                AsnOrganization = Null(Get("自治域归属组织", "org", "asnorg")),
                Domain = Null(Get("国际化域名", "domain")),
                Hostname = Null(Get("网站Host（域名）", "网站Host", "hostname", "host")),
                ServiceProduct = Null(Get("服务产品", "product")),
                ServiceName = Null(Get("服务名称", "service", "servicename")),
                ServiceVersion = Null(Get("服务版本号", "version", "serviceversion")),
                CountryCn = Null(Get("国家（中文）", "国家", "country")),
                CityCn = Null(Get("城市（中文）", "城市", "city")),
                ProvinceCn = Null(Get("省份（中文）", "省份", "province")),

                // 证据列：仅当导出里带下划线/英文证据列时才填
                Title = Null(Get("_title", "title")),
                ServerHeader = Null(Get("_server", "server")),
                VendorName = Null(Get("_vendor", "vendor")),
                SceneCn = Null(Get("_scene", "scene")),
                AsnName = Null(Get("_asname", "asname")),
                OsName = Null(Get("_os", "os", "osname")),
            };
            input.RawJson = JsonSerializer.Serialize(input);
            items.Add(input);
        }
        return items;
    }

    private static string NormalizeHeader(string h)
    {
        // 去掉所有空白与全角括号差异，便于 "网站Host（域名）" / "网站Host" 两种写法
        var s = (h ?? string.Empty).Trim();
        s = s.Replace("（", "(").Replace("）", ")").Replace(" ", "").Replace("\uFEFF", "");
        return s;
    }

    private static List<List<string>> ParseCsvRows(string csv)
    {
        var rows = new List<List<string>>();
        var cur = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (var i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < csv.Length && csv[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else
            {
                switch (ch)
                {
                    case '"': inQuotes = true; break;
                    case ',': cur.Add(field.ToString()); field.Clear(); break;
                    case '\r': break;
                    case '\n':
                        cur.Add(field.ToString()); field.Clear();
                        rows.Add(cur); cur = new List<string>();
                        break;
                    default: field.Append(ch); break;
                }
            }
        }
        if (field.Length > 0 || cur.Count > 0)
        {
            cur.Add(field.ToString());
            rows.Add(cur);
        }
        return rows;
    }

    // ================= 共用：清洗 + 入库 =================

    private async Task StoreAsync(
        List<QuakeAssetInput> items,
        QuakeAssetImportResult result,
        string source,
        long? tenantId,
        long? companyId,
        CancellationToken ct)
    {
        var rowIndex = 0;
        foreach (var input in items)
        {
            rowIndex++;
            try
            {
                var clean = _cleaner.Evaluate(new QuakeAssetCandidate
                {
                    IpAddress = input.IpAddress,
                    Domain = input.Domain,
                    Hostname = input.Hostname,
                    Title = input.Title,
                    ServerHeader = input.ServerHeader,
                    CountryCn = input.CountryCn,
                    ProvinceCn = input.ProvinceCn,
                    CityCn = input.CityCn,
                    Port = input.Port,
                });

                switch (clean.Verdict)
                {
                    case QuakeCleanVerdict.Valid: result.Valid++; break;
                    case QuakeCleanVerdict.Suspect: result.Suspect++; break;
                    default: result.Rejected++; break;
                }

                var keyDomain = NormalizeDomainKey(input);
                var existing = await _db.QuakeAssetRecords.FirstOrDefaultAsync(
                    r => r.Domain != null && r.Domain.ToLower() == keyDomain && r.Port == input.Port, ct);

                // The current unique index represents one asset per company/domain/port.
                // Preserve its first observation until a versioned observation table exists.
                if (existing is not null)
                {
                    result.Skipped++;
                    continue;
                }

                var record = new QuakeAssetRecord
                {
                    Id = SnowflakeId(),
                    BatchId = result.BatchId,
                    ObservedAt = DateTime.UtcNow,
                };

                record.IpAddress = input.IpAddress;
                record.Url = input.Url;
                record.Transport = input.Transport;
                record.Port = input.Port;
                record.SitePath = input.SitePath;
                record.Asn = input.Asn;
                record.AsnOrganization = input.AsnOrganization;
                record.Domain = keyDomain.Length == 0 ? null : keyDomain;
                record.Hostname = input.Hostname;
                record.ServiceProduct = input.ServiceProduct;
                record.ServiceName = input.ServiceName;
                record.ServiceVersion = input.ServiceVersion;
                record.CountryCn = input.CountryCn;
                record.CityCn = input.CityCn;
                record.ProvinceCn = input.ProvinceCn;
                record.Title = input.Title;
                record.ServerHeader = input.ServerHeader;
                record.VendorName = input.VendorName;
                record.SceneCn = input.SceneCn;
                record.AsnName = input.AsnName;
                record.OsName = input.OsName;
                record.Source = source;
                record.CleanVerdict = clean.Verdict.ToString().ToLowerInvariant();
                record.CleanScore = clean.Score;
                record.CleanReasons = string.Join(",", clean.Reasons).Truncate(1000);
                record.RawJson = input.RawJson;

                _db.QuakeAssetRecords.Add(record);

                await _db.SaveChangesAsync(ct);
                result.Stored++;
            }
            catch (Exception ex)
            {
                // 失败必须留痕，不能静默吞掉。
                result.Failed++;
                if (result.Errors.Count < 20)
                    result.Errors.Add($"第 {rowIndex} 条资产保存失败，请稍后重试。");
                _logger.LogWarning("Quake 资产入库失败: {ExceptionType}", ex.GetType().Name);
            }
        }
    }

    // ================= 查询 =================

    public async Task<List<QuakeAssetDto>> ListAsync(
        string? verdict, Guid? batchId, int take, CancellationToken ct = default)
        => (await SearchAsync(null, verdict, batchId, 0, take, ct)).Items;

    public async Task<(int Total, List<QuakeAssetDto> Items)> SearchAsync(
        string? keyword, string? verdict, Guid? batchId, int skip, int take, CancellationToken ct = default)
    {
        var q = _db.QuakeAssetRecords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(verdict)) q = q.Where(r => r.CleanVerdict == verdict);
        if (batchId.HasValue) q = q.Where(r => r.BatchId == batchId.Value);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var key = keyword.Trim().ToLowerInvariant();
            q = q.Where(r => (r.Domain != null && r.Domain.ToLower().Contains(key))
                || (r.Hostname != null && r.Hostname.ToLower().Contains(key))
                || (r.IpAddress != null && r.IpAddress.Contains(key))
                || (r.Title != null && r.Title.ToLower().Contains(key)));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(r => r.Id).Skip(Math.Clamp(skip, 0, 1000000)).Take(Math.Clamp(take, 1, 500))
            .Select(r => new QuakeAssetDto
            {
                Id = r.Id, IdText = r.Id.ToString(), BatchId = r.BatchId, IpAddress = r.IpAddress, Url = r.Url,
                Transport = r.Transport, Port = r.Port, SitePath = r.SitePath,
                Asn = r.Asn, AsnOrganization = r.AsnOrganization, Domain = r.Domain,
                Hostname = r.Hostname, ServiceProduct = r.ServiceProduct,
                ServiceName = r.ServiceName, ServiceVersion = r.ServiceVersion,
                CountryCn = r.CountryCn, CityCn = r.CityCn, ProvinceCn = r.ProvinceCn,
                Title = r.Title, Server = r.Source, CleanVerdict = r.CleanVerdict,
                CleanScore = r.CleanScore, CleanReasons = r.CleanReasons,
                Imported = r.Imported, ImportedLeadId = r.ImportedLeadId,
                ObservedAt = r.ObservedAt,
            }).ToListAsync(ct);
        return (total, items);
    }

    public Task<QuakeAssetRecord?> GetAsync(long id, CancellationToken ct = default)
        => _db.QuakeAssetRecords.FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>标记资产已转线索，并回填线索 id。</summary>
    public async Task MarkImportedAsync(long id, long leadId, CancellationToken ct = default)
    {
        var record = await _db.QuakeAssetRecords.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (record is null) return;
        record.Imported = true;
        record.ImportedLeadId = leadId;
        await _db.SaveChangesAsync(ct);
    }

    private static long SnowflakeId()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (ts << 18) | (Random.Shared.NextInt64(0, 1 << 17) & ((1 << 17) - 1)) + (1L << 17);
    }

    private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
    private static int? ParseInt(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    private static long? ParseLong(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static JsonElement? Obj(JsonElement? el, string name)
    {
        if (el is not JsonElement e || e.ValueKind != JsonValueKind.Object) return null;
        return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    }

    private static string? Str(JsonElement? el, string name)
    {
        if (el is not JsonElement e || e.ValueKind != JsonValueKind.Object) return null;
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrEmpty(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    private static int? Int(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static long? Long(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}

file static class StrExt
{
    /// <summary>安全截断，避免超出列宽。</summary>
    public static string Truncate(this string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max];
}
