using System.Diagnostics;

namespace ClaimsEngine.Api.Middleware;

/// <summary>
/// One structured log line per request: method, path, status and duration. Health and metrics
/// probes are logged at Debug so they do not drown the request log.
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public static readonly PathString[] ProbePaths = ["/health", "/metrics"];

    public async Task InvokeAsync(HttpContext context)
    {
        var level = IsProbe(context.Request.Path) ? LogLevel.Debug : LogLevel.Information;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            logger.Log(
                level,
                "{Method} {Path} responded {StatusCode} in {ElapsedMs:0.0} ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                elapsed.TotalMilliseconds);
        }
    }

    private static bool IsProbe(PathString path) => ProbePaths.Any(p => path.StartsWithSegments(p));
}
