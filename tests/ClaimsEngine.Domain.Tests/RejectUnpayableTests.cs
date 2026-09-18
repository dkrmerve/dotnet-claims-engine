using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>
/// Rule 7's exit: an Approved claim the annual limit can no longer honour is closed by a Manager as
/// Approved -> Rejected (LimitExhausted); a claim that is still payable stays Approved (409 limit_not_exhausted).
/// </summary>
public sealed class RejectUnpayableTests
{
    private static readonly Money AlmostEverythingPaid = Money.Euro(18_000m);

    [Fact]
    public void RejectUnpayable_LimitExhausted_ByManager_RejectsClearsPayoutAndWritesHistory()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 20_000m, deductible: 500m);
        var claim = Fixtures.Approved(policy, claimed: 3_500m);
        var version = claim.Version;

        claim.RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, " holder informed ", policy, AlmostEverythingPaid, FakeClock.Now);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal(RejectionReason.LimitExhausted, claim.RejectionReason);
        Assert.Null(claim.ApprovedPayout);
        Assert.Equal(version + 1, claim.Version);
        Assert.Equal(ClaimStatus.Approved, claim.History[^1].FromStatus);
        Assert.Equal("Rejected (LimitExhausted): 3000.00 EUR no longer fits the annual limit. holder informed", claim.History[^1].Note);
    }

    [Fact]
    public void RejectUnpayable_WithoutNote_AndWithTheLongestNote_FitsTheAuditLine()
    {
        var policy = Fixtures.ActivePolicy();
        var plain = Fixtures.Approved(policy);
        var wordy = Fixtures.Approved(policy);

        plain.RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, null, policy, AlmostEverythingPaid, FakeClock.Now);
        wordy.RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, new string('n', Claim.MaxNoteLength), policy, AlmostEverythingPaid, FakeClock.Now);

        Assert.Equal("Rejected (LimitExhausted): 2500.00 EUR no longer fits the annual limit.", plain.History[^1].Note);
        Assert.True(wordy.History[^1].Note!.Length <= ClaimHistoryEntry.MaxNoteLength);
        var worstCase = $"Rejected (LimitExhausted): {new Money(Money.MaxAmount)} no longer fits the annual limit. ".Length + Claim.MaxNoteLength;
        Assert.True(worstCase <= ClaimHistoryEntry.MaxNoteLength);
    }

    [Fact]
    public void RejectUnpayable_PayoutStillFits_IsLimitNotExhausted_ExactlyAtTheLimitCounts()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 20_000m, deductible: 500m);
        var claim = Fixtures.Approved(policy, claimed: 3_000m);

        var ex = Assert.Throws<InvalidTransitionException>(() =>
            claim.RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, null, policy, Money.Euro(17_500m), FakeClock.Now));

        Assert.Equal("limit_not_exhausted", ex.Code);
        Assert.Equal(ErrorKind.Conflict, ex.Kind);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.NotNull(claim.ApprovedPayout);
        claim.RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, null, policy, Money.Euro(17_500.01m), FakeClock.Now);
        Assert.Equal(ClaimStatus.Rejected, claim.Status);
    }

    [Theory]
    [InlineData(ActorRole.Adjuster)]
    [InlineData(ActorRole.SeniorAdjuster)]
    [InlineData(ActorRole.Claimant)]
    [InlineData(ActorRole.System)]
    public void RejectUnpayable_ByAnyoneButAManager_IsInsufficientAuthority(ActorRole actor)
    {
        var policy = Fixtures.ActivePolicy();
        var claim = Fixtures.Approved(policy);

        Assert.Throws<InsufficientAuthorityException>(() =>
            claim.RejectUnpayable(actor, RejectionReason.LimitExhausted, null, policy, AlmostEverythingPaid, FakeClock.Now));
        Assert.Equal(ClaimStatus.Approved, claim.Status);
    }

    [Theory]
    [InlineData(RejectionReason.Fraud)]
    [InlineData(RejectionReason.Other)]
    [InlineData(RejectionReason.NotCovered)]
    public void RejectUnpayable_WithAnyOtherReason_IsInvalidTransition(RejectionReason reason)
    {
        var policy = Fixtures.ActivePolicy();
        var claim = Fixtures.Approved(policy);

        var ex = Assert.Throws<InvalidTransitionException>(() =>
            claim.RejectUnpayable(ActorRole.Manager, reason, "because", policy, AlmostEverythingPaid, FakeClock.Now));

        Assert.Equal("invalid_transition", ex.Code);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
    }

    [Fact]
    public void RejectUnpayable_FromAnyOtherStatus_ForeignPolicy_OrOverlongNote_IsRefused()
    {
        var policy = Fixtures.ActivePolicy();

        Assert.Throws<InvalidTransitionException>(() =>
            Fixtures.UnderReview(policy).RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, null, policy, AlmostEverythingPaid, FakeClock.Now));
        Assert.Throws<InvalidTransitionException>(() =>
            Fixtures.Paid(policy).RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, null, policy, AlmostEverythingPaid, FakeClock.Now));
        Assert.Throws<ValidationException>(() =>
            Fixtures.Approved(policy).RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, null, Fixtures.ActivePolicy(), AlmostEverythingPaid, FakeClock.Now));
        Assert.Throws<ValidationException>(() =>
            Fixtures.Approved(policy).RejectUnpayable(ActorRole.Manager, RejectionReason.LimitExhausted, new string('n', Claim.MaxNoteLength + 1), policy, AlmostEverythingPaid, FakeClock.Now));
    }

    [Fact]
    public void Reject_OrdinaryRejectionOfAnApprovedClaim_StaysInvalid()
    {
        var claim = Fixtures.Approved();

        Assert.Throws<InvalidTransitionException>(() => claim.Reject(ActorRole.Manager, RejectionReason.LimitExhausted, null, FakeClock.Now));
        Assert.Equal(ClaimStatus.Approved, claim.Status);
    }
}
