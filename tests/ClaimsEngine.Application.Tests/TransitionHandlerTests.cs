using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Application.Tests.Support;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Tests;

/// <summary>Command handlers: each loads the aggregate, delegates to the domain, and commits exactly once.</summary>
public sealed class TransitionHandlerTests
{
    [Fact]
    public async Task Review_HappyPath_And_NotFound()
    {
        var s = new Scenario();
        var claim = s.AddClaim(s.AddPolicy());
        var handler = new StartReviewHandler(s.Claims, s.UnitOfWork, s.Clock);

        var dto = await handler.HandleAsync(new StartReviewCommand(claim.Id.Value, Scenario.Adjuster, "taking it"));

        Assert.Equal(ClaimStatus.UnderReview, dto.Status);
        Assert.Equal(s.Clock.UtcNow, dto.ReviewStartedAt);
        Assert.Equal(1, s.UnitOfWork.Commits);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new StartReviewCommand(Guid.NewGuid(), Scenario.Adjuster, null)));
    }

    [Fact]
    public async Task Approve_HappyPath_And_NotFound()
    {
        var s = new Scenario();
        var claim = s.AddClaim(s.AddPolicy(), status: ClaimStatus.UnderReview);
        var handler = new ApproveClaimHandler(s.Claims, s.UnitOfWork, s.Clock, s.Rules);

        var dto = await handler.HandleAsync(new ApproveClaimCommand(claim.Id.Value, Scenario.Adjuster, null));

        Assert.Equal(ClaimStatus.Approved, dto.Status);
        Assert.Equal(dto.EligiblePayout, dto.ApprovedPayout);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new ApproveClaimCommand(Guid.NewGuid(), Scenario.Adjuster, null)));
    }

    [Fact]
    public async Task Approve_DomainRejection_DoesNotCommit()
    {
        var s = new Scenario();
        var claim = s.AddClaim(s.AddPolicy(), status: ClaimStatus.Submitted);

        await Assert.ThrowsAsync<InvalidTransitionException>(() => new ApproveClaimHandler(s.Claims, s.UnitOfWork, s.Clock, s.Rules).HandleAsync(new ApproveClaimCommand(claim.Id.Value, Scenario.Manager, null)));

        Assert.Equal(0, s.UnitOfWork.Commits);
    }

    [Fact]
    public async Task Reject_HappyPath_And_NotFound()
    {
        var s = new Scenario();
        var claim = s.AddClaim(s.AddPolicy());
        var handler = new RejectClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock);

        var dto = await handler.HandleAsync(new RejectClaimCommand(claim.Id.Value, Scenario.Adjuster, RejectionReason.NotCovered, null));

        Assert.Equal(ClaimStatus.Rejected, dto.Status);
        Assert.Equal(RejectionReason.NotCovered, dto.RejectionReason);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new RejectClaimCommand(Guid.NewGuid(), Scenario.Adjuster, RejectionReason.Fraud, null)));
    }

    [Fact]
    public async Task Pay_HappyPath_RecordsPayoutOnPolicy()
    {
        var s = new Scenario();
        var policy = s.AddPolicy(coverageLimit: 20_000m, deductible: 0m);
        var claim = s.AddClaim(policy, claimed: 4_000m, status: ClaimStatus.Approved);
        var handler = new PayClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock);

        var dto = await handler.HandleAsync(new PayClaimCommand(claim.Id.Value, Scenario.Manager));

        Assert.Equal(ClaimStatus.Paid, dto.Status);
        Assert.Equal(s.Clock.UtcNow, dto.PaidAt);
        Assert.Equal(Money.Euro(4_000m), policy.LifetimePaid);
    }

    [Fact]
    public async Task Pay_SecondClaimBeyondRemainingLimit_IsLimitExhausted()
    {
        var s = new Scenario();
        var policy = s.AddPolicy(coverageLimit: 10_000m, deductible: 0m);
        var first = s.AddClaim(policy, claimed: 7_000m, status: ClaimStatus.Approved);
        var second = s.AddClaim(policy, claimed: 5_000m, status: ClaimStatus.Approved);
        var handler = new PayClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock);

        await handler.HandleAsync(new PayClaimCommand(first.Id.Value, Scenario.Manager));
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => handler.HandleAsync(new PayClaimCommand(second.Id.Value, Scenario.Manager)));

        Assert.Equal("limit_exhausted", ex.Code);
        Assert.Equal(ClaimStatus.Approved, second.Status);
    }

    [Fact]
    public async Task Reject_ApprovedClaimTheLimitCanNoLongerHonour_IsRejectedAsLimitExhausted_ByManagerOnly()
    {
        var s = new Scenario();
        var policy = s.AddPolicy(coverageLimit: 10_000m, deductible: 0m);
        var first = s.AddClaim(policy, claimed: 7_000m, status: ClaimStatus.Approved);
        var second = s.AddClaim(policy, claimed: 5_000m, status: ClaimStatus.Approved);
        await new PayClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock).HandleAsync(new PayClaimCommand(first.Id.Value, Scenario.Manager));
        var handler = new RejectClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock);

        await Assert.ThrowsAsync<InsufficientAuthorityException>(() =>
            handler.HandleAsync(new RejectClaimCommand(second.Id.Value, Scenario.Adjuster, RejectionReason.LimitExhausted, null)));
        var dto = await handler.HandleAsync(new RejectClaimCommand(second.Id.Value, Scenario.Manager, RejectionReason.LimitExhausted, "holder informed"));

        Assert.Equal(ClaimStatus.Rejected, dto.Status);
        Assert.Equal(RejectionReason.LimitExhausted, dto.RejectionReason);
        Assert.Null(dto.ApprovedPayout);
    }

    [Fact]
    public async Task Reject_ApprovedClaimThatIsStillPayable_IsLimitNotExhausted_UnknownPolicy_IsNotFound()
    {
        var s = new Scenario();
        var policy = s.AddPolicy(coverageLimit: 10_000m, deductible: 0m);
        var claim = s.AddClaim(policy, claimed: 5_000m, status: ClaimStatus.Approved);
        var handler = new RejectClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock);

        var ex = await Assert.ThrowsAsync<InvalidTransitionException>(() =>
            handler.HandleAsync(new RejectClaimCommand(claim.Id.Value, Scenario.Manager, RejectionReason.LimitExhausted, null)));

        Assert.Equal("limit_not_exhausted", ex.Code);
        Assert.Equal(ClaimStatus.Approved, claim.Status);
        s.Policies.Policies.Remove(policy.Id);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.HandleAsync(new RejectClaimCommand(claim.Id.Value, Scenario.Manager, RejectionReason.LimitExhausted, null)));
    }

    [Fact]
    public async Task Pay_UnknownClaimOrPolicy_IsNotFound()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var claim = s.AddClaim(policy, status: ClaimStatus.Approved);
        var handler = new PayClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new PayClaimCommand(Guid.NewGuid(), Scenario.Manager)));
        s.Policies.Policies.Remove(policy.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new PayClaimCommand(claim.Id.Value, Scenario.Manager)));
    }

    [Fact]
    public async Task Withdraw_OwnerSucceeds_StrangerIsNotOwner_UnknownIsNotFound()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var claim = s.AddClaim(policy);
        var handler = new WithdrawClaimHandler(s.Claims, s.Policies, s.UnitOfWork, s.Clock);

        await Assert.ThrowsAsync<NotOwnerException>(() => handler.HandleAsync(new WithdrawClaimCommand(claim.Id.Value, Scenario.Stranger, null)));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new WithdrawClaimCommand(Guid.NewGuid(), Scenario.Stranger, null)));

        var dto = await handler.HandleAsync(new WithdrawClaimCommand(claim.Id.Value, Scenario.ClaimantFor(policy), "sorted privately"));
        Assert.Equal(ClaimStatus.Withdrawn, dto.Status);
    }

    [Fact]
    public async Task ClearFlag_HappyPath_And_NotFound()
    {
        var s = new Scenario();
        var holder = HolderId.New();
        var policy = s.AddPolicy(holder: holder);
        s.AddClaim(policy);
        s.AddClaim(policy);
        s.AddClaim(policy);
        var flagged = (await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy))).Claim;
        Assert.Equal(ClaimFlag.RequiresInvestigation, flagged.Flag);
        var handler = new ClearClaimFlagHandler(s.Claims, s.UnitOfWork, s.Clock);

        var dto = await handler.HandleAsync(new ClearClaimFlagCommand(flagged.Id, Scenario.Manager, "all documents verified"));

        Assert.Equal(ClaimFlag.None, dto.Flag);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new ClearClaimFlagCommand(Guid.NewGuid(), Scenario.Manager, "x")));
    }

    [Fact]
    public async Task Commit_ConcurrencyFailure_PropagatesAsConcurrencyException()
    {
        var s = new Scenario();
        var claim = s.AddClaim(s.AddPolicy());
        s.UnitOfWork.FailCommitWith = new ConcurrencyException();

        var ex = await Assert.ThrowsAsync<ConcurrencyException>(() => new StartReviewHandler(s.Claims, s.UnitOfWork, s.Clock).HandleAsync(new StartReviewCommand(claim.Id.Value, Scenario.Adjuster, null)));
        Assert.Equal("concurrency_conflict", ex.Code);
    }

    [Fact]
    public void Dtos_MapNullableMoneyAndPaging()
    {
        Assert.Null(MoneyDto.From((Money?)null));
        Assert.Equal(new MoneyDto(1.5m, "EUR"), MoneyDto.From((Money?)Money.Euro(1.5m)));

        Assert.Equal(0, new PagedResponse<int>([], 1, 20, 0).TotalPages);
        Assert.Equal(1, new PagedResponse<int>([1], 1, 20, 20).TotalPages);
        Assert.Equal(2, new PagedResponse<int>([1], 1, 20, 21).TotalPages);
    }

    [Fact]
    public void IdempotencyRecord_ExpiryIsStrictlyAfterTtl()
    {
        var created = FakeClock.Start;
        var record = new IdempotencyRecord("alice", "k", "h", ClaimId.New(), created);
        var ttl = TimeSpan.FromHours(24);

        Assert.False(record.IsExpired(created + ttl, ttl));
        Assert.True(record.IsExpired(created + ttl + TimeSpan.FromTicks(1), ttl));
        Assert.Equal(TimeSpan.FromHours(24), IdempotencySettings.Default.TimeToLive);
    }
}
