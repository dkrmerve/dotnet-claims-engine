using ClaimsEngine.Api.Errors;
using Microsoft.AspNetCore.Http.Features;

namespace ClaimsEngine.Api.Middleware;

/// <summary>
/// Caps the request body. Kestrel enforces the same limit on the wire; this middleware makes the
/// limit independent of the server (it also holds behind the in-memory test server) and answers
/// with a ProblemDetails 413 instead of a bare connection reset.
/// </summary>
public sealed class RequestBodyLimitMiddleware(RequestDelegate next, long maxBytes)
{
    public const long DefaultMaxBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
        }

        if (context.Request.ContentLength > maxBytes)
        {
            await ApiProblems.WriteAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                ErrorMapper.PayloadTooLargeCode,
                $"The request body must not exceed {maxBytes} bytes.");
            return;
        }

        await next(context);
    }
}
