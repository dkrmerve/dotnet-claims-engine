using System.Globalization;
using ClaimsEngine.Api.Errors;

namespace ClaimsEngine.Api.Validation;

/// <summary>
/// Edge validation: shape and range checks on the raw request, reported all at once as a
/// ProblemDetails "errors" dictionary keyed by field. Business invariants stay in the domain.
/// </summary>
public interface IRequestValidator<in T>
{
    ValidationErrors Validate(T request);
}

public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public ValidationErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors[field] = messages;
        }

        messages.Add(message);
        return this;
    }

    public IDictionary<string, string[]> ToDictionary() => _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
}

/// <summary>Runs the registered validator for the endpoint's <typeparamref name="T"/> argument before the handler.</summary>
public sealed class ValidationFilter<T>(bool bodyRequired = true) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.Arguments.OfType<T>().FirstOrDefault();
        if (request is null)
        {
            return bodyRequired
                ? ApiProblems.ValidationFailed(new ValidationErrors().Add("body", "A JSON request body is required."))
                : await next(context);
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<IRequestValidator<T>>();
        var errors = validator.Validate(request);
        return errors.Any ? ApiProblems.ValidationFailed(errors) : await next(context);
    }
}

public static class EnumNames
{
    /// <summary>Exact (case-insensitive) name match; numeric strings are not accepted.</summary>
    public static bool IsValid<TEnum>(string? value, params TEnum[] excluded)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>()
            .Except(excluded)
            .Any(candidate => string.Equals(candidate.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Allowed<TEnum>(params TEnum[] excluded)
        where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetValues<TEnum>().Except(excluded));
}

public static class Amounts
{
    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;
}

/// <summary>Accepts a calendar date or an ISO-8601 date-time with offset; the latter is normalised to the UTC day.</summary>
public static class IncidentDates
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mmK",
    ];

    public static bool TryParse(string? text, out DateOnly date)
    {
        var trimmed = text?.Trim();
        if (DateOnly.TryParseExact(trimmed, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        if (DateTimeOffset.TryParseExact(
                trimmed,
                DateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var instant))
        {
            date = DateOnly.FromDateTime(instant.UtcDateTime);
            return true;
        }

        date = default;
        return false;
    }

    public static DateOnly Parse(string text) =>
        TryParse(text, out var date) ? date : throw new FormatException($"'{text}' is not a valid incident date.");
}
