using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Policies;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Api.Contracts;

// Request bodies. Every field is nullable so the validators can report all missing or invalid
// fields at once; the ToCommand methods are only called after validation succeeded.
// Amounts are plain decimals in EUR; responses echo them back as { amount, currency }.

public sealed record CreatePolicyRequest(
    string? PolicyNumber,
    Guid? HolderId,
    string? CoverageType,
    decimal? CoverageLimit,
    decimal? Deductible,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo)
{
    public CreatePolicyCommand ToCommand() => new(
        PolicyNumber!,
        HolderId!.Value,
        Enum.Parse<CoverageType>(CoverageType!, ignoreCase: true),
        CoverageLimit!.Value,
        Deductible!.Value,
        EffectiveFrom!.Value,
        EffectiveTo!.Value);
}

/// <param name="IncidentDate">A date (2026-03-10) or an ISO date-time with offset (2026-03-11T01:30:00+03:00), normalised to the UTC calendar day.</param>
public sealed record SubmitClaimRequest(Guid? PolicyId, string? IncidentDate, decimal? ClaimedAmount, string? Description)
{
    public SubmitClaimCommand ToCommand(string? idempotencyKey, Actor actor) => new(
        PolicyId!.Value,
        Validation.IncidentDates.Parse(IncidentDate!),
        ClaimedAmount!.Value,
        Description!,
        idempotencyKey,
        actor);
}

public sealed record RejectClaimRequest(string? Reason, string? Note)
{
    public RejectionReason ParsedReason => Enum.Parse<RejectionReason>(Reason!, ignoreCase: true);
}

/// <summary>Optional free-text note for review / approve / withdraw.</summary>
public sealed record NoteRequest(string? Note);

/// <summary>Mandatory note for clear-flag.</summary>
public sealed record ClearFlagRequest(string? Note);

/// <summary>Query string of GET /claims. Page defaults to 1 and pageSize to 20 (max 100).</summary>
public sealed record ListClaimsRequest(Guid? PolicyId, string? Status, int? Page, int? PageSize)
{
    public const int DefaultPageSize = 20;

    public ListClaimsQuery ToQuery(Actor actor) => new(
        PolicyId,
        Status is null ? null : Enum.Parse<ClaimStatus>(Status, ignoreCase: true),
        Page ?? 1,
        PageSize ?? DefaultPageSize,
        actor);
}

/// <summary>Body of the dev-only POST /auth/token.</summary>
public sealed record DevTokenRequest(string? Subject, string? Role);
