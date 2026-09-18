using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Claims;

/// <summary>
/// Facts about the wider portfolio that the domain needs when a claim is submitted but
/// cannot look up itself (it has no repository access).
/// </summary>
/// <param name="AlreadyPaidInPolicyYear">Sum of payouts already made on the policy in the policy year of the incident.</param>
/// <param name="OtherClaimsByHolderInWindow">Number of other, non-withdrawn claims by the same holder in the last 365 days.</param>
public readonly record struct ClaimSubmissionContext(Money AlreadyPaidInPolicyYear, int OtherClaimsByHolderInWindow)
{
    public static ClaimSubmissionContext Clean => new(Money.Zero, 0);
}
