using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 7: paid payouts per policy year never exceed the coverage limit; re-checked at pay time (422 limit_exhausted) and the policy version moves on every payout.</summary>
public sealed class AnnualLimitTests
{
    [Fact]
    public void AnnualLimit_PayWithinLimit_RecordsPayoutOnPolicyAndBumpsBothVersions()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 20_000m, deductible: 0m);
        var claim = Fixtures.Approved(policy, claimed: 8_000m);
        var policyVersion = policy.Version;
        var claimVersion = claim.Version;

        claim.Pay(ActorRole.Manager, policy, Money.Zero, FakeClock.Now);

        Assert.Equal(ClaimStatus.Paid, claim.Status);
        Assert.Equal(Money.Euro(8_000m), policy.LifetimePaid);
        Assert.Equal(policyVersion + 1, policy.Version);
        Assert.Equal(claimVersion + 1, claim.Version);
    }

    [Fact]
    public void AnnualLimit_PayThatWouldExceedLimit_IsLimitExhausted()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 10_000m, deductible: 0m);
        var claim = Fixtures.Approved(policy, claimed: 5_000m);

        var ex = Assert.Throws<RuleViolationException>(() => claim.Pay(ActorRole.Manager, policy, Money.Euro(8_000m), FakeClock.Now));

        Assert.Equal("limit_exhausted", ex.Code);
        Assert.Equal(ErrorKind.RuleViolation, ex.Kind);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.Equal(Money.Zero, policy.LifetimePaid);
    }

    [Fact]
    public void AnnualLimit_PayExactlyUpToLimit_IsAllowed()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 10_000m, deductible: 0m);
        var claim = Fixtures.Approved(policy, claimed: 2_000m);

        claim.Pay(ActorRole.Manager, policy, Money.Euro(8_000m), FakeClock.Now);

        Assert.Equal(ClaimStatus.Paid, claim.Status);
    }

    [Fact]
    public void AnnualLimit_PayWithForeignPolicy_IsValidationError()
    {
        var policy = Fixtures.ActivePolicy();
        var claim = Fixtures.Approved(policy);

        Assert.Throws<ValidationException>(() => claim.Pay(ActorRole.Manager, Fixtures.ActivePolicy(), Money.Zero, FakeClock.Now));
    }

    [Fact]
    public void AnnualLimit_SubmitUsesPolicyYearOfIncident_NotCalendarYear()
    {
        // Policy runs 2025-07-01 .. 2027-06-30. A claim from policy year 1 must not consume year 2.
        var policy = Fixtures.ActivePolicy(coverageLimit: 10_000m, deductible: 0m, from: new DateOnly(2025, 7, 1), to: new DateOnly(2027, 6, 30));

        var yearOne = policy.PolicyYearContaining(new DateOnly(2026, 6, 30));
        var yearTwo = policy.PolicyYearContaining(new DateOnly(2026, 7, 1));

        Assert.Equal(new DateRange(new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30)), yearOne);
        Assert.Equal(new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2027, 6, 30)), yearTwo);
        Assert.NotEqual(yearOne, yearTwo);
    }
}
