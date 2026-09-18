namespace ClaimsEngine.Api.Middleware;

/// <summary>
/// Defensive response headers for a JSON API. The Content-Security-Policy is skipped for the
/// interactive docs page, which needs to run its own scripts.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string DocsPath = "/docs";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cache-Control"] = "no-store";

            if (!context.Request.Path.StartsWithSegments(DocsPath))
            {
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
