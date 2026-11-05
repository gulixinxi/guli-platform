namespace GuliERP.Crm.Application;

/// <summary>
/// 负责人（OwnerId）规则的纯判定，不含任何 I/O。
///
/// <para>
/// 规则来源（用户已定案，R42）：
/// 新建客户／商机／跟进记录时，普通管理权限可以指定本公司有效成员为初始负责人；
/// 建档之后更换或清空负责人必须走专门分配操作，普通编辑接口不得更换或清空。
/// </para>
///
/// <para>
/// 放在应用层而不是复制到各处，是为了让服务层与 API 层共用同一份判定，
/// 避免「同一规则多份拷贝、彼此漂移」（R41 报告 §5.1 记录的根因）。
/// </para>
/// </summary>
public static class CrmOwnerRules
{
    /// <summary>
    /// 请求是否试图更换或清空负责人。
    ///
    /// <list type="bullet">
    ///   <item>省略 <c>ownerId</c> 或传 <c>null</c> ⇒ 保持原负责人，<b>不算</b>变更。</item>
    ///   <item>传非 null 且与当前负责人不同 ⇒ 是变更，必须走专门分配操作。</item>
    ///   <item>传非 null 且与当前负责人相同 ⇒ 无变化，视为普通编辑。</item>
    /// </list>
    ///
    /// <para>
    /// 当前负责人为 <c>null</c> 的情形同样成立：此时传任何非 null 都是「变更」。
    /// </para>
    /// </summary>
    public static bool IsOwnerChange(long? currentOwnerId, long? requestedOwnerId) =>
        requestedOwnerId.HasValue && requestedOwnerId.Value != currentOwnerId;

    /// <summary>
    /// 解析本次实际要写入的负责人：请求显式指定的优先，否则继承原负责人。
    ///
    /// <para>
    /// 用于线索转客户：请求未指定新负责人时继承线索原负责人。
    /// 继承到的值<b>仍然</b>要过有效成员校验——继承不是豁免，
    /// 不能把一个早已失效的旧负责人 ID 静默写进新客户。
    /// </para>
    /// </summary>
    public static long? ResolveEffectiveOwner(long? requestedOwnerId, long? inheritedOwnerId) =>
        requestedOwnerId ?? inheritedOwnerId;
}

/// <summary>负责人所属业务对象。用于把服务层异常翻译成对应的专门分配入口。</summary>
public enum CrmOwnerEntityKind
{
    Company,
    Deal,
    Activity
}

/// <summary>
/// 普通编辑路径试图更换或清空负责人时抛出。
///
/// <para>
/// 服务层负责判定与抛出（任何调用方都拿到同样结论，不依赖 HTTP 层）；
/// API 层把它翻译成 4xx 客户端错误，并在文案里指向对应的专门分配入口。
/// </para>
/// </summary>
public sealed class CrmOwnerChangeNotAllowedException : InvalidOperationException
{
    public CrmOwnerChangeNotAllowedException(CrmOwnerEntityKind kind)
        : base($"普通编辑不能更换或清空负责人（{kind}）。")
    {
        Kind = kind;
    }

    public CrmOwnerEntityKind Kind { get; }
}
