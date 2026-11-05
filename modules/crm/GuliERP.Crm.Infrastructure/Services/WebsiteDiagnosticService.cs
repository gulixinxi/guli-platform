using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Domain.Enums;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record DiagnosticCheck(string Key, string Status, string Evidence, DateTime CheckedAt);
public sealed record DiagnosticAttempt(int Number, DateTime At, string Outcome);
public sealed record DiagnosticChange(string Key, string PreviousStatus, string CurrentStatus);
public sealed record WebsiteDiagnosticRequest(string Url);

/// <summary>Queues versioned technical checks for an explicitly confirmed identity.</summary>
public sealed class WebsiteDiagnosticService(CrmDbContext db)
{
    public async Task<object> RequestAsync(long candidateId, string url, long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("操作人员身份无效。");
        var candidate = await db.IdentityCandidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == candidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该身份候选。");
        if (candidate.Status != "confirmed") throw new InvalidOperationException("只有已确认身份的候选可请求网站诊断。");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || target.Scheme != Uri.UriSchemeHttps || target.AbsolutePath != "/"
            || !string.IsNullOrEmpty(target.Query) || !string.IsNullOrEmpty(target.Fragment)
            || PublicWebTarget.Host(url) != candidate.Domain)
            throw new ArgumentException("请使用已确认域名的 HTTPS 网站首页，不允许路径、参数或其他域名。");

        var latest = await db.WebsiteDiagnosticReports.AsNoTracking()
            .Where(r => r.CandidateId == candidateId).OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
        if (latest is not null && latest.RequestedAt > DateTime.UtcNow.AddHours(-1))
            return Project(latest, cooledDown: true);

        var report = new WebsiteDiagnosticReport
        {
            Id = NewId(), CandidateId = candidateId, Version = (latest?.Version ?? 0) + 1,
            Url = target.GetLeftPart(UriPartial.Path), RequestedBy = actorId,
            RequestedAt = DateTime.UtcNow, Status = "queued"
        };
        db.WebsiteDiagnosticReports.Add(report);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) { throw new InvalidOperationException("诊断请求已由其他人员提交，请刷新报告列表。", ex); }
        return Project(report, cooledDown: false);
    }

    public async Task<object> ListAsync(long candidateId, CancellationToken ct)
    {
        if (!await db.IdentityCandidates.AsNoTracking().AnyAsync(c => c.Id == candidateId, ct))
            throw new KeyNotFoundException("当前公司找不到该身份候选。");
        var reports = await db.WebsiteDiagnosticReports.AsNoTracking()
            .Where(r => r.CandidateId == candidateId).OrderByDescending(r => r.Version).Take(30).ToListAsync(ct);
        var items = reports.Select(r =>
        {
            var previous = r.Status == "completed"
                ? reports.FirstOrDefault(p => p.Version < r.Version && p.Status == "completed"
                    && p.MethodVersion == r.MethodVersion)
                : null;
            return Project(r, false, previous);
        }).ToList();
        return new { items };
    }

    public async Task<object> GetAsync(long candidateId, long reportId, CancellationToken ct)
    {
        if (!await db.IdentityCandidates.AsNoTracking().AnyAsync(c => c.Id == candidateId, ct))
            throw new KeyNotFoundException("当前公司找不到该身份候选。");
        var report = await db.WebsiteDiagnosticReports.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CandidateId == candidateId, ct)
            ?? throw new KeyNotFoundException("当前公司找不到该网站报告。");
        var previous = report.Status == "completed"
            ? await db.WebsiteDiagnosticReports.AsNoTracking()
                .Where(r => r.CandidateId == candidateId && r.Version < report.Version
                    && r.Status == "completed" && r.MethodVersion == report.MethodVersion)
                .OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct)
            : null;
        return Project(report, false, previous);
    }

    internal static List<DiagnosticChange> CompareChecks(WebsiteDiagnosticReport current, WebsiteDiagnosticReport? previous)
    {
        if (previous is null || current.Status != "completed" || previous.Status != "completed"
            || current.MethodVersion != previous.MethodVersion) return [];
        var before = (JsonSerializer.Deserialize<List<DiagnosticCheck>>(previous.ChecksJson) ?? [])
            .ToDictionary(check => check.Key, check => check.Status);
        return (JsonSerializer.Deserialize<List<DiagnosticCheck>>(current.ChecksJson) ?? [])
            .Where(check => before.TryGetValue(check.Key, out var status) && status != check.Status)
            .Select(check => new DiagnosticChange(check.Key, before[check.Key], check.Status)).ToList();
    }

    public static object Project(WebsiteDiagnosticReport r, bool cooledDown, WebsiteDiagnosticReport? previous = null)
    {
        List<DiagnosticAttempt> attempts;
        string? attemptsDataWarning = null;
        try { attempts = JsonSerializer.Deserialize<List<DiagnosticAttempt>>(r.AttemptLogJson) ?? []; }
        catch (JsonException)
        {
            attempts = [];
            attemptsDataWarning = "尝试记录格式异常；报告结论仍需单独核对。";
        }
        // Older reports may have stored raw exception text. Sanitize on read as
        // well so the API never republishes those historical values.
        var publicAttempts = attempts.Select(a => a with
        {
            Outcome = a.Outcome == "completed" ? "completed" : "网站检测未完成或已重试。"
        }).ToList();
        var publicChecks = (JsonSerializer.Deserialize<List<DiagnosticCheck>>(r.ChecksJson) ?? [])
            .Select(c => c.Status == "error" ? c with { Evidence = "网站检测未完成，请人工核对。" } : c)
            .ToList();
        return new
        {
        id = r.Id.ToString(), candidateId = r.CandidateId.ToString(), r.Version, r.Url,
        r.MethodVersion, r.Status, r.Trigger, r.RequestedAt, requestedBy = r.RequestedBy.ToString(),
        r.StartedAt, r.CompletedAt, r.NextAttemptAt, r.AttemptCount,
        Error = r.Error is null ? null : "网站检测未完成，请查看检查项或稍后重试。", cooledDown,
        checks = publicChecks,
        attempts = publicAttempts, attemptsDataWarning,
        comparedWithVersion = previous?.Version,
        changes = CompareChecks(r, previous)
        };
    }

    private static long NewId() => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20);
}

