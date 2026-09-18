using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Tests.Support;

internal static class Fixtures
{
    public static readonly DateOnly PolicyStart = new(2026, 1, 1);
    public static readonly DateOnly PolicyEnd = new(2026, 12, 31);
    public static readonly ClaimRules Rules = ClaimRules.Default;

    public static Policy ActivePolicy(
        decimal coverageLimit = 20_000m,
        decimal deductible = 500m,
        DateOnly? from = null,
        DateOnly? to = null,
        HolderId? holder = null,
        CoverageType type = CoverageType.Auto) =>
        Policy.Create(
            PolicyId.New(),
            "POL-" + Guid.NewGuid().ToString("N")[..8],
            holder ?? HolderId.New(),
            type,
            Money.Euro(coverageLimit),
            Money.Euro(deductible),
            from ?? PolicyStart,
            to ?? PolicyEnd);

    public static Claim Submitted(
        Policy? policy = null,
        decimal claimed = 3_000m,
        DateOnly? incidentDate = null,
        ClaimSubmissionContext? context = null,
        DateTimeOffset? filedAt = null,
        string description = "Rear-ended at a traffic light.",
        ClaimRules? rules = null) =>
        Claim.Submit(
            ClaimId.New(),
            policy ?? ActivePolicy(),
            incidentDate ?? FakeClock.Today.AddDays(-5),
            Money.Euro(claimed),
            description,
            context ?? ClaimSubmissionContext.Clean,
            rules ?? Rules,
            filedAt ?? FakeClock.Now);

    public static Claim UnderReview(Policy? policy = null, decimal claimed = 3_000m, ClaimSubmissionContext? context = null)
    {
        var claim = Submitted(policy, claimed, context: context);
        claim.StartReview(ActorRole.Adjuster, FakeClock.Now);
        return claim;
    }

    public static Claim Approved(Policy? policy = null, decimal claimed = 3_000m, ActorRole approver = ActorRole.Manager)
    {
        var claim = UnderReview(policy, claimed);
        claim.Approve(approver, Rules, FakeClock.Now);
        return claim;
    }

    public static Claim Paid(Policy policy, decimal claimed = 3_000m, Money? alreadyPaid = null)
    {
        var claim = Approved(policy, claimed);
        claim.Pay(ActorRole.Manager, policy, alreadyPaid ?? Money.Zero, FakeClock.Now);
        return claim;
    }
}
