using System.Diagnostics;
using ClaimsEngine.Api.Middleware;
using ClaimsEngine.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace ClaimsEngine.Api.Errors;

/// <summary>
/// Builds RFC 7807 problem responses. Every problem carries a stable machine-readable
/// <c>code</c> extension and a <c>type</c> URL pointing at the error catalog in the README.
/// </summary>
public static class ApiProblems
{
    public const string CodeKey = "code";
    public const string TypeBase = "https://github.com/dkrmerve/dotnet-claims-engine/docs/errors#";

    public static string TypeFor(string code) => TypeBase + code;

    public static ProblemDetails Build(int status, string code, string detail) => new()
    {
        Status = status,
        Title = ReasonPhrases.GetReasonPhrase(status),
        Detail = detail,
        Type = TypeFor(code),
        Extensions = { [CodeKey] = code },
    };

    public static IResult ValidationFailed(ValidationErrors errors) =>
        TypedResults.ValidationProblem(
            errors.ToDictionary(),
            detail: "One or more request fields are invalid; see errors.",
            title: ReasonPhrases.GetReasonPhrase(StatusCodes.Status400BadRequest),
            type: TypeFor(ErrorMapper.RequestValidationFailedCode),
            extensions: new Dictionary<string, object?> { [CodeKey] = ErrorMapper.RequestValidationFailedCode });

    /// <summary>Writes a problem from places that are not endpoints (authentication events, rate limiter).</summary>
    public static async Task WriteAsync(HttpContext http, int status, string code, string detail)
    {
        http.Response.StatusCode = status;
        var service = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await service.WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = Build(status, code, detail) });
    }

    /// <summary>
    /// Applied to every problem the framework writes, including ones it generates itself (404 for
    /// unknown routes, 405, 415): guarantees code/type and adds the correlation and trace ids.
    /// </summary>
    public static void Decorate(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        var http = context.HttpContext;

        problem.Status ??= http.Response.StatusCode;
        if (!problem.Extensions.ContainsKey(CodeKey))
        {
            var code = ErrorMapper.DefaultCodeFor(problem.Status.Value);
            problem.Extensions[CodeKey] = code;
            problem.Type = TypeFor(code);
        }

        problem.Detail ??= DefaultDetail(problem.Status.Value, http);
        problem.Instance ??= http.Request.Path;
        problem.Extensions.TryAdd("correlationId", CorrelationIdMiddleware.Get(http));
        problem.Extensions.TryAdd("traceId", Activity.Current?.Id ?? http.TraceIdentifier);

        // The exception handler clears the response before writing; keep the correlation header.
        CorrelationIdMiddleware.EnsureHeader(http);
    }

    private static string DefaultDetail(int status, HttpContext http) => status switch
    {
        StatusCodes.Status404NotFound => $"No resource matches {http.Request.Method} {http.Request.Path}.",
        StatusCodes.Status405MethodNotAllowed => $"{http.Request.Method} is not allowed on {http.Request.Path}.",
        StatusCodes.Status415UnsupportedMediaType => "The request body must be application/json.",
        _ => ReasonPhrases.GetReasonPhrase(status),
    };
}
