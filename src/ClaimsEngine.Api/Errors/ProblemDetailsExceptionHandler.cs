using ClaimsEngine.Api.Middleware;
using Microsoft.AspNetCore.Diagnostics;

namespace ClaimsEngine.Api.Errors;

/// <summary>
/// Turns every exception into a ProblemDetails response through <see cref="ErrorMapper"/>.
/// Unexpected exceptions become a generic 500 (no message, no stack trace) and are logged at
/// error level together with the correlation id.
/// </summary>
public sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var error = ErrorMapper.Map(exception);
        var correlationId = CorrelationIdMiddleware.Get(httpContext);

        if (error.IsUnexpected)
        {
            logger.LogError(
                exception,
                "Unhandled {ExceptionType} for {Method} {Path}; correlationId={CorrelationId}",
                exception.GetType().Name,
                httpContext.Request.Method,
                httpContext.Request.Path,
                correlationId);
        }
        else
        {
            logger.LogInformation(
                "{Method} {Path} -> {Status} {Code}; correlationId={CorrelationId}; {Detail}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                error.Status,
                error.Code,
                correlationId,
                error.Detail);
        }

        httpContext.Response.StatusCode = error.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = ApiProblems.Build(error.Status, error.Code, error.Detail),
        });
    }
}
