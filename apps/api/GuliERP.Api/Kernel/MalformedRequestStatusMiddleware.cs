using GuliERP.Foundation.Kernel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace GuliERP.Api.Kernel;

/// <summary>
/// Minimal API body binding can return an empty 400 without throwing through
/// IExceptionHandler. Give that response the same stable error envelope.
/// </summary>
public sealed class MalformedRequestStatusMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRequestContextAccessor accessor)
    {
        await next(context).ConfigureAwait(false);

        if (context.Response.StatusCode != StatusCodes.Status400BadRequest
            || context.Response.HasStarted
            || !string.IsNullOrEmpty(context.Response.ContentType)
            || context.Response.ContentLength is > 0)
            return;

        var rc = accessor.Current;
        var problem = new ProblemDetails
        {
            Type = "https://docs.gulierp.example.com/errors/malformed_request",
            Title = "Malformed Request",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request could not be read. Check the body and Content-Type.",
        }.WithGuliErpExtensions(ErrorCodes.MalformedRequest, rc?.RequestId, rc?.TraceId);

        await context.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json; charset=utf-8").ConfigureAwait(false);
    }
}