/// <summary>Small durable worker. Claims one report at a time; interrupted running jobs are retried after lease expiry.</summary>
public sealed class WebsiteDiagnosticWorker(IServiceScopeFactory scopes, ILogger<WebsiteDiagnosticWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Website diagnostic worker cycle failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        var now = DateTime.UtcNow;
        var job = await db.WebsiteDiagnosticReports.IgnoreQueryFilters().AsNoTracking()
            .Where(r => (r.Status == "queued" && (r.NextAttemptAt == null || r.NextAttemptAt <= now))
                || (r.Status == "running" && r.NextAttemptAt <= now))
            .OrderBy(r => r.RequestedAt).FirstOrDefaultAsync(ct);
        if (job is null) return;
        var claimed = await db.WebsiteDiagnosticReports.IgnoreQueryFilters()
            .Where(r => r.Id == job.Id && r.Status == job.Status && r.AttemptCount == job.AttemptCount)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, "running")
                .SetProperty(r => r.AttemptCount, r => r.AttemptCount + 1)
                .SetProperty(r => r.StartedAt, now)
                .SetProperty(r => r.NextAttemptAt, now.AddMinutes(3)), ct);
        if (claimed != 1) return;
        var report = await db.WebsiteDiagnosticReports.IgnoreQueryFilters().SingleAsync(r => r.Id == job.Id, ct);
        var identityStillConfirmed = await db.IdentityCandidates.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(c => c.Id == report.CandidateId && c.TenantId == report.TenantId
                && c.CompanyId == report.CompanyId && c.Status == "confirmed", ct);
        if (!identityStillConfirmed)
        {
            report.Status = "failed";
            report.Error = "身份审核已撤销，未访问网站。";
            report.ChecksJson = JsonSerializer.Serialize(new[] { new DiagnosticCheck("reachability", "not_tested", report.Error, DateTime.UtcNow) });
            report.CompletedAt = DateTime.UtcNow;
            report.NextAttemptAt = null;
            AppendAttempt(report, report.Error);
            await db.SaveChangesAsync(ct);
            return;
        }
        try
        {
            var checker = scope.ServiceProvider.GetRequiredService<WebsiteCheckService>();
            var checks = await BuildChecksAsync(report.Url, checker, ct);
            report.ChecksJson = JsonSerializer.Serialize(checks);
            report.Status = "completed";
            report.CompletedAt = DateTime.UtcNow;
            report.NextAttemptAt = null;
            report.Error = null;
            AppendAttempt(report, "completed");
            var previous = await db.WebsiteDiagnosticReports.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.CandidateId == report.CandidateId && r.TenantId == report.TenantId
                    && r.CompanyId == report.CompanyId && r.Version < report.Version
                    && r.Status == "completed" && r.MethodVersion == report.MethodVersion)
                .OrderByDescending(r => r.Version).FirstOrDefaultAsync(ct);
            var candidate = await db.IdentityCandidates.IgnoreQueryFilters().AsNoTracking()
                .FirstAsync(c => c.Id == report.CandidateId && c.TenantId == report.TenantId
                    && c.CompanyId == report.CompanyId, ct);
            var signal = CreateChangeSignal(report, previous, candidate);
            if (signal is not null && !await db.Signals.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(s => s.TenantId == report.TenantId && s.CompanyId == report.CompanyId
                    && s.Source == "website-diagnostic" && s.QueryParams == signal.QueryParams, ct))
                db.Signals.Add(signal);
            var contentReminder = await WebsiteContentSignalService.BuildScheduledReviewReminderAsync(
                db, report, candidate, ct);
            if (contentReminder is not null) db.Signals.Add(contentReminder);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A failed report attempt must not publish reminders created earlier in this attempt.
            foreach (var pending in db.ChangeTracker.Entries<Signal>()
                .Where(entry => entry.State == EntityState.Added).ToList())
                pending.State = EntityState.Detached;
            // Reports and attempt logs are returned to CRM users. Exception text
            // can contain request URLs, connection details or upstream data.
            var message = PublicFailureMessage(ex);
            report.Error = message;
            AppendAttempt(report, message);
            report.Status = report.AttemptCount >= 3 ? "failed" : "queued";
            report.NextAttemptAt = report.Status == "queued" ? DateTime.UtcNow.AddMinutes(1 << report.AttemptCount) : null;
            if (report.Status == "failed")
            {
                report.CompletedAt = DateTime.UtcNow;
                report.ChecksJson = JsonSerializer.Serialize(new[]
                {
                    new DiagnosticCheck("reachability", "error", message, DateTime.UtcNow),
                    new DiagnosticCheck("https", "not_tested", "网站检测未完成。", DateTime.UtcNow),
                    new DiagnosticCheck("robots", "not_tested", "网站检测未完成。", DateTime.UtcNow),
                    new DiagnosticCheck("sitemap", "not_tested", "网站检测未完成。", DateTime.UtcNow),
                    new DiagnosticCheck("canonical", "not_tested", "网站检测未完成。", DateTime.UtcNow),
                    new DiagnosticCheck("schema", "not_tested", "网站检测未完成。", DateTime.UtcNow),
                    new DiagnosticCheck("quotable_text", "not_tested", "网站检测未完成。", DateTime.UtcNow)
                });
            }
            logger.LogWarning("Website diagnostic job {ReportId} attempt {Attempt} failed ({ExceptionType})",
                report.Id, report.AttemptCount, ex.GetType().Name);
        }
        await db.SaveChangesAsync(ct);
    }

    private static void AppendAttempt(WebsiteDiagnosticReport report, string outcome)
    {
        var attempts = JsonSerializer.Deserialize<List<DiagnosticAttempt>>(report.AttemptLogJson) ?? [];
        attempts.Add(new DiagnosticAttempt(report.AttemptCount, DateTime.UtcNow, outcome));
        report.AttemptLogJson = JsonSerializer.Serialize(attempts);
    }

    public static string PublicFailureMessage(Exception exception) => exception switch
    {
        HttpRequestException or TimeoutException or TaskCanceledException => "网站暂时无法完成检测，系统将按重试规则处理。",
        _ => "网站检测未完成，请稍后查看或联系管理员。"
    };

    public static Signal? CreateChangeSignal(WebsiteDiagnosticReport report,
        WebsiteDiagnosticReport? previous, IdentityCandidate candidate)
    {
        var changes = WebsiteDiagnosticService.CompareChecks(report, previous);
        if (changes.Count == 0) return null;
        var now = DateTimeOffset.UtcNow;
        return new Signal
        {
            Id = (now.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
            TenantId = report.TenantId, CompanyId = report.CompanyId,
            CompanyName = candidate.ProposedName.Length > 200 ? candidate.ProposedName[..200] : candidate.ProposedName,
            Type = SignalType.Other,
            Title = "网站技术检查项发生变化", Detail = $"{changes.Count} 项技术状态变化；请核对报告后判断业务意义。",
            Source = "website-diagnostic", SourceUrl = report.Url,
            EventAt = now, CollectedAt = now, CreatedAt = now,
            Confidence = 70, Status = SignalStatus.Pending,
            EvidenceType = "website_diagnostic", QueryParams = $"report_id={report.Id}",
            RawContent = JsonSerializer.Serialize(new { reportId = report.Id.ToString(), report.Version,
                previousVersion = previous?.Version, report.MethodVersion, changes })
        };
    }

    public static async Task<List<DiagnosticCheck>> BuildChecksAsync(string url, WebsiteCheckService checker, CancellationToken ct)
    {
        var domain = PublicWebTarget.Host(url);
        var checks = new List<DiagnosticCheck>();
        void Add(string key, string status, string evidence) => checks.Add(new(key, status, evidence, DateTime.UtcNow));
        var robots = await checker.FetchWellKnownAsync(domain, "/robots.txt", ct);
        if (robots.StatusCode == 429) throw new HttpRequestException("robots.txt 返回 429，稍后重试。");
        if (robots.Error is not null) Add("robots", "error", robots.Error);
        else Add("robots", robots.StatusCode == 200 ? "pass" : "fail", $"HTTP {robots.StatusCode}");
        // Conservative policy: a root disallow for any user agent blocks further probing.
        if (robots.Content is not null && Regex.IsMatch(robots.Content,
                @"(?im)^\s*Disallow\s*:\s*/\s*(?:#.*)?$", RegexOptions.CultureInvariant))
        {
            foreach (var key in new[] { "reachability", "https", "sitemap", "canonical", "schema", "quotable_text" })
                Add(key, "not_tested", "robots.txt 禁止抓取根路径；未继续检测。");
            return checks;
        }

        var site = await checker.CheckAsync(domain, ct);
        if (site.HttpsStatusCode == 429 || site.HttpStatusCode == 429)
            throw new HttpRequestException("网站返回 429，稍后重试。");
        if (!site.Accessible && site.TransientFailure)
            throw new HttpRequestException("网站暂不可达或超时，稍后重试。");
        if (site.FinalUrl is not null && site.FinalUrl.Length > 0
            && PublicWebTarget.Host(site.FinalUrl) != domain)
        {
            Add("reachability", "error", "首页跳转到其他域名，未把目标网站内容归属于该身份候选。");
            foreach (var key in new[] { "https", "sitemap", "canonical", "schema", "quotable_text" })
                Add(key, "not_tested", "首页发生跨域跳转。");
            return checks;
        }
        Add("reachability", site.Accessible ? "pass" : site.Error is null ? "fail" : "error",
            site.Accessible ? $"HTTP {site.HttpsStatusCode ?? site.HttpStatusCode}; {site.ResponseMs} ms" : site.Error ?? "未获得成功响应");
        Add("https", site.HttpsSupported && site.Accessible ? "pass" : site.Accessible ? "fail" : "not_tested",
            site.HttpsSupported ? $"HTTPS HTTP {site.HttpsStatusCode}" : "HTTPS 未成功返回网页");
        var sitemap = await checker.FetchWellKnownAsync(domain, "/sitemap.xml", ct);
        if (sitemap.StatusCode == 429) throw new HttpRequestException("sitemap.xml 返回 429，稍后重试。");
        Add("sitemap", sitemap.Error is not null ? "error" : sitemap.StatusCode == 200 ? "pass" : "fail",
            sitemap.Error ?? $"HTTP {sitemap.StatusCode}");
        var html = site.Html;
        if (!site.Accessible || string.IsNullOrWhiteSpace(html))
        {
            foreach (var key in new[] { "canonical", "schema", "quotable_text" })
                Add(key, "not_tested", "首页未成功获取 HTML。");
            return checks;
        }
        var canonical = Regex.Match(html, @"<link\b[^>]*rel\s*=\s*['"" ]canonical['"" ][^>]*>", RegexOptions.IgnoreCase);
        Add("canonical", canonical.Success ? "pass" : "fail", canonical.Success ? "首页存在 canonical 标记" : "首页未发现 canonical 标记");
        var schema = Regex.IsMatch(html, @"<script\b[^>]*type\s*=\s*['"" ]application/ld\+json['"" ]", RegexOptions.IgnoreCase);
        Add("schema", schema ? "pass" : "fail", schema ? "首页存在 JSON-LD 标记；未验证语义正确性" : "首页未发现 JSON-LD 标记");
        var plain = Regex.Replace(Regex.Replace(html, @"(?is)<(script|style)\b.*?</\1>", " "), @"<[^>]+>", " ");
        plain = WebUtility.HtmlDecode(plain);
        var length = Regex.Replace(plain, @"\s+", " ").Trim().Length;
        Add("quotable_text", length >= 200 ? "pass" : "fail", $"首页可见文本约 {length} 字符；阈值 200，仅为技术基线");
        return checks;
    }
}
