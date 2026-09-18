using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Tests.Support;

/// <summary>One wired-up set of fakes per test.</summary>
internal sealed class Scenario
{
    public FakeClock Clock { get; } = new();

    public FakePolicyRepository Policies { get; } = new();

    public FakeClaimRepository Claims { get; }

    public FakeIdempotencyStore Idempotency { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public ClaimRules Rules { get; } = ClaimRules.Default;

    public IdempotencySettings IdempotencySettings { get; } = IdempotencySettings.Default;

    public Scenario()
    {
        Claims = new FakeClaimRepository(Policies);
    }

    public static Actor Manager => new("manager-1", ActorRole.Manager);

    public static Actor Adjuster => new("adjuster-1", ActorRole.Adjuster);

    public static Actor ClaimantFor(Policy policy) => new(policy.HolderId.Value.ToString(), ActorRole.Claimant);

    public static Actor Stranger => new(Guid.NewGuid().ToString(), ActorRole.Claimant);

    public Policy AddPolicy(decimal coverageLimit = 20_000m, decimal deductible = 500m, DateOnly? from = null, DateOnly? to = null, HolderId? holder = null)
    {
        var policy = Policy.Create(
            PolicyId.New(),
            "POL-" + Guid.NewGuid().ToString("N")[..8],
            holder ?? HolderId.New(),
            CoverageType.Auto,
            Money.Euro(coverageLimit),
            Money.Euro(deductible),
            from ?? new DateOnly(2026, 1, 1),
            to ?? new DateOnly(2026, 12, 31));
        Policies.Policies[policy.Id] = policy;
        return policy;
    }

    public Claim AddClaim(Policy policy, decimal claimed = 3_000m, DateOnly? incidentDate = null, ClaimStatus status = ClaimStatus.Submitted, DateTimeOffset? filedAt = null)
    {
        var claim = Claim.Submit(
            ClaimId.New(),
            policy,
            incidentDate ?? Clock.Today.AddDays(-5),
            Money.Euro(claimed),
            "Test claim",
            ClaimSubmissionContext.Clean,
            Rules,
            filedAt ?? Clock.UtcNow);

        if (status is ClaimStatus.UnderReview or ClaimStatus.Approved or ClaimStatus.Paid)
        {
            claim.StartReview(ActorRole.Adjuster, Clock.UtcNow);
        }

        if (status is ClaimStatus.Approved or ClaimStatus.Paid)
        {
            claim.Approve(ActorRole.Manager, Rules, Clock.UtcNow);
        }

        if (status is ClaimStatus.Paid)
        {
            claim.Pay(ActorRole.Manager, policy, Money.Zero, Clock.UtcNow);
        }

        if (status is ClaimStatus.Withdrawn)
        {
            claim.Withdraw(ActorRole.Claimant, Clock.UtcNow);
        }

        Claims.Claims.Add(claim);
        return claim;
    }

    public SubmitClaimHandler SubmitHandler() => new(Policies, Claims, Idempotency, UnitOfWork, Clock, Rules, IdempotencySettings);

    public SubmitClaimCommand SubmitCommand(Policy policy, decimal amount = 3_000m, string? key = null, Actor? actor = null, string description = "Rear-ended at a traffic light.") =>
        new(policy.Id.Value, Clock.Today.AddDays(-5), amount, description, key, actor ?? ClaimantFor(policy));
}
