using System.Text.Json;
using GuliERP.Crm.Application;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GuliERP.Crm.Infrastructure.Services;

public sealed record ContactCandidateInput(string IdentityCandidateId, string? Name, string? Position,
    string? Email, string? Phone, string SourceType, string SourceReference,
    Dictionary<string, string> FieldSources, DateTime ObservedAt);
public sealed record ContactCandidateReviewInput(string Action, string Reason);
public sealed record ContactCandidateReasonInput(string Reason);
public sealed record ContactCandidateCorrectionInput(ContactCandidateInput Candidate, string Reason);

/// <summary>Stores reviewable contact leads without writing the official CRM contact table.</summary>
public sealed class ContactCandidateService(CrmDbContext db)
{
    public async Task<object> ListAsync(long identityCandidateId, long actorId, CancellationToken ct)
    {
        if (actorId <= 0) throw new ArgumentException("查看人无效。");
        if (!await db.IdentityCandidates.AnyAsync(x => x.Id == identityCandidateId, ct))
            throw new KeyNotFoundException("身份候选不存在。");
        var now = DateTime.UtcNow;
        var candidates = await db.ContactCandidates.AsNoTracking()
            .Where(x => x.IdentityCandidateId == identityCandidateId)
            .OrderByDescending(x => x.CreatedAt).Take(100)
            .ToListAsync(ct);
        var visible = candidates.Where(x => x.Status != "redacted" && x.ExpiresAt > now
            && (x.Phone != null || x.Email != null)).ToList();
        foreach (var candidate in visible)
            AddEvent(candidate.Id, "list_view", "有联系人查看权限的账号查看候选列表", actorId);
        if (visible.Count > 0) await db.SaveChangesAsync(ct);
        var items = candidates.Select(x => new { id = x.Id.ToString(),
            Name = x.ExpiresAt > now && x.Status != "redacted" ? x.Name : null,
            Position = x.ExpiresAt > now && x.Status != "redacted" ? x.Position : null,
            Email = x.ExpiresAt > now && x.Status != "redacted" ? x.Email : null,
            Phone = x.ExpiresAt > now && x.Status != "redacted" ? x.Phone : null,
            x.SourceType, x.SourceReference, x.FieldSourcesJson,
            Status = x.ExpiresAt <= now && x.Status != "redacted" ? "expired" : x.Status,
            x.ObservedAt, x.ExpiresAt, x.ReviewedAt, x.ReviewReason }).ToList();
        return new { items };
    }

