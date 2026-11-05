namespace GuliERP.Crm.Infrastructure.Services;

public sealed record IntelligenceSourceStatus(string Code, string Name, string Mode, string Status,
    bool CanRequestNow, string AvailableFields, string Authorization, string Quota,
    string Cost, string Retention, string Note);

/// <summary>Read-only capability inventory. It never calls external providers.</summary>
public sealed class IntelligenceSourceCatalog(QuakeRuntimeCredentialStore quake, ChinazConnectorSettings chinaz)
{
    public IReadOnlyList<IntelligenceSourceStatus> List(long tenantId, long companyId)
    {
        if (tenantId <= 0 || companyId <= 0) throw new ArgumentException("需要有效的租户和公司范围。");
        var quakeState = quake.GetStatus(tenantId, companyId);
        var chinazState = chinaz.GetStatus(tenantId, companyId);
        var icpToolFound = HasBeianExecutable();
        return [
            new("quake", "Quake 资产搜索", "api", quakeState.Configured ? "connection_configured" : "not_configured",
                quakeState.Configured, "域名、IP、端口和服务观测", "Key 已配置不代表供应商许可已核验",
                "按账户额度；系统仅有同条件冷却，未取得合同额度", "待确认", "待确认",
                "只生成待审资产，不自动确认企业身份"),
            new("chinaz", "站长之家主体线索", "optional_connector", chinazState.Enabled ? "switch_enabled" : "not_configured",
                chinazState.Enabled, "页面公开主体语句、备案号候选", "开关记录授权依据，但系统未独立核验合同范围",
                "每批最多 10 条、请求间隔至少 3 秒；合同额度待确认", "待确认", "待确认",
                "结果仅是第三方候选，仍须人工核对"),
            new("icp_manual", "ICP备案人工补查", "manual_trigger", icpToolFound ? "local_tool_found" : "not_configured", false,
                "备案主体和备案号候选", "本机 beian 工具及来源规则须人工核对", "未配置自动批量额度", "待确认", "待确认",
                icpToolFound ? "已找到本机程序；仍需逐条人工触发，实际查询结果未由此状态证明" : "未找到本机 beian 程序；不会发起查询"),
            new("registry", "企业登记/信用信息", "licensed_api", "not_configured", false,
                "企业名称、信用代码、登记经营范围", "未录入可核验的 API 许可", "待确认", "待确认", "待确认",
                "无许可时不请求；信用代码不得根据名称生成"),
            new("baidu_ads", "百度广告公开迹象", "licensed_or_manual", "not_configured", false,
                "关键词、地域、时间、广告标识", "未录入可核验的许可或人工采集规则", "待确认", "待确认", "待确认",
                "广告迹象不能推断持续投放、预算或账户所有权"),
            new("recruitment", "招聘公开信息", "licensed_or_manual", "not_configured", false,
                "招聘岗位、公开联系线索、发布时间", "未录入可核验的许可或人工采集规则", "待确认", "待确认", "待确认",
                "招聘电话仅作候选，不自动成为销售联系人或自动触达")
        ];
    }

    private static bool HasBeianExecutable()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "beian.exe", "beian.cmd", "beian.bat" } : new[] { "beian" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                if (names.Any(name => File.Exists(Path.Combine(directory.Trim('"'), name)))) return true;
            }
            catch (ArgumentException) { /* Ignore an invalid PATH entry. */ }
        }
        return false;
    }
}
