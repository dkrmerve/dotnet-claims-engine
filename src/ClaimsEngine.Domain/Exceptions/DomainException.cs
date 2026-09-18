namespace ClaimsEngine.Domain.Exceptions;

/// <summary>
/// Categorises a failure so the API layer can pick an HTTP status without the domain
/// knowing anything about HTTP. The mapping table lives in one place: ErrorMapper in the API.
/// </summary>
public enum ErrorKind
{
    /// <summary>Malformed or nonsensical input (400).</summary>
    Validation,
    /// <summary>No usable identity was supplied (401).</summary>
    Unauthenticated,
    /// <summary>The actor is known but not allowed to do this (403).</summary>
    Forbidden,
    /// <summary>The referenced aggregate does not exist (404).</summary>
    NotFound,
    /// <summary>The request conflicts with the current state of the resource (409).</summary>
    Conflict,
    /// <summary>Well-formed request that a business rule refuses (422).</summary>
    RuleViolation,
}

/// <summary>Base class of every failure raised by the domain and application layers.</summary>
public abstract class DomainException(string code, ErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Stable, machine-readable, snake_case code such as <c>invalid_transition</c>.</summary>
    public string Code { get; } = code;

    public ErrorKind Kind { get; } = kind;
}

/// <summary>A domain invariant on the input was violated (400).</summary>
public sealed class ValidationException(string message, string code = ValidationException.DefaultCode)
    : DomainException(code, ErrorKind.Validation, message)
{
    public const string DefaultCode = "validation_error";
}

/// <summary>The aggregate does not exist (404).</summary>
public sealed class NotFoundException(string resource, object id)
    : DomainException("not_found", ErrorKind.NotFound, $"{resource} '{id}' was not found.");

/// <summary>The claim's state machine does not allow the requested move (409).</summary>
public sealed class InvalidTransitionException(string message, string code = InvalidTransitionException.DefaultCode)
    : DomainException(code, ErrorKind.Conflict, message)
{
    public const string DefaultCode = "invalid_transition";
}

/// <summary>Somebody else changed the row between load and save (409).</summary>
public sealed class ConcurrencyException(Exception? innerException = null)
    : DomainException(
        "concurrency_conflict",
        ErrorKind.Conflict,
        "The record was modified by another request. Reload and retry.",
        innerException);

/// <summary>A unique constraint rejected the write (409). The submit path uses it to resolve idempotency races.</summary>
public sealed class DuplicateKeyException(string message, Exception? innerException = null)
    : DomainException("duplicate_key", ErrorKind.Conflict, message, innerException);

/// <summary>A business rule refused a well-formed request (422). The code names the rule.</summary>
public sealed class RuleViolationException(string code, string message)
    : DomainException(code, ErrorKind.RuleViolation, message);

/// <summary>The actor is authenticated but lacks the authority for this action (403).</summary>
public sealed class InsufficientAuthorityException(string message)
    : DomainException("insufficient_authority", ErrorKind.Forbidden, message);

/// <summary>No (valid) actor identity was supplied (401).</summary>
public sealed class UnauthenticatedException(string message)
    : DomainException("unauthenticated", ErrorKind.Unauthenticated, message);

/// <summary>Codes used with <see cref="RuleViolationException"/>, <see cref="InvalidTransitionException"/> and <see cref="ValidationException"/>.</summary>
public static class ErrorCodes
{
    public const string PolicyNotEligible = "policy_not_eligible";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string LimitExhausted = "limit_exhausted";
    public const string LimitNotExhausted = "limit_not_exhausted";
    public const string InvestigationPending = "investigation_pending";
    public const string FlagNotSet = "flag_not_set";
    public const string RejectionNoteRequired = "rejection_note_required";
    public const string NoteRequired = "note_required";
}

/// <summary>A Claimant tried to reach a policy or claim that belongs to another holder (403).</summary>
public sealed class NotOwnerException(string message)
    : DomainException("not_owner", ErrorKind.Forbidden, message);
