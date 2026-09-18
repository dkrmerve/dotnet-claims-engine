namespace ClaimsEngine.Domain.Claims;

public enum ClaimStatus
{
    Submitted,
    UnderReview,
    Approved,
    Paid,
    Rejected,
    Withdrawn,
}

public enum ClaimFlag
{
    None,
    RequiresInvestigation,
}

/// <summary>Rule 9: every rejection carries one of these; <see cref="Other"/> needs a free-text note.</summary>
public enum RejectionReason
{
    LateFiling,
    BelowDeductible,
    LimitExhausted,
    NotCovered,
    Fraud,
    InsufficientEvidence,
    Other,
}

public static class ClaimStatusExtensions
{
    /// <summary>Paid, Rejected and Withdrawn are final: no transition leaves them.</summary>
    public static bool IsTerminal(this ClaimStatus status) =>
        status is ClaimStatus.Paid or ClaimStatus.Rejected or ClaimStatus.Withdrawn;
}
