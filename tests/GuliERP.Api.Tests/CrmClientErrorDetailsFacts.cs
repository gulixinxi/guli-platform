using GuliERP.Api.Crm;
using Xunit;

namespace GuliERP.Api.Tests;

public sealed class CrmClientErrorDetailsFacts
{
    [Theory]
    [InlineData(400, "Password=SECRET_SENTINEL", "提交的资料无法处理，请检查后重试。")]
    [InlineData(409, "Host=internal-db;Password=SECRET_SENTINEL", "资料状态已变化，请刷新后重试。")]
    public void UnreviewedExceptionText_IsNotReturned(int status, string exceptionText, string expected)
    {
        var detail = CrmClientErrorDetails.Safe(status, exceptionText);
        Assert.Equal(expected, detail);
        Assert.DoesNotContain("SECRET_SENTINEL", detail);
    }

    [Fact]
    public void ReviewedBusinessMessage_RemainsActionable()
    {
        Assert.Equal(
            "确认身份至少需要一条非第三方的外部来源证据；第三方页面和人工备注不能单独作为依据。",
            CrmClientErrorDetails.Safe(409,
                "确认身份至少需要一条非第三方的外部来源证据；第三方页面和人工备注不能单独作为依据。"));
    }
}
