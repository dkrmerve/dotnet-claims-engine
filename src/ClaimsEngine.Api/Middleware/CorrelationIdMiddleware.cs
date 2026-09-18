namespace ClaimsEngine.Api.Middleware;

/// <summary>
/// Reads <c>X-Correlation-Id</c> (or generates one), echoes it on the response and puts it in
/// the logging scope so every log line of the request carries it.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const int MaxLength = 64;

    private const string ItemKey = "ClaimsEngine.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString().Trim();
        var correlationId = IsAcceptable(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        EnsureHeader(context);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context) => context.Items[ItemKey] as string ?? context.TraceIdentifier;

    public static void EnsureHeader(HttpContext context)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.Headers[HeaderName] = Get(context);
        }
    }

    private static bool IsAcceptable(string value) =>
        value.Length is > 0 and <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
