using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Claims;

/// <summary>
/// The tunable numbers behind the business rules. Immutable and validated once, so a
/// misconfigured deployment fails at startup rather than during a claim.
/// </summary>
public sealed class ClaimRules
{
    public static readonly ClaimRules Default = new(
        filingWindowDays: 30,
        frequentClaimantThreshold: 3,
        frequentClaimantWindowDays: 365,
        highValueShareOfLimit: 0.80m,
        reviewSlaDays: 14,
        adjusterApprovalLimit: Money.Euro(5_000m),
        seniorAdjusterApprovalLimit: Money.Euro(50_000m));

    public ClaimRules(
        int filingWindowDays,
        int frequentClaimantThreshold,
        int frequentClaimantWindowDays,
        decimal highValueShareOfLimit,
        int reviewSlaDays,
        Money adjusterApprovalLimit,
        Money seniorAdjusterApprovalLimit)
    {
        if (filingWindowDays <= 0)
        {
            throw new ValidationException("filingWindowDays must be positive.");
        }

        if (frequentClaimantThreshold <= 0)
        {
            throw new ValidationException("frequentClaimantThreshold must be positive.");
        }

        if (frequentClaimantWindowDays <= 0)
        {
            throw new ValidationException("frequentClaimantWindowDays must be positive.");
        }

        if (highValueShareOfLimit is <= 0m or > 1m)
        {
            throw new ValidationException("highValueShareOfLimit must be within (0, 1].");
        }

        if (reviewSlaDays <= 0)
        {
            throw new ValidationException("reviewSlaDays must be positive.");
        }

        if (seniorAdjusterApprovalLimit < adjusterApprovalLimit)
        {
            throw new ValidationException("seniorAdjusterApprovalLimit must be at least adjusterApprovalLimit.");
        }

        FilingWindowDays = filingWindowDays;
        FrequentClaimantThreshold = frequentClaimantThreshold;
        FrequentClaimantWindowDays = frequentClaimantWindowDays;
        HighValueShareOfLimit = highValueShareOfLimit;
        ReviewSlaDays = reviewSlaDays;
        AdjusterApprovalLimit = adjusterApprovalLimit;
        SeniorAdjusterApprovalLimit = seniorAdjusterApprovalLimit;
    }

    /// <summary>Rule 2: a claim filed more than this many days after the incident is rejected as LateFiling.</summary>
    public int FilingWindowDays { get; }

    /// <summary>Rule 6: this many other claims by the same holder inside the window trigger an investigation flag.</summary>
    public int FrequentClaimantThreshold { get; }

    /// <summary>Rule 6: look-back window, in days, for counting the holder's other claims.</summary>
    public int FrequentClaimantWindowDays { get; }

    /// <summary>Rule 6: claims at or above this share of the coverage limit trigger an investigation flag.</summary>
    public decimal HighValueShareOfLimit { get; }

    /// <summary>Rule 8: claims under review for longer than this are overdue.</summary>
    public int ReviewSlaDays { get; }

    /// <summary>Rule 5: an Adjuster may approve payouts up to and including this amount.</summary>
    public Money AdjusterApprovalLimit { get; }

    /// <summary>Rule 5: a SeniorAdjuster may approve payouts up to and including this amount.</summary>
    public Money SeniorAdjusterApprovalLimit { get; }

    public TimeSpan FilingWindow => TimeSpan.FromDays(FilingWindowDays);

    public TimeSpan FrequentClaimantWindow => TimeSpan.FromDays(FrequentClaimantWindowDays);

    public TimeSpan ReviewSla => TimeSpan.FromDays(ReviewSlaDays);
}
