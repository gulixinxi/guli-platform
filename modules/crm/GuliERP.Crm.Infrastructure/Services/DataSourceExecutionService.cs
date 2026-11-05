using System.Data.Common;
using GuliERP.Crm.Domain.Entities;
using GuliERP.Crm.Infrastructure.Persistence;
using GuliERP.Foundation.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GuliERP.Crm.Infrastructure.Services;

public enum SourceAdapterOutcome { Success, RateLimited, TemporaryFailure, PermanentFailure }

public sealed record SourceAdapterReply(SourceAdapterOutcome Outcome,
    IReadOnlyDictionary<string, string>? Fields = null);
public sealed record SourceLookupRequest(string SourceCode, string TargetKind, string TargetValue,
    IReadOnlyList<string> RequestedFields);

/// <summary>Replaceable provider contract. An adapter returns observations; it never writes CRM records.</summary>
public interface IDataSourceAdapter
{
    string SourceCode { get; }
    bool IsSynthetic { get; }
    Task<SourceAdapterReply> ExecuteAsync(SourceLookupRequest request, CancellationToken ct);
}

public sealed record DataSourceExecutionResult(string SourceCode, string Status, Guid RequestId,
    int Attempts, IReadOnlyDictionary<string, string>? Fields = null);

/// <summary>
/// P1-1C execution boundary. Only synthetic adapters are accepted at present. A reviewed
/// registration cannot activate a live vendor adapter; that requires a later, explicit gate.
/// Every physical attempt reserves one quota unit before invoking the adapter.
/// </summary>
public sealed class DataSourceExecutionService(CrmDbContext db, ICurrentTenant tenant, ICurrentCompany company)
{
    private static readonly SemaphoreSlim InMemoryReservationGate = new(1, 1);
    private const int MaxAttempts = 3;
    private static readonly IReadOnlyDictionary<string, string[]> TargetKinds =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["quake"] = ["query"], ["chinaz"] = ["domain"], ["icp_manual"] = ["domain"],
            ["registry"] = ["company_name", "credit_code"],
            ["baidu_ads"] = ["keyword", "domain"],
            ["recruitment"] = ["company_name", "domain"]
        };

    public Task<DataSourceExecutionResult> ExecuteAsync(SourceLookupRequest query,
        IDataSourceAdapter adapter, CancellationToken ct) => ExecuteCoreAsync(query, adapter, MaxAttempts, ct);

    /// <summary>
    /// Synthetic one-physical-attempt boundary for legacy connector migration tests.
    /// A caller must make a new reservation before each mock Key fallback or batch item.
    /// Live adapters remain rejected, and no existing live connector invokes this method.
    /// </summary>
    public Task<DataSourceExecutionResult> ExecuteOneSyntheticAttemptAsync(SourceLookupRequest query,
        IDataSourceAdapter adapter, CancellationToken ct) => ExecuteCoreAsync(query, adapter, 1, ct);

    private async Task<DataSourceExecutionResult> ExecuteCoreAsync(SourceLookupRequest query,
        IDataSourceAdapter adapter, int maxAttempts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sourceCode = query.SourceCode;
        var requestedFields = query.RequestedFields;
        if (tenant.Id is not long tenantId || tenantId <= 0 ||
            company.Id is not long companyId || companyId <= 0)
            throw new InvalidOperationException("缺少租户或公司范围。");
        if (!DataSourceAuthorizationService.IsKnownSource(sourceCode))
            throw new ArgumentException("未知数据源。");
        if (adapter.SourceCode != sourceCode) throw new ArgumentException("适配器与数据源不匹配。");
        if (!adapter.IsSynthetic) return new(sourceCode, "not_configured", Guid.Empty, 0);
        if (!TargetKinds[sourceCode].Contains(query.TargetKind, StringComparer.Ordinal) ||
            string.IsNullOrWhiteSpace(query.TargetValue) || query.TargetValue.Length > 300 ||
            query.TargetValue.Any(char.IsControl))
            throw new ArgumentException("查询目标类型或内容无效。");
        if (requestedFields is null || requestedFields.Count is < 1 or > 20 ||
            requestedFields.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64) ||
            requestedFields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requestedFields.Count ||
            string.Join(',', requestedFields).Length > 1000)
            throw new ArgumentException("请求字段无效或重复。");

        var row = await db.DataSourceAuthorizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.SourceCode == sourceCode, ct);
        if (row is null || row.Status != "reviewed")
            return new(sourceCode, "not_configured", Guid.Empty, 0);
        var now = DateTime.UtcNow;
        if (row.ValidFrom > now) return new(sourceCode, "not_yet_valid", Guid.Empty, 0);
        if (row.ValidUntil <= now) return new(sourceCode, "expired", Guid.Empty, 0);
        var allowed = row.PermittedFields.Split([',', '，', ';', '；', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!requestedFields.All(allowed.Contains))
            return new(sourceCode, "field_denied", Guid.Empty, 0);

        var requestId = Guid.NewGuid();
        for (var number = 1; number <= maxAttempts; number++)
        {
            var reservation = await ReserveAsync(row, tenantId, companyId, requestId,
                number, requestedFields, ct);
            if (reservation.Attempt is null)
                return new(sourceCode, reservation.BlockedStatus!, requestId, number - 1);
            var attempt = reservation.Attempt;

            SourceAdapterReply reply;
            try { reply = await adapter.ExecuteAsync(query, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CompleteAsync(attempt, "uncertain", ct: CancellationToken.None);
                throw;
            }
            catch (Exception)
            {
                // The provider may have received the request. Do not retry an ambiguous failure.
                await CompleteAsync(attempt, "uncertain", ct: CancellationToken.None);
                return new(sourceCode, "uncertain", requestId, number);
            }

            var status = reply.Outcome switch
            {
                SourceAdapterOutcome.Success => "succeeded",
                SourceAdapterOutcome.RateLimited => "rate_limited",
                SourceAdapterOutcome.TemporaryFailure => "temporary_failure",
                _ => "permanent_failure"
            };
            await CompleteAsync(attempt, status, ct: ct);
            if (reply.Outcome == SourceAdapterOutcome.Success)
            {
                var fields = (reply.Fields ?? new Dictionary<string, string>())
                    .Where(x => requestedFields.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
                    .ToDictionary(x => x.Key, x => x.Value);
                return new(sourceCode, "succeeded", requestId, number, fields);
            }
            if (reply.Outcome == SourceAdapterOutcome.PermanentFailure || number == maxAttempts)
                return new(sourceCode, status, requestId, number);
            await Task.Delay(TimeSpan.FromMilliseconds(25 * number), ct);
        }
        throw new InvalidOperationException("执行状态无效。");
    }

    public async Task<IReadOnlyList<object>> ListUsageAsync(CancellationToken ct)
    {
        if (tenant.Id is not > 0 || company.Id is not > 0)
            throw new InvalidOperationException("缺少租户或公司范围。");
        var now = DateTime.UtcNow;
        var dayStart = now.Date;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var authorizations = await db.DataSourceAuthorizations.AsNoTracking().OrderBy(x => x.SourceCode).ToListAsync(ct);
        var attempts = await db.DataSourceExecutionAttempts.AsNoTracking()
            .Where(x => x.ReservedAt >= monthStart).ToListAsync(ct);
        return authorizations.Select(row => (object)new
        {
            row.SourceCode,
            RuntimeStatus = "live_disabled",
            row.DailyQuota, row.MonthlyQuota,
            DailyUsed = attempts.Count(x => x.AuthorizationId == row.Id && x.ReservedAt >= dayStart),
            MonthlyUsed = attempts.Count(x => x.AuthorizationId == row.Id)
        }).ToList();
    }

    private sealed record Reservation(DataSourceExecutionAttempt? Attempt, string? BlockedStatus = null);

    private async Task<Reservation> ReserveAsync(DataSourceAuthorization authorization,
        long tenantId, long companyId, Guid requestId, int number,
        IReadOnlyList<string> fields, CancellationToken ct)
    {
        var memoryLock = !db.Database.IsRelational();
        if (memoryLock) await InMemoryReservationGate.WaitAsync(ct);
        try
        {
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct) : null;
            if (transaction is not null)
                await LockAuthorizationAsync(authorization.Id, transaction, ct);
            // Recheck validity while holding the quota lock.
            var now = DateTime.UtcNow;
            var current = await db.DataSourceAuthorizations.AsNoTracking()
                .SingleAsync(x => x.Id == authorization.Id, ct);
            var permitted = current.PermittedFields.Split([',', '，', ';', '；', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current.Status != "reviewed") return new(null, "not_configured");
            if (current.ValidFrom > now) return new(null, "not_yet_valid");
            if (current.ValidUntil <= now) return new(null, "expired");
            if (!fields.All(permitted.Contains)) return new(null, "field_denied");
            var dayStart = now.Date;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthCount = await db.DataSourceExecutionAttempts.CountAsync(x =>
                x.AuthorizationId == authorization.Id && x.ReservedAt >= monthStart, ct);
            var dayCount = await db.DataSourceExecutionAttempts.CountAsync(x =>
                x.AuthorizationId == authorization.Id && x.ReservedAt >= dayStart, ct);
            if (current.MonthlyQuota is int monthly && monthCount >= monthly ||
                current.DailyQuota is int daily && dayCount >= daily)
                return new(null, "quota_exhausted");
            var attempt = new DataSourceExecutionAttempt
            {
                TenantId = tenantId, CompanyId = companyId, AuthorizationId = authorization.Id,
                SourceCode = authorization.SourceCode, RequestId = requestId,
                AttemptNumber = number, RequestedFields = string.Join(',', fields),
                ReservedAt = now
            };
            db.DataSourceExecutionAttempts.Add(attempt);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new(attempt);
        }
        finally { if (memoryLock) InMemoryReservationGate.Release(); }
    }

    private async Task CompleteAsync(DataSourceExecutionAttempt attempt, string status, CancellationToken ct)
    {
        attempt.Status = status;
        attempt.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task LockAuthorizationAsync(long id, IDbContextTransaction transaction, CancellationToken ct)
    {
        DbConnection connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT \"Id\" FROM crm.data_source_authorizations WHERE \"Id\"=@id AND \"TenantId\"=@tenant AND \"CompanyId\"=@company FOR UPDATE";
        foreach (var (name, value) in new[] { ("id", id), ("tenant", tenant.Id!.Value), ("company", company.Id!.Value) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        if (await command.ExecuteScalarAsync(ct) is null)
            throw new InvalidOperationException("授权资料已变更或不在当前公司范围。");
    }
}
