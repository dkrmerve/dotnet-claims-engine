using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Policies;
using ClaimsEngine.Application.Tests.Support;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Tests;

/// <summary>Read side: lookups, ownership filtering, pagination and the overdue (rule 8) threshold.</summary>
public sealed class QueryHandlerTests
{
    [Fact]
    public async Task GetClaim_ReturnsDto_ForBackOffice()
    {
        var s = new Scenario();
        var claim = s.AddClaim(s.AddPolicy());

        var dto = await new GetClaimHandler(s.Claims, s.Policies).HandleAsync(new GetClaimQuery(claim.Id.Value, Scenario.Adjuster));

        Assert.Equal(claim.Id.Value, dto.Id);
        Assert.Equal("EUR", dto.ClaimedAmount.Currency);
        Assert.Null(dto.ApprovedPayout);
    }

    [Fact]
    public async Task GetClaim_Unknown_IsNotFound()
    {
        var s = new Scenario();
        await Assert.ThrowsAsync<NotFoundException>(() => new GetClaimHandler(s.Claims, s.Policies).HandleAsync(new GetClaimQuery(Guid.NewGuid(), Scenario.Adjuster)));
    }

    [Fact]
    public async Task GetClaim_ClaimantOwner_IsAllowed_Stranger_IsNotOwner()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var claim = s.AddClaim(policy);
        var handler = new GetClaimHandler(s.Claims, s.Policies);

        var dto = await handler.HandleAsync(new GetClaimQuery(claim.Id.Value, Scenario.ClaimantFor(policy)));
        Assert.Equal(claim.Id.Value, dto.Id);

