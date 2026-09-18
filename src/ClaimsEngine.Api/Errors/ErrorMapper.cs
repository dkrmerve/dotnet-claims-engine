using System.Text.Json;
using ClaimsEngine.Domain.Exceptions;

namespace ClaimsEngine.Api.Errors;

/// <param name="IsUnexpected">True for 500s: the exception is logged at error level and its message is not exposed.</param>
public readonly record struct MappedError(int Status, string Code, string Detail, bool IsUnexpected);

/// <summary>The single exception-to-HTTP mapping table. See the README error catalog.</summary>
public static class ErrorMapper
{
    public const string RequestValidationFailedCode = "request_validation_failed";
    public const string InvalidRequestCode = "invalid_request";
    public const string PayloadTooLargeCode = "payload_too_large";
    public const string UnauthenticatedCode = "unauthenticated";
    public const string ForbiddenRoleCode = "forbidden_role";
    public const string RateLimitedCode = "rate_limited";
    public const string InternalErrorCode = "internal_error";

    public static MappedError Map(Exception exception) => exception switch
    {
        DomainException domain => new MappedError(StatusFor(domain.Kind), domain.Code, domain.Message, IsUnexpected: false),
        BadHttpRequestException bad => new MappedError(
            bad.StatusCode,
            bad.StatusCode == StatusCodes.Status413PayloadTooLarge ? PayloadTooLargeCode : InvalidRequestCode,
            Describe(bad),
            IsUnexpected: false),
        _ => new MappedError(
            StatusCodes.Status500InternalServerError,
            InternalErrorCode,
            "An unexpected error occurred. It was logged under the returned correlationId.",
            IsUnexpected: true),
    };

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.RuleViolation => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Code for problems the framework produced without one (unknown route, wrong method, ...).</summary>
    public static string DefaultCodeFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => InvalidRequestCode,
        StatusCodes.Status401Unauthorized => UnauthenticatedCode,
        StatusCodes.Status403Forbidden => ForbiddenRoleCode,
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status413PayloadTooLarge => PayloadTooLargeCode,
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        StatusCodes.Status429TooManyRequests => RateLimitedCode,
        >= 500 => InternalErrorCode,
        _ => $"http_{status}",
    };

    private static string Describe(BadHttpRequestException exception) =>
        exception.InnerException is JsonException json ? $"{exception.Message} {json.Message}" : exception.Message;
}
