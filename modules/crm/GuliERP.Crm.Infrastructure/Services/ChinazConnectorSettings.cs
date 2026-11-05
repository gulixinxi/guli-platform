using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record ChinazSettingsInput(bool Enabled, string? AuthorizationReference);
public sealed record ChinazSettingsStatus(bool Enabled, bool AuthorizationConfigured, string AuthorizationReference);

/// <summary>Server-local connector switch. Saved separately from deployed appsettings.</summary>
public sealed class ChinazConnectorSettings(IConfiguration configuration)
{
    private readonly object _gate = new();
    private readonly string _path = configuration["Crm:Chinaz:SettingsPath"] ?? DefaultPath(configuration);

    private static string DefaultPath(IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("GuliERP")
            ?? configuration["GULIERP_ConnectionStrings:GuliERP"];
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("未指定数据库，不能确定站长之家开关的环境归属。");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        if (string.IsNullOrWhiteSpace(builder.Host) || string.IsNullOrWhiteSpace(builder.Database))
            throw new InvalidOperationException("数据库连接缺少主机或库名，不能确定站长之家开关的环境归属。");
        var identity = $"{builder.Host.ToLowerInvariant()}|{builder.Port}|{builder.Database.ToLowerInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GuliERP", $"chinaz-connector-{hash}.json");
    }

    public ChinazSettingsStatus GetStatus(long tenantId, long companyId)
    {
        lock (_gate) return Read(ScopedPath(tenantId, companyId));
    }

    public ChinazSettingsStatus Update(long tenantId, long companyId, ChinazSettingsInput input)
    {
        var path = ScopedPath(tenantId, companyId);
        var reference = input.AuthorizationReference?.Trim() ?? "";
        if (reference.Length > 500 || reference.Any(char.IsControl))
            throw new ArgumentException("授权依据不能超过 500 字，也不能包含控制字符。");
        if (input.Enabled && reference.Length == 0)
            throw new ArgumentException("开启自动查询前须填写可核对的授权依据。");
        var next = new ChinazSettingsStatus(input.Enabled, reference.Length > 0, reference);
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(next));
                File.Move(temp, path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return next;
        }
    }

    private string ScopedPath(long tenantId, long companyId)
    {
        if (tenantId <= 0 || companyId <= 0)
            throw new ArgumentException("缺少租户或公司范围。");
        return Path.Combine(Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}-{tenantId}-{companyId}.json");
    }

    private static ChinazSettingsStatus Read(string path)
    {
        // The former database-wide file and configuration flags are deliberately
        // ignored. They cannot prove authorization for an individual company.
        if (!File.Exists(path)) return new(false, false, "");
        var saved = JsonSerializer.Deserialize<ChinazSettingsStatus>(File.ReadAllText(path))
            ?? throw new InvalidDataException("站长之家开关配置无效。");
        return saved with { Enabled = saved.Enabled && saved.AuthorizationConfigured && saved.AuthorizationReference.Length > 0 };
    }
}
