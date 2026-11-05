using System.Text;
using System.Text.Json;
using GuliERP.Api.Kernel;
using GuliERP.Foundation.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GuliERP.Api.Tests;

/// <summary>
/// 异常边界：调用方传输故障（<see cref="BadHttpRequestException"/>）必须映射为
/// 4xx，而不是 500。
///
/// <para><b>为什么这条要锁死</b>：请求体读不出来、Content-Type 不支持、载荷超限，
/// 这些错误的产生方是调用方。返回 500 会让客户端和重试中间件<b>反复重发一个永远
/// 不会成功的请求</b>，同时把服务端故障看板污染成假告警。ASP.NET 已经在异常上算好了
/// 精确状态码（400 / 413 / 415），把它丢掉是信息损失。</para>
///
/// <para><b>为什么必须带一条 500 守卫</b>：新增分支有把真实服务端故障误降级成 4xx 的
/// 风险（业务异常被误判成传输故障）。<see cref="NonClientFault_StillMapsTo500"/>
/// 是这条风险的反向验证——摘掉修复它仍然绿，说明它真的在测接线而不是在测常量。</para>
/// </summary>
public class MalformedRequestMappingFacts
{
    private static readonly StubRequestContextAccessor Accessor = new();

    private sealed class StubRequestContextAccessor : IRequestContextAccessor
    {
        public RequestContext? Current { get; private set; } =
            new("req-test-0001", "trace-test-0001", DateTimeOffset.UtcNow);

        public void Set(RequestContext context) => Current = context;

        public void Clear() => Current = null;
    }

    /// <summary>跑一次异常边界，返回 (状态码, 响应体)。</summary>
    private static async Task<(int Status, string Body)> HandleAsync(Exception ex)
    {
        var handler = new FoundationExceptionHandler(
            NullLogger<FoundationExceptionHandler>.Instance, Accessor);

        var ctx = new DefaultHttpContext();
        var buffer = new MemoryStream();
        ctx.Response.Body = buffer;

        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled, "异常边界必须接管该异常（返回 true），否则会掉进默认 500 管道");

        buffer.Position = 0;
        var body = Encoding.UTF8.GetString(buffer.ToArray());
        return (ctx.Response.StatusCode, body);
    }

    private static string? CodeOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        // ProblemDetails.Extensions is [JsonExtensionData]: the extension
        // keys are flattened onto the ROOT object, not nested under
        // "extensions".
        var root = doc.RootElement;
        if (!root.TryGetProperty(ProblemDetailsExtensions.CodeKey, out var code))
        {
            throw new Xunit.Sdk.XunitException(
                $"Response body has no '{ProblemDetailsExtensions.CodeKey}' key. Body: {body}");
        }
        return code.GetString();
    }

    // ----------------------------------------------------------------
    // 调用方传输故障 → 4xx
    // ----------------------------------------------------------------

    [Theory]
    [InlineData(400)]
    [InlineData(408)]   // Request Timeout
    [InlineData(413)]   // Payload Too Large
    [InlineData(415)]   // Unsupported Media Type
    public async Task ClientFault_PreservesTheStatusAspNetComputed(int expected)
    {
        var (status, body) = await HandleAsync(
            new BadHttpRequestException("body unreadable", expected));

        Assert.Equal(expected, status);
        Assert.Equal(ErrorCodes.MalformedRequest, CodeOf(body));
    }

    [Fact]
    public async Task MalformedBody_Is400Not500()
    {
        // 未显式给 StatusCode 时 ASP.NET 的默认值就是 400。
        var (status, body) = await HandleAsync(
            new BadHttpRequestException("Unexpected end of content"));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.NotEqual(StatusCodes.Status500InternalServerError, status);
        Assert.Equal(ErrorCodes.MalformedRequest, CodeOf(body));
    }

    [Fact]
    public async Task PathologicalServerStatus_IsClampedTo400()
    {
        // 防御性分支：万一某天异常上带了个 5xx，也不能把调用方故障报成服务端故障。
        var (status, _) = await HandleAsync(
            new BadHttpRequestException("odd", StatusCodes.Status500InternalServerError));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    // ----------------------------------------------------------------
    // G2-002 禁令：响应体不得回显异常消息
    // ----------------------------------------------------------------

    [Fact]
    public async Task ResponseBody_DoesNotEchoTheExceptionMessage()
    {
        // BadHttpRequestException.Message 可能带上调用方自己发来的载荷片段，
        // 所以响应体和日志都不能包含它。
        const string payloadEcho = "SENTINEL_PAYLOAD_DO_NOT_ECHO";
        var (_, body) = await HandleAsync(
            new BadHttpRequestException($"Failed to read parameter '{payloadEcho}'"));

        Assert.DoesNotContain(payloadEcho, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedRequestLog_DoesNotRecordCallerPayload()
    {
        const string payloadEcho = "SENTINEL_PAYLOAD_DO_NOT_LOG";
        var logger = new CaptureLogger();
        var handler = new FoundationExceptionHandler(logger, Accessor);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await handler.TryHandleAsync(ctx,
            new BadHttpRequestException($"Unreadable body: {payloadEcho}"), CancellationToken.None);

        Assert.Single(logger.Entries);
        Assert.DoesNotContain(payloadEcho, logger.Entries[0].Message, StringComparison.Ordinal);
        Assert.Null(logger.Entries[0].Exception);
    }

    private sealed class CaptureLogger : ILogger<FoundationExceptionHandler>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }

    [Fact]
    public async Task ResponseBody_CarriesRequestIdAndTraceId()
    {
        var (_, body) = await HandleAsync(new BadHttpRequestException("x", 400));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("req-test-0001", root.GetProperty(ProblemDetailsExtensions.RequestIdKey).GetString());
        Assert.Equal("trace-test-0001", root.GetProperty(ProblemDetailsExtensions.TraceIdKey).GetString());
    }

    [Fact]
    public async Task ResponseBody_UsesProblemJsonContentType()
    {
        var handler = new FoundationExceptionHandler(
            NullLogger<FoundationExceptionHandler>.Instance, Accessor);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await handler.TryHandleAsync(ctx, new BadHttpRequestException("x", 400), CancellationToken.None);

        Assert.Equal(
            "application/problem+json; charset=utf-8",
            ctx.Response.ContentType);
    }

    // ----------------------------------------------------------------
    // 反向验证：真实服务端故障绝不能被降级
    // ----------------------------------------------------------------

    [Fact]
    public async Task NonClientFault_StillMapsTo500()
    {
        var (status, body) = await HandleAsync(new InvalidOperationException("boom"));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal(ErrorCodes.InternalError, CodeOf(body));
        Assert.DoesNotContain("boom", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubclassedClientFault_StillMapsTo4xx()
    {
        // 用派生类型锁住：判定必须基于 BadHttpRequestException 基类，
        // 否则将来某个框架内部子类会绕过这条分支。
        var (status, body) = await HandleAsync(
            new DerivedBadRequestException(StatusCodes.Status413PayloadTooLarge));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, status);
        Assert.Equal(ErrorCodes.MalformedRequest, CodeOf(body));
    }

    private sealed class DerivedBadRequestException : BadHttpRequestException
    {
        public DerivedBadRequestException(int statusCode)
            : base("derived", statusCode) { }
    }
}
