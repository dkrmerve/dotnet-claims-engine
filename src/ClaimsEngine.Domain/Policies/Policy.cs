using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Policies;

public enum CoverageType
{
    Auto,
    Home,
    Health,
}

public enum PolicyStatus
{
    Active,
    Lapsed,
    Cancelled,
}

/// <summary>
/// An insurance policy: what is covered, for how much, and during which period.
/// Claims are always filed against exactly one policy.
/// </summary>
public sealed class Policy
{
    // EF Core materialisation.
    private Policy()
    {
    }

    public PolicyId Id { get; private set; }

    public string PolicyNumber { get; private set; } = default!;

    public HolderId HolderId { get; private set; }

    public CoverageType CoverageType { get; private set; }

    /// <summary>Maximum total payout per policy year.</summary>
    public Money CoverageLimit { get; private set; }

    /// <summary>Amount the claimant carries themselves on every claim.</summary>
    public Money Deductible { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public DateOnly EffectiveTo { get; private set; }

    public PolicyStatus Status { get; private set; }

    /// <summary>Sum of everything ever paid out on this policy (informational).</summary>
    public Money LifetimePaid { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token. Bumped on every payout so that two payments racing
    /// against the same annual limit cannot both succeed.
    /// </summary>
    public long Version { get; private set; }

    public static Policy Create(
        PolicyId id,
        string policyNumber,
        HolderId holderId,
        CoverageType coverageType,
        Money coverageLimit,
        Money deductible,
        DateOnly effectiveFrom,
        DateOnly effectiveTo)
    {
        if (string.IsNullOrWhiteSpace(policyNumber))
        {
            throw new ValidationException("Policy number is required.");
        }

        if (coverageLimit.IsZero)
        {
            throw new ValidationException("Coverage limit must be greater than zero.");
        }

        if (deductible >= coverageLimit)
        {
            throw new ValidationException(
                $"Deductible ({deductible}) must be lower than the coverage limit ({coverageLimit}).");
        }

        if (effectiveTo <= effectiveFrom)
        {
            throw new ValidationException(
                $"effectiveTo ({effectiveTo:yyyy-MM-dd}) must be after effectiveFrom ({effectiveFrom:yyyy-MM-dd}).");
        }

        return new Policy
        {
            Id = id,
            PolicyNumber = policyNumber.Trim(),
            HolderId = holderId,
            CoverageType = coverageType,
            CoverageLimit = coverageLimit,
            Deductible = deductible,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
            Status = PolicyStatus.Active,
            LifetimePaid = Money.Zero,
            Version = 1,
        };
    }

    public DateRange CoveragePeriod => new(EffectiveFrom, EffectiveTo);

    /// <summary>Rule 1: only active policies cover incidents inside their effective period.</summary>
    public bool IsEligibleFor(DateOnly incidentDate) =>
        Status == PolicyStatus.Active && CoveragePeriod.Contains(incidentDate);

    public void EnsureEligibleFor(DateOnly incidentDate)
    {
        if (Status != PolicyStatus.Active)
        {
            throw new RuleViolationException(ErrorCodes.PolicyNotEligible, $"Policy {PolicyNumber} is {Status}; only Active policies accept claims.");
        }

        if (!CoveragePeriod.Contains(incidentDate))
        {
            throw new RuleViolationException(ErrorCodes.PolicyNotEligible,
                $"Incident date {incidentDate:yyyy-MM-dd} is outside the coverage period " +
                $"{EffectiveFrom:yyyy-MM-dd}..{EffectiveTo:yyyy-MM-dd} of policy {PolicyNumber}.");
        }
    }

    /// <summary>
    /// The policy year (12 months starting on an anniversary of <see cref="EffectiveFrom"/>) that
    /// contains <paramref name="date"/>. Annual limits are tracked per policy year.
    /// </summary>
    public DateRange PolicyYearContaining(DateOnly date)
    {
        if (!CoveragePeriod.Contains(date))
        {
            throw new ValidationException($"{date:yyyy-MM-dd} is not inside the coverage period of policy {PolicyNumber}.");
        }

        var start = EffectiveFrom;
        while (start.AddYears(1) <= date)
        {
            start = start.AddYears(1);
        }

        var end = start.AddYears(1).AddDays(-1);
        return new DateRange(start, end < EffectiveTo ? end : EffectiveTo);
    }

    public void Lapse()
    {
        if (Status != PolicyStatus.Active)
        {
            throw new InvalidTransitionException($"Policy {PolicyNumber} is {Status} and cannot lapse.");
        }

        Status = PolicyStatus.Lapsed;
        Version++;
    }

    public void Cancel()
    {
        if (Status == PolicyStatus.Cancelled)
        {
            throw new InvalidTransitionException($"Policy {PolicyNumber} is already cancelled.");
        }

        Status = PolicyStatus.Cancelled;
        Version++;
    }

    /// <summary>
    /// Called when a claim is paid. Bumping <see cref="Version"/> turns the policy into a
    /// serialisation point for payouts: concurrent payments conflict instead of both committing.
    /// </summary>
    public void RecordPayout(Money payout)
    {
        LifetimePaid += payout;
        Version++;
    }
}
