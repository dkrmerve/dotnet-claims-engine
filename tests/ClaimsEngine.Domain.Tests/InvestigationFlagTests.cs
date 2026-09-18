using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 6: 3+ other recent claims or claimed >= 80% of the limit flag the claim; flagged claims cannot be approved until a Manager clears the flag with a note.</summary>
public sealed class InvestigationFlagTests
{
    [Theory]
    [InlineData(2, ClaimFlag.None)]
    [InlineData(3, ClaimFlag.RequiresInvestigation)]
    [InlineData(10, ClaimFlag.RequiresInvestigation)]
    public void Flag_ExactlyThreeOtherClaims_Flags_TwoDoNot(int otherClaims, ClaimFlag expected)
    {
        var claim = Fixtures.Submitted(context: new ClaimSubmissionContext(Money.Zero, otherClaims));
        Assert.Equal(expected, claim.Flag);
    }

    [Theory]
    [InlineData(15_999.99, ClaimFlag.None)]
    [InlineData(16_000.00, ClaimFlag.RequiresInvestigation)]
    [InlineData(20_000.00, ClaimFlag.RequiresInvestigation)]
    public void Flag_ClaimedExactly80PercentOfLimit_Flags(decimal claimed, ClaimFlag expected)
    {
        var claim = Fixtures.Submitted(Fixtures.ActivePolicy(coverageLimit: 20_000m), claimed: claimed);
        Assert.Equal(expected, claim.Flag);
    }

    [Fact]
    public void Flag_FlaggedClaim_NotesReasonInHistory()
    {
        var claim = Fixtures.Submitted(context: new ClaimSubmissionContext(Money.Zero, 3));

        Assert.Contains("flagged for investigation (holder has 3 other claims", claim.History[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Flag_FlaggedClaim_CannotBeApprovedEvenByManager()
    {
        var claim = Fixtures.UnderReview(context: new ClaimSubmissionContext(Money.Zero, 3));

        var ex = Assert.Throws<InvalidTransitionException>(() => claim.Approve(ActorRole.Manager, Fixtures.Rules, FakeClock.Now));

        Assert.Equal("investigation_pending", ex.Code);
        Assert.Equal(ClaimStatus.UnderReview, claim.Status);
    }

    [Fact]
    public void Flag_ClearByNonManager_IsInsufficientAuthority()
    {
        var claim = Fixtures.UnderReview(context: new ClaimSubmissionContext(Money.Zero, 3));

        var ex = Assert.Throws<InsufficientAuthorityException>(() => claim.ClearFlag(ActorRole.SeniorAdjuster, "looks fine", FakeClock.Now));
        Assert.Equal("insufficient_authority", ex.Code);
        Assert.True(claim.IsFlagged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Flag_ClearWithoutNote_IsNoteRequired(string? note)
    {
        var claim = Fixtures.UnderReview(context: new ClaimSubmissionContext(Money.Zero, 3));

        var ex = Assert.Throws<ValidationException>(() => claim.ClearFlag(ActorRole.Manager, note, FakeClock.Now));
        Assert.Equal("note_required", ex.Code);
        Assert.Equal(ErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public void Flag_ManagerClearsWithNote_ThenApprovalSucceeds()
    {
        var claim = Fixtures.UnderReview(context: new ClaimSubmissionContext(Money.Zero, 3));
        var versionBefore = claim.Version;

        claim.ClearFlag(ActorRole.Manager, "  Interviewed the holder; documents are genuine.  ", FakeClock.Now);

        Assert.Equal(ClaimFlag.None, claim.Flag);
        Assert.Equal(versionBefore + 1, claim.Version);
        var entry = claim.History[^1];
        Assert.Equal(ClaimStatus.UnderReview, entry.FromStatus);
        Assert.Equal(ClaimStatus.UnderReview, entry.ToStatus);
        Assert.Equal(ActorRole.Manager, entry.ActorRole);
        Assert.Equal("Investigation flag cleared: Interviewed the holder; documents are genuine.", entry.Note);

        claim.Approve(ActorRole.Adjuster, Fixtures.Rules, FakeClock.Now);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
    }

    [Fact]
    public void Flag_ClearOnClosedClaim_IsInvalidTransition()
    {
        var claim = Fixtures.Submitted(context: new ClaimSubmissionContext(Money.Zero, 3));
        claim.Withdraw(ActorRole.Claimant, FakeClock.Now);

        var ex = Assert.Throws<InvalidTransitionException>(() => claim.ClearFlag(ActorRole.Manager, "note", FakeClock.Now));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public void Flag_ThresholdsAreConfigurable()
    {
        var rules = new ClaimRules(30, 1, 365, 0.5m, 14, Money.Euro(5_000m), Money.Euro(50_000m));

        Assert.True(Fixtures.Submitted(context: new ClaimSubmissionContext(Money.Zero, 1), rules: rules).IsFlagged);
        Assert.True(Fixtures.Submitted(Fixtures.ActivePolicy(coverageLimit: 10_000m), claimed: 5_000m, rules: rules).IsFlagged);
    }
}
