using GuliERP.Mdm.Application;
using GuliERP.Mdm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuliERP.Mdm.Infrastructure.Seed;

/// <summary>
/// 播种 dry-run 沙箱（TASK 3 第 7 项缺陷修复）。
///
/// <para>
/// <b>缺陷</b>：<c>seed-mdm numbering --dry-run</c> 与
/// <c>seed-mdm masterdata --dry-run</c> 从来没有把 <c>--dry-run</c> 传给服务层，
/// 服务层也没有任何 <c>DryRun</c> 分支 ⇒ 干跑照样把行写进数据库。
/// 只有 <see cref="ReferenceSeedService"/> 实现了真正的干跑
/// （关系库事务回滚 / 内存库跳过 SaveChanges）。
/// </para>
///
/// <para>
/// <b>修法</b>：关系型数据库上把整次播种包进一个事务，结束时
/// <c>RollbackAsync</c>。因为所有 MDM 服务共用同一个 scoped
/// <see cref="MdmDbContext"/> 实例，下游 <c>SaveChangesAsync</c> 会加入
/// 这个外层事务里，因此整次播种的写入被整体撤销。
/// </para>
///
/// <para>
/// <b>非关系型 provider（InMemory 等）明确拒绝</b>：内存库的
/// <c>SaveChangesAsync</c> 是真写入且不支持事务回滚，此时「声称干跑却真的写库」
/// 比直接报错更危险。因此这里抛 <see cref="MdmValidationException"/>，
/// 让调用方拿不到任何伪造的干跑结果。
/// </para>
/// </summary>
internal static class SeedDryRunScope
{
    /// <summary>
    /// 在 dry-run 事务中执行 <paramref name="body"/>，结束后回滚，返回 body 的结果。
    /// </summary>
    /// <exception cref="MdmValidationException">
    /// 当前 provider 不是关系型数据库，无法回滚写入。
    /// </exception>
    public static async Task<T> RunAsync<T>(
        MdmDbContext db,
        Func<Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(body);

        if (!db.Database.IsRelational())
        {
            var providerName = db.Database.ProviderName ?? "(unknown)";
            throw new MdmValidationException(
                MdmErrorCodes.ValidationFailed,
                $"Dry-run 需要支持事务回滚的关系型数据库；当前 provider '{providerName}' 无法回滚写入。"
                + "为避免「声称干跑却真的写库」，已拒绝执行。请改用 PostgreSQL 运行 dry-run。");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await body();
            await tx.RollbackAsync(ct);
            return result;
        }
        catch
        {
            // 回滚后再抛：失败路径同样不能留下任何行。
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
