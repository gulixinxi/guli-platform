namespace GuliERP.Crm.Application;

/// <summary>
/// CRM 业务校验失败：调用方的输入不能被接受。
///
/// <para>
/// 服务层负责判定与抛出（任何调用方——不只是 HTTP——都拿到同样结论）；
/// API 层把它翻译成 4xx（400 输入非法 / 409 资源冲突）。
/// </para>
///
/// <para>
/// 继承 <see cref="InvalidOperationException"/> 是为了保持与既有
/// <see cref="CrmOwnerChangeNotAllowedException"/> 一致的调用方兼容面：
/// 任何按 <c>InvalidOperationException</c> 捕获的旧调用方仍然能捕获本异常。
/// </para>
///
/// <para>
/// 消息必须是<b>固定文案</b>：不得回显调用方输入（统一社会信用代码、
/// 公司全称等），否则会把调用方自己的资料带出服务层。
/// </para>
/// </summary>
public sealed class CrmValidationException : InvalidOperationException
{
    public CrmValidationException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>稳定的机器可读错误码。</summary>
    public string Code { get; }
}

/// <summary>CRM 业务校验错误码（稳定、机器可读）。</summary>
public static class CrmErrorCodes
{
    /// <summary>
    /// 统一社会信用代码超过 <c>crm.companies.CreditCode</c> 的 18 位上限。
    /// </summary>
    public const string InvalidCreditCode = "crm.invalid_credit_code";

    /// <summary>
    /// 同一统一社会信用代码 / 公司全名的客户已存在。
    /// </summary>
    public const string DuplicateCompany = "crm.duplicate_company";
}