    public async Task<string> CreateAsync(ContactCandidateInput input, long actorId, CancellationToken ct,
        long? excludedCandidateId = null)
    {
        if (actorId <= 0 || input.ObservedAt > DateTime.UtcNow.AddMinutes(5)
            || input.ObservedAt < DateTime.UtcNow.AddYears(-2)) throw new ArgumentException("采集时间或操作人无效。");
        if (!long.TryParse(input.IdentityCandidateId, out var identityId) || identityId <= 0)
            throw new ArgumentException("企业身份候选 ID 无效。");
        var identity = await db.IdentityCandidates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == identityId && x.Status == "confirmed", ct)
            ?? throw new InvalidOperationException("请先确认企业身份。");
        if (input.SourceType is not ("public_site" or "recruitment" or "manual")
            || !Uri.TryCreate(input.SourceReference, UriKind.Absolute, out var source)
            || source.Scheme != Uri.UriSchemeHttps || input.SourceReference.Length > 1000)
            throw new ArgumentException("请填写公开 HTTPS 来源链接。");
        var name = input.Name?.Trim(); var position = input.Position?.Trim();
        var email = input.Email?.Trim(); var phone = input.Phone?.Trim();
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException("至少填写一种联系方式。");
        if (name?.Length > 120 || position?.Length > 120 || email?.Length > 200 || phone?.Length > 50)
            throw new ArgumentException("联系人字段过长。");
        if (input.FieldSources is null || input.FieldSources.Count == 0
            || new[] { ("email", email), ("phone", phone), ("name", name), ("position", position) }
                .Any(field => !string.IsNullOrWhiteSpace(field.Item2)
                    && (!input.FieldSources.TryGetValue(field.Item1, out var reference)
                        || reference != input.SourceReference)))
            throw new ArgumentException("每个填写的字段都必须标明同一公开来源。");
        if (await db.ContactCandidates.AnyAsync(x => x.IdentityCandidateId == identity.Id
            && x.Id != excludedCandidateId
            && x.Status != "redacted" && x.SourceReference == input.SourceReference
            && ((email != null && x.Email == email) || (phone != null && x.Phone == phone)), ct))
            throw new InvalidOperationException("相同来源和联系方式已有待审候选，请先核对现有记录。");
        var id = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20);
        db.ContactCandidates.Add(new ContactCandidate { Id = id, IdentityCandidateId = identity.Id,
            Name = name, Position = position, Email = email, Phone = phone,
            SourceType = input.SourceType, SourceReference = input.SourceReference,
            FieldSourcesJson = JsonSerializer.Serialize(input.FieldSources), ObservedAt = input.ObservedAt,
            ExpiresAt = input.ObservedAt.AddDays(90), CreatedBy = actorId });
        AddEvent(id, "create", "按公开来源录入待审候选", actorId);
        await db.SaveChangesAsync(ct);
        return id.ToString();
    }

    public async Task<object> RevealAsync(long id, string reason, long actorId, CancellationToken ct)
    {
        ValidateReason(reason, actorId);
        var item = await db.ContactCandidates.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("联系人候选不存在。");
        if (item.Status == "redacted") throw new InvalidOperationException("该候选已删除个人信息。");
        if (item.ExpiresAt <= DateTime.UtcNow) throw new InvalidOperationException("来源保留期已过，请重新采集。");
        AddEvent(id, "reveal", reason.Trim(), actorId);
        await db.SaveChangesAsync(ct);
        return new { id = item.Id.ToString(), item.Name, item.Position, item.Email, item.Phone,
            item.SourceType, item.SourceReference, item.FieldSourcesJson, item.Status,
            item.ObservedAt, item.ExpiresAt, item.ReviewReason, item.PromotedContactId };
    }

    public async Task<object> EventsAsync(long id, CancellationToken ct)
    {
        if (!await db.ContactCandidates.AnyAsync(x => x.Id == id, ct))
            throw new KeyNotFoundException("联系人候选不存在。");
        var events = await db.ContactCandidateEvents.AsNoTracking().Where(x => x.CandidateId == id)
            .OrderByDescending(x => x.OccurredAt).Take(100)
            .Select(x => new { x.Action, x.Reason, x.ActorId, x.OccurredAt }).ToListAsync(ct);
        return new { items = events };
    }

    public async Task ReviewAsync(long id, ContactCandidateReviewInput input, long actorId, CancellationToken ct)
    {
        ValidateReason(input.Reason, actorId);
        if (input.Action is not ("confirm" or "reject")) throw new ArgumentException("审核动作无效。");
        var item = await db.ContactCandidates.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("联系人候选不存在。");
        if (item.Status != "pending") throw new InvalidOperationException("只有待审候选可审核。");
        if (input.Action == "confirm" && item.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("来源已过期，请重新采集和核对。");
        item.Status = input.Action == "confirm" ? "confirmed" : "rejected";
        item.ReviewReason = input.Reason.Trim(); item.ReviewedBy = actorId; item.ReviewedAt = DateTime.UtcNow;
        AddEvent(id, input.Action, item.ReviewReason, actorId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<long> PromoteAsync(long id, string reason, long actorId, CancellationToken ct)
    {
        ValidateReason(reason, actorId);
        var item = await db.ContactCandidates.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("联系人候选不存在。");
        if (item.Status == "redacted") throw new InvalidOperationException("该候选已删除个人信息。");
        if (item.PromotedContactId.HasValue) return item.PromotedContactId.Value;
        if (item.Status != "confirmed" || item.ExpiresAt <= DateTime.UtcNow)
            throw new InvalidOperationException("只有已审核且未过期的候选可转为联系人。");
        if (item.SourceType == "recruitment")
            throw new InvalidOperationException("招聘联系方式不能直接转为销售联系人，请另行核实销售联系用途与来源。");
        if (string.IsNullOrWhiteSpace(item.Name))
            throw new InvalidOperationException("正式联系人需要经核实的姓名。");
        var companyId = await db.Leads.AsNoTracking()
            .Where(x => x.SourceIdentityCandidateId == item.IdentityCandidateId && x.ConvertedToCompanyId != null)
            .Select(x => x.ConvertedToCompanyId).FirstOrDefaultAsync(ct);
        if (companyId is null || !await db.Companies.AnyAsync(x => x.Id == companyId, ct))
            throw new InvalidOperationException("请先将对应线索转为 CRM 客户。");
        if (await db.Contacts.AnyAsync(x => x.CrmCompanyId == companyId
            && ((item.Email != null && x.Email == item.Email) || (item.Phone != null && x.Phone == item.Phone)), ct))
            throw new InvalidOperationException("该客户已有相同联系方式，请人工核对去重。");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var contact = await new ContactService(db).CreateAsync(new CreateContactRequest(companyId.Value,
            item.Name, item.Position, null, null, item.Phone, null, item.Email, false,
            $"候选来源：{item.SourceReference}"), ct);
        item.PromotedContactId = contact.Id;
        AddEvent(id, "promote", reason.Trim(), actorId);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return contact.Id;
    }

    public async Task RedactAsync(long id, string reason, long actorId, CancellationToken ct)
    {
        ValidateReason(reason, actorId);
        var item = await db.ContactCandidates.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("联系人候选不存在。");
        if (item.Status == "redacted") return;
        item.Name = null; item.Position = null; item.Email = null; item.Phone = null;
        item.FieldSourcesJson = "{}"; item.Status = "redacted";
        AddEvent(id, "redact", reason.Trim(), actorId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> CorrectAsync(long id, ContactCandidateCorrectionInput input, long actorId, CancellationToken ct)
    {
        ValidateReason(input.Reason, actorId);
        var old = await db.ContactCandidates.FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new KeyNotFoundException("联系人候选不存在。");
        if (old.Status == "redacted" || old.PromotedContactId.HasValue)
            throw new InvalidOperationException("已删除或已转正式联系人的候选须走正式联系人更正流程。");
        if (input.Candidate.IdentityCandidateId != old.IdentityCandidateId.ToString())
            throw new ArgumentException("更正不能改变企业身份归属。");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var replacementId = await CreateAsync(input.Candidate, actorId, ct, id);
        old.Name = null; old.Position = null; old.Email = null; old.Phone = null;
        old.FieldSourcesJson = "{}"; old.Status = "redacted";
        AddEvent(id, "correct", $"{input.Reason.Trim()[..Math.Min(input.Reason.Trim().Length, 950)]}；替代候选 {replacementId}", actorId);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return replacementId;
    }

    private void AddEvent(long id, string action, string reason, long actorId) =>
        db.ContactCandidateEvents.Add(new ContactCandidateEvent
        {
            Id = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
            CandidateId = id, Action = action, Reason = reason, ActorId = actorId
        });

    private static void ValidateReason(string? reason, long actorId)
    {
        if (actorId <= 0 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 10 or > 1000)
            throw new ArgumentException("请填写 10–1000 字的操作理由。");
    }

}

/// <summary>Clears expired candidate PII, preserving only non-sensitive audit metadata.</summary>
public sealed class ContactCandidateRetentionWorker(IServiceScopeFactory scopes,
    ILogger<ContactCandidateRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RedactNextBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Contact candidate retention cycle failed"); }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    internal async Task<int> RedactNextBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1129271876)", ct);
        var expired = await db.ContactCandidates.IgnoreQueryFilters()
            .Where(x => x.Status != "redacted" && x.ExpiresAt <= DateTime.UtcNow)
            .OrderBy(x => x.ExpiresAt).Take(100).ToListAsync(ct);
        foreach (var item in expired)
        {
            item.Name = null; item.Position = null; item.Email = null; item.Phone = null;
            item.FieldSourcesJson = "{}"; item.Status = "redacted";
            db.ContactCandidateEvents.Add(new ContactCandidateEvent
            {
                Id = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 20) | Random.Shared.NextInt64(1 << 20),
                TenantId = item.TenantId, CompanyId = item.CompanyId, CandidateId = item.Id,
                Action = "expire", Reason = "90 天保留期到期自动清理", ActorId = 0
            });
        }
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return expired.Count;
    }
}
