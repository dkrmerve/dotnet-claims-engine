using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 9: a rejection always carries a reason; Other requires a note (400 rejection_note_required).</summary>
public sealed class RejectionReasonTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Rejection_OtherWithoutNote_IsRejectionNoteRequired(string? note)
    {
        var claim = Fixtures.UnderReview();

        var ex = Assert.Throws<ValidationException>(() => claim.Reject(ActorRole.Adjuster, RejectionReason.Other, note, FakeClock.Now));

        Assert.Equal("rejection_note_required", ex.Code);
        Assert.Equal(ErrorKind.Validation, ex.Kind);
        Assert.Equal(ClaimStatus.UnderReview, claim.Status);
    }

    [Fact]
    public void Rejection_OtherWithNote_IsRecordedWithNote()
    {
        var claim = Fixtures.UnderReview();

        claim.Reject(ActorRole.Manager, RejectionReason.Other, " duplicate of an earlier claim ", FakeClock.Now);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal(RejectionReason.Other, claim.RejectionReason);
        Assert.Equal("Rejected (Other): duplicate of an earlier claim", claim.History[^1].Note);
    }

    [Theory]
    [InlineData(RejectionReason.NotCovered)]
    [InlineData(RejectionReason.Fraud)]
    [InlineData(RejectionReason.InsufficientEvidence)]
    [InlineData(RejectionReason.LateFiling)]
    [InlineData(RejectionReason.BelowDeductible)]
    [InlineData(RejectionReason.LimitExhausted)]
    public void Rejection_EveryOtherReason_NeedsNoNote(RejectionReason reason)
    {
        var claim = Fixtures.Submitted();

        claim.Reject(ActorRole.Adjuster, reason, null, FakeClock.Now);

        Assert.Equal(reason, claim.RejectionReason);
        Assert.Equal($"Rejected: {reason}.", claim.History[^1].Note);
    }

    [Fact]
    public void Rejection_FromSubmittedAndUnderReview_BothAllowed()
    {
        var fromSubmitted = Fixtures.Submitted();
        fromSubmitted.Reject(ActorRole.Adjuster, RejectionReason.NotCovered, null, FakeClock.Now);

        var fromReview = Fixtures.UnderReview();
        fromReview.Reject(ActorRole.SeniorAdjuster, RejectionReason.Fraud, "staged accident", FakeClock.Now);

        Assert.Equal(ClaimStatus.Rejected, fromSubmitted.Status);
        Assert.Equal(ClaimStatus.Rejected, fromReview.Status);
    }
}
