using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 4: Submitted -> UnderReview -> Approved -> Paid; Submitted|UnderReview -> Rejected|Withdrawn; terminal states are final (409 invalid_transition).</summary>
public sealed class StateMachineTests
{
    public enum ClaimAction
    {
        Review,
        Approve,
        Reject,
        Pay,
        Withdraw,
        ClearFlag,
    }

    private static readonly HashSet<(ClaimStatus, ClaimAction)> Allowed =
    [
        (ClaimStatus.Submitted, ClaimAction.Review),
        (ClaimStatus.Submitted, ClaimAction.Reject),
        (ClaimStatus.Submitted, ClaimAction.Withdraw),
        (ClaimStatus.UnderReview, ClaimAction.Approve),
        (ClaimStatus.UnderReview, ClaimAction.Reject),
        (ClaimStatus.UnderReview, ClaimAction.Withdraw),
        (ClaimStatus.Approved, ClaimAction.Pay),
    ];

    /// <summary>Every (state, action) pair: 6 states x 6 actions.</summary>
    public static TheoryData<ClaimStatus, ClaimAction> AllPairs()
    {
        var data = new TheoryData<ClaimStatus, ClaimAction>();
        foreach (var status in Enum.GetValues<ClaimStatus>())
        {
            foreach (var action in Enum.GetValues<ClaimAction>())
            {
                data.Add(status, action);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void StateMachine_EveryStateActionPair_BehavesPerTable(ClaimStatus from, ClaimAction action)
    {
        var policy = Fixtures.ActivePolicy();
        var claim = InState(policy, from);

        if (Allowed.Contains((from, action)))
        {
            Perform(claim, policy, action);
            Assert.Equal(ExpectedTarget(action), claim.Status);
            return;
        }

        var ex = Assert.Throws<InvalidTransitionException>(() => Perform(claim, policy, action));

        // ClearFlag on a live, unflagged claim is a different 409 than a transition on a closed claim.
        var expectedCode = action == ClaimAction.ClearFlag && !from.IsTerminal() ? "flag_not_set" : "invalid_transition";
        Assert.Equal(expectedCode, ex.Code);
        Assert.Equal(ErrorKind.Conflict, ex.Kind);
        Assert.Equal(from, claim.Status);
    }

    [Fact]
    public void StateMachine_HappyPath_SubmittedToPaid()
    {
        var policy = Fixtures.ActivePolicy();
        var claim = Fixtures.Submitted(policy);

        claim.StartReview(ActorRole.Adjuster, FakeClock.Now);
        Assert.Equal(ClaimStatus.UnderReview, claim.Status);

        claim.Approve(ActorRole.Adjuster, Fixtures.Rules, FakeClock.Now);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        Assert.Equal(claim.EligiblePayout, claim.ApprovedPayout);

        claim.Pay(ActorRole.Manager, policy, Money.Zero, FakeClock.Now);
        Assert.Equal(ClaimStatus.Paid, claim.Status);
        Assert.Equal(FakeClock.Now, claim.PaidAt);
        Assert.Equal(4, claim.Version);
    }

    [Fact]
    public void StateMachine_DoubleApprove_IsInvalidTransition()
    {
        var claim = Fixtures.Approved();
        var ex = Assert.Throws<InvalidTransitionException>(() => claim.Approve(ActorRole.Manager, Fixtures.Rules, FakeClock.Now));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public void StateMachine_PayBeforeApprove_IsInvalidTransition()
    {
        var policy = Fixtures.ActivePolicy();
        var claim = Fixtures.UnderReview(policy);
        Assert.Throws<InvalidTransitionException>(() => claim.Pay(ActorRole.Manager, policy, Money.Zero, FakeClock.Now));
    }

    [Fact]
    public void StateMachine_WithdrawAfterApproval_IsInvalidTransition()
    {
        var claim = Fixtures.Approved();
        var ex = Assert.Throws<InvalidTransitionException>(() => claim.Withdraw(ActorRole.Claimant, FakeClock.Now));
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public void StateMachine_ClearFlagOnUnflaggedClaim_IsFlagNotSet()
    {
        var claim = Fixtures.Submitted();
        var ex = Assert.Throws<InvalidTransitionException>(() => claim.ClearFlag(ActorRole.Manager, "checked", FakeClock.Now));
        Assert.Equal("flag_not_set", ex.Code);
    }

    [Theory]
    [InlineData(ClaimStatus.Paid, true)]
    [InlineData(ClaimStatus.Rejected, true)]
    [InlineData(ClaimStatus.Withdrawn, true)]
    [InlineData(ClaimStatus.Submitted, false)]
    [InlineData(ClaimStatus.UnderReview, false)]
    [InlineData(ClaimStatus.Approved, false)]
    public void StateMachine_TerminalStates(ClaimStatus status, bool terminal)
    {
        Assert.Equal(terminal, status.IsTerminal());
    }

    [Fact]
    public void StateMachine_WithdrawFromUnderReview_IsAllowed()
    {
        var claim = Fixtures.UnderReview();
        claim.Withdraw(ActorRole.Claimant, FakeClock.Now, "changed my mind");
        Assert.Equal(ClaimStatus.Withdrawn, claim.Status);
        Assert.Equal("changed my mind", claim.History[^1].Note);
    }

    private static Claim InState(Domain.Policies.Policy policy, ClaimStatus status)
    {
        switch (status)
        {
            case ClaimStatus.Submitted:
                return Fixtures.Submitted(policy);
            case ClaimStatus.UnderReview:
                return Fixtures.UnderReview(policy);
            case ClaimStatus.Approved:
                return Fixtures.Approved(policy);
            case ClaimStatus.Paid:
                return Fixtures.Paid(policy);
            case ClaimStatus.Rejected:
                {
                    var claim = Fixtures.Submitted(policy);
                    claim.Reject(ActorRole.Adjuster, RejectionReason.NotCovered, null, FakeClock.Now);
                    return claim;
                }

            case ClaimStatus.Withdrawn:
                {
                    var claim = Fixtures.Submitted(policy);
                    claim.Withdraw(ActorRole.Claimant, FakeClock.Now);
                    return claim;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }
    }

    private static void Perform(Claim claim, Domain.Policies.Policy policy, ClaimAction action)
    {
        switch (action)
        {
            case ClaimAction.Review:
                claim.StartReview(ActorRole.Adjuster, FakeClock.Now);
                break;
            case ClaimAction.Approve:
                claim.Approve(ActorRole.Manager, Fixtures.Rules, FakeClock.Now);
                break;
            case ClaimAction.Reject:
                claim.Reject(ActorRole.Adjuster, RejectionReason.NotCovered, null, FakeClock.Now);
                break;
            case ClaimAction.Pay:
                claim.Pay(ActorRole.Manager, policy, Money.Zero, FakeClock.Now);
                break;
            case ClaimAction.Withdraw:
                claim.Withdraw(ActorRole.Claimant, FakeClock.Now);
                break;
            case ClaimAction.ClearFlag:
                claim.ClearFlag(ActorRole.Manager, "reviewed", FakeClock.Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private static ClaimStatus ExpectedTarget(ClaimAction action) => action switch
    {
        ClaimAction.Review => ClaimStatus.UnderReview,
        ClaimAction.Approve => ClaimStatus.Approved,
        ClaimAction.Reject => ClaimStatus.Rejected,
        ClaimAction.Pay => ClaimStatus.Paid,
        ClaimAction.Withdraw => ClaimStatus.Withdrawn,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
