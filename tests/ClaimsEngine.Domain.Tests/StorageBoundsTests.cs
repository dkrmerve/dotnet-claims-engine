using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>
/// The domain refuses values its storage cannot hold: amounts beyond numeric(18,2) and notes that,
/// wrapped in their audit-line prefix, would not fit <see cref="ClaimHistoryEntry.MaxNoteLength"/>.
/// </summary>
public sealed class StorageBoundsTests
{
    [Fact]
    public void Money_AboveMaxAmount_IsRejected_AtMaxAmount_IsAccepted()
    {
        Assert.Throws<ValidationException>(() => new Money(Money.MaxAmount + 0.01m));
        Assert.Equal(Money.MaxAmount, new Money(Money.MaxAmount).Amount);
    }

    [Fact]
    public void Note_TooLong_IsRejectedByEveryTransition_AndLeavesTheClaimUntouched()
    {
        var note = new string('n', Claim.MaxNoteLength + 1);
        var flagged = Fixtures.UnderReview(claimed: 19_000m);
        Assert.True(flagged.IsFlagged);

        Assert.Throws<ValidationException>(() => Fixtures.Submitted().StartReview(ActorRole.Adjuster, FakeClock.Now, note));
        Assert.Throws<ValidationException>(() => Fixtures.UnderReview().Approve(ActorRole.Manager, Fixtures.Rules, FakeClock.Now, note));
        Assert.Throws<ValidationException>(() => Fixtures.UnderReview().Reject(ActorRole.Adjuster, RejectionReason.Other, note, FakeClock.Now));
        Assert.Throws<ValidationException>(() => Fixtures.Submitted().Withdraw(ActorRole.Claimant, FakeClock.Now, note));
        Assert.Throws<ValidationException>(() => flagged.ClearFlag(ActorRole.Manager, note, FakeClock.Now));

        Assert.True(flagged.IsFlagged);
        Assert.Equal(ClaimStatus.UnderReview, flagged.Status);
    }

    [Theory]
    [InlineData(RejectionReason.InsufficientEvidence)]
    [InlineData(RejectionReason.Other)]
    public void Note_AtMaxLength_FitsTheAuditLineTogetherWithItsPrefix(RejectionReason reason)
    {
        var note = new string('n', Claim.MaxNoteLength);
        var rejected = Fixtures.UnderReview();
        var flagged = Fixtures.UnderReview(claimed: 19_000m);

        rejected.Reject(ActorRole.Adjuster, reason, note, FakeClock.Now);
        flagged.ClearFlag(ActorRole.Manager, note, FakeClock.Now);

        Assert.True(rejected.History[^1].Note!.Length <= ClaimHistoryEntry.MaxNoteLength);
        Assert.True(flagged.History[^1].Note!.Length <= ClaimHistoryEntry.MaxNoteLength);
    }

    [Fact]
    public void EveryRejectionReasonName_LeavesRoomForTheLongestNote()
    {
        var longestPrefix = Enum.GetNames<RejectionReason>().Max(name => $"Rejected ({name}): ".Length);

        Assert.True(longestPrefix + Claim.MaxNoteLength <= ClaimHistoryEntry.MaxNoteLength);
    }
}
