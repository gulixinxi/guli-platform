using System.Net;
using System.Text.Json.Serialization;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record IdentityBatchRow(string Domain, string ProposedName, string SourceReference,
    string ClaimText, DateTime ObservedAt,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? AssetId = null);
public sealed record IdentityBatchRequest(List<IdentityBatchRow> Items, string? SourceFileName = null,
    string? SourceFileSha256 = null);
public sealed record IdentityBatchResultRow(int Row, string Domain, string Status, string Reason, string? AssetId);
public sealed record IdentityBatchResult(int Total, int Ready, int Created, List<IdentityBatchResultRow> Items);

/// <summary>Imports supplied third-party facts as pending candidates only; never fetches a vendor page.</summary>
public sealed class IdentityCandidateBatchService(CrmDbContext db, IdentityCandidateService candidates)
{
    public async Task<IdentityBatchResult> ProcessAsync(IdentityBatchRequest request, long actorId, bool commit, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("操作人员身份无效。");
        if (request?.Items is null || request.Items.Count is < 1 or > 200)
            throw new ArgumentException("每批须包含 1–200 条记录。");
        if ((request.SourceFileName?.Length ?? 0) > 200
            || (request.SourceFileSha256 is not null && !System.Text.RegularExpressions.Regex.IsMatch(request.SourceFileSha256, "^[a-fA-F0-9]{64}$")))
            throw new ArgumentException("导入文件名或摘要无效。");
        var rows = new List<IdentityBatchResultRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            var domain = (item?.Domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            var provenance = request.SourceFileSha256 is null ? ""
                : $"\n导入文件：{request.SourceFileName ?? "未命名"}；客户端 SHA-256：{request.SourceFileSha256}";
            if (item is null || !ValidDomain(domain) || !ValidFact(item) || item.ClaimText.Length + provenance.Length > 2000)
            {
                rows.Add(new(i + 1, domain, "invalid", "域名、主体名称、来源网址、原文或采集时间无效。", null));
                continue;
            }
            if (!seen.Add(domain + "\0" + item!.ProposedName.Trim().ToLowerInvariant() + "\0" + (item.AssetId?.ToString() ?? "")))
            {
                rows.Add(new(i + 1, domain, "duplicate_input", "本批已有相同域名和主体名称。", null));
                continue;
            }
            var assets = await db.QuakeAssetRecords.AsNoTracking()
                .Where(a => (item.AssetId == null || a.Id == item.AssetId)
                    && ((a.Domain != null && a.Domain.ToLower() == domain)
                    || (a.Hostname != null && a.Hostname.ToLower() == domain)))
                .Select(a => a.Id).Take(2).ToListAsync(ct);
            if (assets.Count != 1)
            {
                rows.Add(new(i + 1, domain, assets.Count == 0 ? "no_asset" : "ambiguous_asset",
                    assets.Count == 0 ? "当前公司无对应待审资产。" : "当前公司有多条同域名资产，须先指定具体资产后逐条录入。", null));
                continue;
            }
            var existing = await db.IdentityCandidates.AsNoTracking()
                .AnyAsync(c => c.AssetId == assets[0] && c.ProposedName == item.ProposedName.Trim(), ct);
            if (existing)
            {
                rows.Add(new(i + 1, domain, "existing_candidate", "该资产已有同名身份候选，未重复创建。", assets[0].ToString()));
                continue;
            }
            if (!commit)
            {
                rows.Add(new(i + 1, domain, "ready", "可保存为待审候选；第三方来源不能单独确认身份。", assets[0].ToString()));
                continue;
            }
            await candidates.CreateAsync(assets[0], new(item.ProposedName.Trim(),
                new("third_party", item.SourceReference.Trim(), item.ClaimText.Trim() + provenance, item.ObservedAt)), actorId, ct);
            created++;
            rows.Add(new(i + 1, domain, "created", "已保存为待审候选。", assets[0].ToString()));
        }
        return new(request.Items.Count, rows.Count(r => r.Status == "ready"), created, rows);
    }

    private static bool ValidDomain(string domain) => domain.Length <= 253 && domain.Contains('.')
        && Uri.CheckHostName(domain) == UriHostNameType.Dns && !IPAddress.TryParse(domain, out _)
        && !domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        && domain.Split('.').All(part => part.Length is > 0 and <= 63
            && part[0] != '-' && part[^1] != '-'
            && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static bool ValidFact(IdentityBatchRow item) =>
        !string.IsNullOrWhiteSpace(item.ProposedName) && item.ProposedName.Trim().Length is >= 2 and <= 300
        && !item.ProposedName.Contains('*') && !item.ProposedName.Contains('＊')
        && !string.IsNullOrWhiteSpace(item.ClaimText) && item.ClaimText.Length <= 2000
        && !string.IsNullOrWhiteSpace(item.SourceReference) && item.SourceReference.Length <= 1000
        && Uri.TryCreate(item.SourceReference.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && item.ObservedAt >= DateTime.UtcNow.AddYears(-10) && item.ObservedAt <= DateTime.UtcNow.AddDays(1);
}