        var ex = await Assert.ThrowsAsync<NotOwnerException>(() => handler.HandleAsync(new GetClaimQuery(claim.Id.Value, Scenario.Stranger)));
        Assert.Equal("not_owner", ex.Code);
    }

    [Fact]
    public async Task GetClaim_ClaimantWhosePolicyVanished_IsNotFound()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var claim = s.AddClaim(policy);
        s.Policies.Policies.Remove(policy.Id);

        await Assert.ThrowsAsync<NotFoundException>(() => new GetClaimHandler(s.Claims, s.Policies).HandleAsync(new GetClaimQuery(claim.Id.Value, Scenario.ClaimantFor(policy))));
    }

    [Fact]
    public async Task GetHistory_ReturnsEntriesInOrder()
    {
        var s = new Scenario();
        var claim = s.AddClaim(s.AddPolicy(), status: ClaimStatus.Approved);

        var history = await new GetClaimHistoryHandler(s.Claims, s.Policies).HandleAsync(new GetClaimQuery(claim.Id.Value, Scenario.Manager));

        Assert.Equal(3, history.Count);
        Assert.Null(history[0].FromStatus);
        Assert.Equal(ClaimStatus.Approved, history[^1].ToStatus);
    }

    [Fact]
    public async Task ListClaims_FiltersByPolicyAndStatus_AndPages()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var other = s.AddPolicy();
        for (var i = 0; i < 5; i++)
        {
            s.AddClaim(policy, filedAt: s.Clock.UtcNow.AddMinutes(i));
        }

        s.AddClaim(policy, status: ClaimStatus.Withdrawn);
        s.AddClaim(other);
        var handler = new ListClaimsHandler(s.Claims);

        var page1 = await handler.HandleAsync(new ListClaimsQuery(policy.Id.Value, ClaimStatus.Submitted, 1, 2, Scenario.Adjuster));
        var page3 = await handler.HandleAsync(new ListClaimsQuery(policy.Id.Value, ClaimStatus.Submitted, 3, 2, Scenario.Adjuster));
        var all = await handler.HandleAsync(new ListClaimsQuery(null, null, 1, 100, Scenario.Adjuster));

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page1.TotalPages);
        Assert.Single(page3.Items);
        Assert.Equal(7, all.TotalCount);
        Assert.True(all.Items[0].FiledAt >= all.Items[^1].FiledAt);
    }

    [Fact]
    public async Task ListClaims_ClaimantSeesOnlyOwnClaims()
    {
        var s = new Scenario();
        var mine = s.AddPolicy();
        s.AddClaim(mine);
        s.AddClaim(s.AddPolicy());

        var page = await new ListClaimsHandler(s.Claims).HandleAsync(new ListClaimsQuery(null, null, 1, 20, Scenario.ClaimantFor(mine)));

        Assert.Single(page.Items);
        Assert.Equal(mine.Id.Value, page.Items[0].PolicyId);
    }

    [Fact]
    public async Task ListClaims_ClaimantWithNonGuidSubject_SeesNothing()
    {
        var s = new Scenario();
        s.AddClaim(s.AddPolicy());

        var page = await new ListClaimsHandler(s.Claims).HandleAsync(new ListClaimsQuery(null, null, 1, 20, new Actor("alice", ActorRole.Claimant)));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalPages);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task ListClaims_InvalidPaging_IsValidationError(int page, int pageSize)
    {
        var s = new Scenario();
        await Assert.ThrowsAsync<ValidationException>(() => new ListClaimsHandler(s.Claims).HandleAsync(new ListClaimsQuery(null, null, page, pageSize, Scenario.Adjuster)));
    }

    [Fact]
    public async Task Overdue_Exactly14Days_IsNotOverdue_14DaysPlusOneSecond_Is()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var claim = s.AddClaim(policy, status: ClaimStatus.UnderReview);
        var handler = new ListOverdueClaimsHandler(s.Claims, s.Clock, s.Rules);

        s.Clock.Advance(TimeSpan.FromDays(14));
        Assert.Empty(await handler.HandleAsync());

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        var overdue = Assert.Single(await handler.HandleAsync());
        Assert.Equal(claim.Id.Value, overdue.Id);
    }

    [Fact]
    public async Task Overdue_UsesReviewStart_NotFiledAt()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var claim = s.AddClaim(policy, incidentDate: s.Clock.Today.AddDays(-32), filedAt: s.Clock.UtcNow.AddDays(-30));
        s.Clock.Advance(TimeSpan.FromDays(1));
        claim.StartReview(ActorRole.Adjuster, s.Clock.UtcNow);
        var handler = new ListOverdueClaimsHandler(s.Claims, s.Clock, s.Rules);

        s.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Empty(await handler.HandleAsync());
    }

    [Fact]
    public async Task GetPolicy_OwnerAndBackOfficeSeeIt_StrangerDoesNot_UnknownIsNotFound()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var handler = new GetPolicyHandler(s.Policies);

        Assert.Equal(policy.Id.Value, (await handler.HandleAsync(new GetPolicyQuery(policy.Id.Value, Scenario.ClaimantFor(policy)))).Id);
        Assert.Equal(policy.PolicyNumber, (await handler.HandleAsync(new GetPolicyQuery(policy.Id.Value, Scenario.Adjuster))).PolicyNumber);
        await Assert.ThrowsAsync<NotOwnerException>(() => handler.HandleAsync(new GetPolicyQuery(policy.Id.Value, Scenario.Stranger)));
        await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(new GetPolicyQuery(Guid.NewGuid(), Scenario.Manager)));
    }

    [Fact]
    public async Task CreatePolicy_PersistsAndReturnsDto()
    {
        var s = new Scenario();
        var command = new CreatePolicyCommand("POL-NEW", Guid.NewGuid(), Domain.Policies.CoverageType.Home, 50_000m, 250m, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        var dto = await new CreatePolicyHandler(s.Policies, s.UnitOfWork).HandleAsync(command);

        Assert.Equal("POL-NEW", dto.PolicyNumber);
        Assert.Equal(50_000m, dto.CoverageLimit.Amount);
        Assert.Equal(Domain.Policies.PolicyStatus.Active, dto.Status);
        Assert.Single(s.Policies.Policies);
        Assert.Equal(1, s.UnitOfWork.Commits);
    }

    [Fact]
    public async Task CreatePolicy_InvalidValues_ThrowValidationAndCommitNothing()
    {
        var s = new Scenario();
        var command = new CreatePolicyCommand("POL-BAD", Guid.NewGuid(), Domain.Policies.CoverageType.Auto, 100m, 100m, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        await Assert.ThrowsAsync<ValidationException>(() => new CreatePolicyHandler(s.Policies, s.UnitOfWork).HandleAsync(command));
        Assert.Empty(s.Policies.Policies);
        Assert.Equal(0, s.UnitOfWork.Commits);
    }
}
