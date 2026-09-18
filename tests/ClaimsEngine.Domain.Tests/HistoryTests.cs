using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rules 8 and 11: review start is timestamped for the SLA, and every transition appends an audit entry.</summary>
public sealed class HistoryTests
{
    [Fact]
    public void History_FullLifecycle_HasOneEntryPerTransitionWithFromAndTo()
    {
        var policy = Fixtures.ActivePolicy();
        var claim = Fixtures.Submitted(policy);
        claim.StartReview(ActorRole.Adjuster, FakeClock.Plus(TimeSpan.FromHours(1)), "assigned to me");
        claim.Approve(ActorRole.Manager, Fixtures.Rules, FakeClock.Plus(TimeSpan.FromHours(2)));
        claim.Pay(ActorRole.Manager, policy, Money.Zero, FakeClock.Plus(TimeSpan.FromHours(3)));

        var expected = new (ClaimStatus? From, ClaimStatus To, ActorRole Actor)[]
        {
            (null, ClaimStatus.Submitted, ActorRole.Claimant),
            (ClaimStatus.Submitted, ClaimStatus.UnderReview, ActorRole.Adjuster),
            (ClaimStatus.UnderReview, ClaimStatus.Approved, ActorRole.Manager),
            (ClaimStatus.Approved, ClaimStatus.Paid, ActorRole.Manager),
        };

        Assert.Equal(expected.Length, claim.History.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].From, claim.History[i].FromStatus);
            Assert.Equal(expected[i].To, claim.History[i].ToStatus);
            Assert.Equal(expected[i].Actor, claim.History[i].ActorRole);
            Assert.False(string.IsNullOrWhiteSpace(claim.History[i].Note));
        }

        Assert.Equal("assigned to me", claim.History[1].Note);
        Assert.Equal(FakeClock.Plus(TimeSpan.FromHours(1)), claim.History[1].At);
        Assert.True(claim.History.Select(h => h.At).SequenceEqual(claim.History.Select(h => h.At).Order()));
    }

    [Fact]
    public void ReviewSla_StartReview_RecordsTheInstantTheClaimEnteredUnderReview()
    {
        var claim = Fixtures.Submitted();
        var reviewAt = FakeClock.Plus(TimeSpan.FromDays(2));

        claim.StartReview(ActorRole.Adjuster, reviewAt);

        Assert.Equal(reviewAt, claim.ReviewStartedAt);
        Assert.Equal(reviewAt, claim.History.Single(h => h.ToStatus == ClaimStatus.UnderReview).At);
        Assert.Equal(TimeSpan.FromDays(14), Fixtures.Rules.ReviewSla);
    }

    [Fact]
    public void History_SubmissionEntry_HasNoFromStatus()
    {
        var entry = Fixtures.Submitted().History.Single();

        Assert.Null(entry.FromStatus);
        Assert.Equal(ClaimStatus.Submitted, entry.ToStatus);
        Assert.Equal("Claim submitted.", entry.Note);
        Assert.Equal(FakeClock.Now, entry.At);
    }
}
