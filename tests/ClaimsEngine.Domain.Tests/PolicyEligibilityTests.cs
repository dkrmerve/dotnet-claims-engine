using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 1: only Active policies accept claims, and only for incidents inside the effective period (422 policy_not_eligible).</summary>
public sealed class PolicyEligibilityTests
{
    [Fact]
    public void Eligibility_LapsedPolicy_IsRejectedWithPolicyNotEligible()
    {
        var policy = Fixtures.ActivePolicy();
        policy.Lapse();

        var ex = Assert.Throws<RuleViolationException>(() => Fixtures.Submitted(policy));

        Assert.Equal("policy_not_eligible", ex.Code);
        Assert.Equal(ErrorKind.RuleViolation, ex.Kind);
        Assert.Contains("Lapsed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Eligibility_CancelledPolicy_IsRejectedWithPolicyNotEligible()
    {
        var policy = Fixtures.ActivePolicy();
        policy.Cancel();

        var ex = Assert.Throws<RuleViolationException>(() => Fixtures.Submitted(policy));
        Assert.Equal("policy_not_eligible", ex.Code);
    }

    [Fact]
    public void Eligibility_IncidentBeforeEffectiveFrom_IsRejected()
    {
        var policy = Fixtures.ActivePolicy(from: new DateOnly(2026, 3, 1));

        var ex = Assert.Throws<RuleViolationException>(() => Fixtures.Submitted(policy, incidentDate: new DateOnly(2026, 2, 28)));
        Assert.Equal("policy_not_eligible", ex.Code);
        Assert.Contains("outside the coverage period", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Eligibility_IncidentAfterEffectiveTo_IsRejected()
    {
        var policy = Fixtures.ActivePolicy(to: new DateOnly(2026, 3, 1));

        var ex = Assert.Throws<RuleViolationException>(() => Fixtures.Submitted(policy, incidentDate: new DateOnly(2026, 3, 2)));
        Assert.Equal("policy_not_eligible", ex.Code);
    }

    [Fact]
    public void Eligibility_IncidentExactlyOnEffectiveFrom_IsInsidePeriod()
    {
        var policy = Fixtures.ActivePolicy(from: new DateOnly(2026, 3, 1));

        var claim = Fixtures.Submitted(policy, incidentDate: new DateOnly(2026, 3, 1));

        Assert.Equal(ClaimStatus.Submitted, claim.Status);
    }

    [Fact]
    public void Eligibility_IncidentExactlyOnEffectiveTo_IsInsidePeriod()
    {
        var policy = Fixtures.ActivePolicy(to: new DateOnly(2026, 3, 10));

        var claim = Fixtures.Submitted(policy, incidentDate: new DateOnly(2026, 3, 10));

        Assert.Equal(ClaimStatus.Submitted, claim.Status);
    }

    [Fact]
    public void Eligibility_IsEligibleFor_ReflectsStatusAndPeriod()
    {
        var policy = Fixtures.ActivePolicy();

        Assert.True(policy.IsEligibleFor(Fixtures.PolicyStart));
        Assert.False(policy.IsEligibleFor(Fixtures.PolicyStart.AddDays(-1)));

        policy.Lapse();
        Assert.False(policy.IsEligibleFor(Fixtures.PolicyStart));
    }
}
