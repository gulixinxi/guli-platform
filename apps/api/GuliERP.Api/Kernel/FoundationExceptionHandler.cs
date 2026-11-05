using GuliERP.Foundation.Kernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace GuliERP.Api.Kernel;

/// <summary>
/// Foundation <see cref="IExceptionHandler"/> that maps unhandled
/// exceptions to an RFC 9457 / 7807 <see cref="ProblemDetails"/>
/// response with the GuliERP extensions (<c>code</c>,
/// <c>requestId</c>, <c>traceId</c>).
///
/// <para>
/// G2-002 contract:
/// <list type="bullet">
///   <item>HTTP 500 + <c>code = internal_error</c> for any exception the
///         Foundation cannot classify.</item>
///   <item>HTTP 4xx + <c>code = malformed_request</c> for
///         <see cref="BadHttpRequestException"/> — a caller-side
///         transport fault (unreadable body, bad Content-Type,
///         oversized payload). The status ASP.NET computed is
///         preserved (400 / 413 / 415); these must not be reported
///         as 500, because resending the same request can never
///         succeed.</item>
///   <item>Never expose stack trace / SQL / connection string /
///         password / token / internal file path in the response body.</item>
///   <item>Server-side log carries the full exception with
///         <c>RequestId</c> + <c>TraceId</c> for operator correlation.</item>
/// </list>
/// </para>
///
/// <para>
/// Business <see cref="Exception"/> → specific HTTP status mapping is
/// deferred to a later Goal (per task §8 "未来业务异常 另行 Goal 定义");
/// G2-002 deliberately does NOT introduce a business-exception
/// hierarchy.
/// </para>
/// </summary>
public sealed class FoundationExceptionHandler : IExceptionHandler
{
    private readonly ILogger<FoundationExceptionHandler> _logger;
    private readonly IRequestContextAccessor _accessor;

    public FoundationExceptionHandler(
        ILogger<FoundationExceptionHandler> logger,
        IRequestContextAccessor accessor)
    {
        _logger = logger;
        _accessor = accessor;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var rc = _accessor.Current;

        // ---- Client-side transport faults ----------------------------
        // A malformed body / bad Content-Type / oversized payload never
        // reached business logic: the CALLER is at fault, so this is 4xx,
        // not 500. Returning 500 here makes clients and retry middleware
        // resend a request that can never succeed.
        //
        // ASP.NET already computed the precise status on the exception
        // (400 malformed body, 413 too large, 415 unsupported media type).
        // We honour it and fall back to 400 only if it is absent or is
        // not a 4xx — an exception claiming a 5xx is still a caller
        // fault here, so we clamp it rather than propagate.
        if (exception is BadHttpRequestException badRequest)
        {
            var clientStatus = badRequest.StatusCode is >= 400 and < 500
                ? badRequest.StatusCode
                : StatusCodes.Status400BadRequest;

            // BadHttpRequestException.Message can echo the caller's payload.
            // Keep it out of logs as well as the response body.
            _logger.LogWarning(
                "Malformed request (RequestId={RequestId}, TraceId={TraceId}, Method={Method}) -> {Status}",
                rc?.RequestId,
                rc?.TraceId,
                httpContext.Request.Method,
                clientStatus);

            var clientProblem = new ProblemDetails
            {
                Type = "https://docs.gulierp.example.com/errors/malformed_request",
                Title = "Malformed Request",
                Status = clientStatus,
                // Deliberately NOT exception.Message: it can echo the
                // caller's own payload. G2-002 ban list applies here too.
                Detail = "The request could not be read. Check the body, Content-Type and Content-Length.",
            }.WithGuliErpExtensions(
                ErrorCodes.MalformedRequest,
                rc?.RequestId,
                rc?.TraceId);

            httpContext.Response.StatusCode = clientStatus;
            await httpContext.Response.WriteAsJsonAsync(
                clientProblem,
                options: null,
                contentType: "application/problem+json; charset=utf-8",
                cancellationToken).ConfigureAwait(false);

            return true;
        }

        // Server-side log: full exception, no PII filter needed beyond
        // the standard ILogger rules (no password / token by convention).
        _logger.LogError(
            exception,
            "Unhandled exception (RequestId={RequestId}, TraceId={TraceId}, Path={Path}, Method={Method})",
            rc?.RequestId,
            rc?.TraceId,
            httpContext.Request.Path.Value,
            httpContext.Request.Method);

        // Response body: safe envelope, no internals.
        var problem = new ProblemDetails
        {
            Type = "https://docs.gulierp.example.com/errors/internal_error",
            Title = "Internal Server Error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "An unexpected error occurred. The incident has been logged.",
        }.WithGuliErpExtensions(
            ErrorCodes.InternalError,
            rc?.RequestId,
            rc?.TraceId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        // RFC 9457 §3: ProblemDetails responses use media type
        // "application/problem+json". Use the WriteAsJsonAsync overload
        // that takes an explicit contentType so the default
        // "application/json" does NOT clobber it.
        await httpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json; charset=utf-8",
            cancellationToken).ConfigureAwait(false);

        return true;
    }
}
