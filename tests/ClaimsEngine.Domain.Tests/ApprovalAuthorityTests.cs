using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 5: approval authority by payout amount; limits are inclusive (403 insufficient_authority).</summary>
public sealed class ApprovalAuthorityTests
{
    [Theory]
    [InlineData(4_999.99, ActorRole.Adjuster, true)]
    [InlineData(5_000.00, ActorRole.Adjuster, true)]
    [InlineData(5_000.01, ActorRole.Adjuster, false)]
    [InlineData(5_000.01, ActorRole.SeniorAdjuster, true)]
    [InlineData(50_000.00, ActorRole.SeniorAdjuster, true)]
    [InlineData(50_000.01, ActorRole.SeniorAdjuster, false)]
    [InlineData(50_000.01, ActorRole.Manager, true)]
    [InlineData(1_000_000.00, ActorRole.Manager, true)]
    [InlineData(1.00, ActorRole.Claimant, false)]
    [InlineData(1.00, ActorRole.System, false)]
    public void Authority_ThresholdBoundaries(decimal payout, ActorRole role, bool expected)
    {
        Assert.Equal(expected, ApprovalAuthority.CanApprove(role, Money.Euro(payout), Fixtures.Rules));
    }

    [Theory]
    [InlineData(5_000.00, "Adjuster, SeniorAdjuster or Manager")]
    [InlineData(5_000.01, "SeniorAdjuster or Manager")]
    [InlineData(50_000.01, "Manager")]
    public void Authority_DescribeRequiredRoles(decimal payout, string expected)
    {
        Assert.Equal(expected, ApprovalAuthority.DescribeRequiredRoles(Money.Euro(payout), Fixtures.Rules));
    }

    [Fact]
    public void Authority_AdjusterApproving5000Point01_IsInsufficientAuthority()
    {
        // claimed 5 500.01 - deductible 500 = payout 5 000.01
        var claim = Fixtures.UnderReview(Fixtures.ActivePolicy(deductible: 500m), claimed: 5_500.01m);

        var ex = Assert.Throws<InsufficientAuthorityException>(() => claim.Approve(ActorRole.Adjuster, Fixtures.Rules, FakeClock.Now));

        Assert.Equal("insufficient_authority", ex.Code);
        Assert.Equal(ErrorKind.Forbidden, ex.Kind);
        Assert.Contains("SeniorAdjuster or Manager", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ClaimStatus.UnderReview, claim.Status);
    }

    [Fact]
    public void Authority_AdjusterApprovingExactly5000_IsAllowed()
    {
        var claim = Fixtures.UnderReview(Fixtures.ActivePolicy(deductible: 500m), claimed: 5_500m);
        claim.Approve(ActorRole.Adjuster, Fixtures.Rules, FakeClock.Now);
        Assert.Equal(Money.Euro(5_000m), claim.ApprovedPayout);
    }

    [Fact]
    public void Authority_SeniorAdjusterApproving50000Point01_IsInsufficientAuthority()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 1_000_000m, deductible: 0m);
        var claim = Fixtures.UnderReview(policy, claimed: 50_000.01m);

        Assert.Throws<InsufficientAuthorityException>(() => claim.Approve(ActorRole.SeniorAdjuster, Fixtures.Rules, FakeClock.Now));
        claim.Approve(ActorRole.Manager, Fixtures.Rules, FakeClock.Now);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
    }

    [Fact]
    public void Authority_ClaimantCannotReviewRejectOrPay()
    {
        var policy = Fixtures.ActivePolicy();

        Assert.Throws<InsufficientAuthorityException>(() => Fixtures.Submitted(policy).StartReview(ActorRole.Claimant, FakeClock.Now));
        Assert.Throws<InsufficientAuthorityException>(() => Fixtures.Submitted(policy).Reject(ActorRole.Claimant, RejectionReason.Fraud, null, FakeClock.Now));
        Assert.Throws<InsufficientAuthorityException>(() => Fixtures.Approved(policy).Pay(ActorRole.Claimant, policy, Money.Zero, FakeClock.Now));
    }

    [Fact]
    public void Authority_OnlyClaimantCanWithdraw()
    {
        var ex = Assert.Throws<InsufficientAuthorityException>(() => Fixtures.Submitted().Withdraw(ActorRole.Manager, FakeClock.Now));
        Assert.Equal("insufficient_authority", ex.Code);
    }

    [Fact]
    public void Authority_TransitionIsCheckedBeforeAuthority()
    {
        // A closed claim answers 409 even to an unauthorised actor: state first, then rights.
        var claim = Fixtures.Approved();
        Assert.Throws<InvalidTransitionException>(() => claim.StartReview(ActorRole.Claimant, FakeClock.Now));
    }
}
