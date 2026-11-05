using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace GuliERP.Crm.Infrastructure.Services;

/// <summary>
/// ICP 备案查询服务：通过 beian CLI 查询域名对应的备案信息。
/// beian 依赖 playwright 浏览器，每次调用需 10-20 秒。
/// </summary>
public class IcpLookupService
{
    private readonly ILogger<IcpLookupService> _logger;

    // beian CLI 路径
    private const string BeianCli = "beian";
    private readonly string? _playwrightBrowsersPath;
    private readonly bool _enabled;

    public IcpLookupService(ILogger<IcpLookupService> logger, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        _logger = logger;
        _playwrightBrowsersPath = configuration?["Crm:PlaywrightBrowsersPath"];
        _enabled = bool.TryParse(configuration?["Crm:Icp:Enabled"], out var enabled) && enabled;
    }

    /// <summary>
    /// 查询单个域名的 ICP 备案信息。
    /// </summary>
    public async Task<IcpResult?> LookupAsync(string domain, CancellationToken ct = default)
    {
        if (!_enabled)
            return new IcpResult { Domain = domain, Found = false, QueryStatus = "disabled", Error = "ICP 查询尚未启用。" };
        try
        {
            domain = PublicWebTarget.Host(domain);
            await PublicWebTarget.ResolveAsync(domain, ct);
            var psi = new ProcessStartInfo
            {
                FileName = BeianCli,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--website");
            psi.ArgumentList.Add("--raw");
            psi.ArgumentList.Add(domain);
            if (!string.IsNullOrWhiteSpace(_playwrightBrowsersPath))
                psi.Environment["PLAYWRIGHT_BROWSERS_PATH"] = _playwrightBrowsersPath;

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                _logger.LogError("无法启动 beian 进程");
                return null;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(45));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            if (string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogWarning("beian 返回空: {Domain}", domain);
                return null;
            }

            // 提取 JSON 数组部分（beian 可能输出警告前缀）
            var jsonStart = stdout.IndexOf('[');
            if (jsonStart < 0)
            {
                _logger.LogWarning("beian 输出无 JSON: {Domain} stdout: {Out}", domain, stdout[..Math.Min(200, stdout.Length)]);
                return null;
            }

            using var doc = JsonDocument.Parse(stdout[jsonStart..]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;

            var first = root[0];
            if (!first.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array || records.GetArrayLength() == 0)
            {
                return new IcpResult { Domain = domain, Found = false, QueryStatus = "not_found" };
            }

            var rec = records[0];
            return new IcpResult
            {
                Domain = GetStr(rec, "domain") ?? domain,
                Found = true,
                QueryStatus = "found",
                UnitName = GetStr(rec, "unitName"),
                Nature = GetStr(rec, "nature"),
                ServiceLicence = GetStr(rec, "serviceLicence"),
                MainLicence = GetStr(rec, "mainLicence"),
                UpdateDate = GetStr(rec, "updateDate")
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new IcpResult { Domain = domain, Found = false, QueryStatus = "not_configured", Error = "服务端未安装或未配置 beian CLI。" };
        }
        catch (Exception ex)
        {
            _logger.LogError("ICP 查询失败 {Domain}: {ExceptionType}", domain, ex.GetType().Name);
            return new IcpResult { Domain = domain, Found = false, QueryStatus = "error", Error = "ICP 查询失败，请稍后重试。" };
        }
    }

    /// <summary>
    /// 批量查询多个域名（串行，避免同时启动多个浏览器）。
    /// </summary>
    public async Task<List<IcpResult>> LookupBatchAsync(List<string> domains, CancellationToken ct = default)
    {
        var results = new List<IcpResult>();
        foreach (var domain in domains)
        {
            if (ct.IsCancellationRequested) break;
            var r = await LookupAsync(domain, ct);
            results.Add(r ?? new IcpResult { Domain = domain, Found = false });
            // 间隔 2 秒，避免浏览器同时启动
            await Task.Delay(2000, ct);
        }
        return results;
    }

    private static string? GetStr(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }
}

/// <summary>ICP 备案查询结果</summary>
public class IcpResult
{
    public string Domain { get; set; } = "";
    public bool Found { get; set; }
    public string QueryStatus { get; set; } = "error"; // found / not_found / not_configured / error
    public string? UnitName { get; set; }       // 主办单位名称
    public string? Nature { get; set; }         // 单位性质（企业/个人等）
    public string? ServiceLicence { get; set; } // 网站备案号
    public string? MainLicence { get; set; }    // 主备案号
    public string? UpdateDate { get; set; }     // 备案更新时间
    public string? Error { get; set; }
}
