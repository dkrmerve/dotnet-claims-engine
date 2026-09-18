using System.Net;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Api.Tests;

/// <summary>The transition endpoints end to end: state machine (rule 4), authority (rule 5), flags (rule 6), limits (rule 7), overdue (rule 8), rejection reasons (rule 9), history (rule 11).</summary>
[Collection(ApiCollection.Name)]
public sealed class ClaimLifecycleTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task Lifecycle_ReviewApprovePay_HappyPathWithHistory()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 20_000m, deductible: 500m);
        var submitted = await SubmitClaimAsync(policy, amount: 3_000m);

        Clock.Advance(TimeSpan.FromHours(1));
        var reviewed = await TransitionAsync(Adjuster, submitted.Id, "review", new { note = "assigned" });
        Assert.Equal(ClaimStatus.UnderReview, reviewed.Status);
        Assert.Equal(Clock.UtcNow, reviewed.ReviewStartedAt);

        var approved = await TransitionAsync(Adjuster, submitted.Id, "approve");
        Assert.Equal(ClaimStatus.Approved, approved.Status);
        Assert.Equal(new MoneyDto(2_500m, "EUR"), approved.ApprovedPayout);

        var paid = await TransitionAsync(Manager, submitted.Id, "pay");
        Assert.Equal(ClaimStatus.Paid, paid.Status);
        Assert.Equal(Clock.UtcNow, paid.PaidAt);
        Assert.Equal(4, paid.Version);

        var history = await (await ClaimantFor(policy).GetAsync($"/claims/{submitted.Id}/history")).ReadOkAsync<List<ClaimHistoryEntryDto>>();
        Assert.Equal([ClaimStatus.Submitted, ClaimStatus.UnderReview, ClaimStatus.Approved, ClaimStatus.Paid], history.Select(h => h.ToStatus));
        Assert.Equal("assigned", history[1].Note);
        Assert.Equal([ActorRole.Claimant, ActorRole.Adjuster, ActorRole.Adjuster, ActorRole.Manager], history.Select(h => h.ActorRole));

        var refreshed = await (await Manager.GetAsync($"/policies/{policy.Id}")).ReadOkAsync<PolicyDto>();
        Assert.Equal(2_500m, refreshed.LifetimePaid.Amount);
        Assert.Equal(2, refreshed.Version);
    }

    [Fact]
    public async Task Transition_ApproveFromSubmitted_Is409InvalidTransition()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        await (await Manager.PostEmptyAsync($"/claims/{claim.Id}/approve")).AssertProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task Transition_PayBeforeApprove_Is409()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        await TransitionAsync(Adjuster, claim.Id, "review");
        await (await Manager.PostEmptyAsync($"/claims/{claim.Id}/pay")).AssertProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task Transition_DoubleApprove_Is409()
    {
        var claim = await ApprovedClaimAsync(await CreatePolicyAsync());
        await (await Manager.PostEmptyAsync($"/claims/{claim.Id}/approve")).AssertProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task Transition_WithdrawAfterApproval_Is409_WithdrawFromReview_IsAllowed()
    {
        var policy = await CreatePolicyAsync();
        var approved = await ApprovedClaimAsync(policy);
        await (await ClaimantFor(policy).PostEmptyAsync($"/claims/{approved.Id}/withdraw")).AssertProblemAsync(HttpStatusCode.Conflict, "invalid_transition");

        var reviewing = await SubmitClaimAsync(policy);
        await TransitionAsync(Adjuster, reviewing.Id, "review");
        var withdrawn = await TransitionAsync(ClaimantFor(policy), reviewing.Id, "withdraw", new { note = "settled privately" });
        Assert.Equal(ClaimStatus.Withdrawn, withdrawn.Status);
    }

    [Fact]
    public async Task Transition_OnTerminalClaim_Is409()
    {
        var claim = await ApprovedClaimAsync(await CreatePolicyAsync());
        await TransitionAsync(Manager, claim.Id, "pay");

        await (await Adjuster.PostEmptyAsync($"/claims/{claim.Id}/review")).AssertProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
        await (await Adjuster.PostJsonAsync($"/claims/{claim.Id}/reject", new { reason = "Fraud" })).AssertProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
        await (await Manager.PostJsonAsync($"/claims/{claim.Id}/clear-flag", new { note = "x" })).AssertProblemAsync(HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task Transition_UnknownClaim_Is404OnEveryAction()
    {
        var id = Guid.NewGuid();
        await (await Adjuster.PostEmptyAsync($"/claims/{id}/review")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await Manager.PostEmptyAsync($"/claims/{id}/approve")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await Manager.PostJsonAsync($"/claims/{id}/reject", new { reason = "Fraud" })).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await Manager.PostEmptyAsync($"/claims/{id}/pay")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await As(ActorRole.Claimant, "x").PostEmptyAsync($"/claims/{id}/withdraw")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await Manager.PostJsonAsync($"/claims/{id}/clear-flag", new { note = "x" })).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await Manager.GetAsync($"/claims/{id}")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await Manager.GetAsync($"/claims/{id}/history")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Authority_AdjusterOn5000Point01_Is403_SeniorAdjusterOk()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 100_000m, deductible: 500m);
        var claim = await SubmitClaimAsync(policy, amount: 5_500.01m);
        await TransitionAsync(Adjuster, claim.Id, "review");

        var problem = await (await Adjuster.PostEmptyAsync($"/claims/{claim.Id}/approve")).AssertProblemAsync(HttpStatusCode.Forbidden, "insufficient_authority");
        Assert.Contains("SeniorAdjuster or Manager", problem.Detail, StringComparison.Ordinal);

        var approved = await TransitionAsync(SeniorAdjuster, claim.Id, "approve");
        Assert.Equal(5_000.01m, approved.ApprovedPayout!.Amount);
    }

    [Fact]
    public async Task Authority_SeniorAdjusterOn50000Point01_Is403_ManagerOk()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 1_000_000m, deductible: 0m);
        var claim = await SubmitClaimAsync(policy, amount: 50_000.01m);
        await TransitionAsync(Adjuster, claim.Id, "review");

        await (await SeniorAdjuster.PostEmptyAsync($"/claims/{claim.Id}/approve")).AssertProblemAsync(HttpStatusCode.Forbidden, "insufficient_authority");
        Assert.Equal(ClaimStatus.Approved, (await TransitionAsync(Manager, claim.Id, "approve")).Status);
    }

    [Fact]
    public async Task Flag_FlaggedClaim_CannotBeApproved_UntilManagerClearsWithNote()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 10_000m);
        var claim = await SubmitClaimAsync(policy, amount: 8_000m);
        Assert.Equal(ClaimFlag.RequiresInvestigation, claim.Flag);
        await TransitionAsync(Adjuster, claim.Id, "review");

        await (await Manager.PostEmptyAsync($"/claims/{claim.Id}/approve")).AssertProblemAsync(HttpStatusCode.Conflict, "investigation_pending");
        var missingNote = await (await Manager.PostJsonAsync($"/claims/{claim.Id}/clear-flag", new { note = " " })).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("note", missingNote.Errors!.Keys);
        await (await Manager.PostEmptyAsync($"/claims/{claim.Id}/clear-flag")).AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_request");

        var cleared = await TransitionAsync(Manager, claim.Id, "clear-flag", new { note = "Documents verified." });
        Assert.Equal(ClaimFlag.None, cleared.Flag);
        Assert.Equal(ClaimStatus.Approved, (await TransitionAsync(Manager, claim.Id, "approve")).Status);

        await (await Manager.PostJsonAsync($"/claims/{claim.Id}/clear-flag", new { note = "again" })).AssertProblemAsync(HttpStatusCode.Conflict, "flag_not_set");
    }

    [Fact]
    public async Task Flag_ClearOnUnflaggedClaim_Is409FlagNotSet()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        await (await Manager.PostJsonAsync($"/claims/{claim.Id}/clear-flag", new { note = "nothing to clear" })).AssertProblemAsync(HttpStatusCode.Conflict, "flag_not_set");
    }

    [Fact]
    public async Task Reject_OtherWithoutNote_Is400_WithNote_Ok_UnknownReason_ListsValues()
    {
        var policy = await CreatePolicyAsync();
        var claim = await SubmitClaimAsync(policy);

        var noNote = await (await Adjuster.PostJsonAsync($"/claims/{claim.Id}/reject", new { reason = "Other" })).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("required when reason is Other", noNote.Errors!["note"][0], StringComparison.Ordinal);

        var unknown = await (await Adjuster.PostJsonAsync($"/claims/{claim.Id}/reject", new { reason = "Because" })).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Equal("reason must be one of: LateFiling, BelowDeductible, LimitExhausted, NotCovered, Fraud, InsufficientEvidence, Other.", unknown.Errors!["reason"][0]);

        var rejected = await TransitionAsync(Adjuster, claim.Id, "reject", new { reason = "other", note = "duplicate submission" });
        Assert.Equal(ClaimStatus.Rejected, rejected.Status);
        Assert.Equal(RejectionReason.Other, rejected.RejectionReason);
    }

    [Fact]
    public async Task Reject_NoteOver2000Chars_Is400()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        var problem = await (await Adjuster.PostJsonAsync($"/claims/{claim.Id}/reject", new { reason = "Fraud", note = new string('n', 2_001) })).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("note", problem.Errors!.Keys);
        await (await Adjuster.PostJsonAsync($"/claims/{claim.Id}/review", new { note = new string('n', 2_001) })).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
    }

    [Fact]
    public async Task Pay_SecondClaimBeyondLimit_Is422LimitExhausted()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 10_000m, deductible: 0m);
        var first = await ApprovedClaimAsync(policy, amount: 7_000m);
        var second = await ApprovedClaimAsync(policy, amount: 5_000m);

        await TransitionAsync(Manager, first.Id, "pay");
        await (await Manager.PostEmptyAsync($"/claims/{second.Id}/pay")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "limit_exhausted");

        Assert.Equal(ClaimStatus.Approved, (await GetClaimAsync(second.Id)).Status);
    }

    [Fact]
    public async Task Overdue_Exactly14Days_NotListed_14DaysPlusOneSecond_Listed_FromReviewStart()
    {
        var policy = await CreatePolicyAsync();
        var claim = await SubmitClaimAsync(policy);
        Clock.Advance(TimeSpan.FromDays(5));
        await TransitionAsync(Adjuster, claim.Id, "review");
        var untouched = await SubmitClaimAsync(policy);

        Clock.Advance(TimeSpan.FromDays(14));
        Assert.Empty(await (await Adjuster.GetAsync("/claims/overdue")).ReadOkAsync<List<ClaimDto>>());

        Clock.Advance(TimeSpan.FromSeconds(1));
        var overdue = await (await Adjuster.GetAsync("/claims/overdue")).ReadOkAsync<List<ClaimDto>>();
        Assert.Equal([claim.Id], overdue.Select(c => c.Id));
        Assert.DoesNotContain(untouched.Id, overdue.Select(c => c.Id));
    }

    [Fact]
    public async Task List_FiltersPagesAndScopesToClaimant()
    {
        var policy = await CreatePolicyAsync();
        var other = await CreatePolicyAsync();
        for (var i = 0; i < 3; i++)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            await SubmitClaimAsync(policy);
        }

        var withdrawn = await SubmitClaimAsync(policy);
        await TransitionAsync(ClaimantFor(policy), withdrawn.Id, "withdraw");
        await SubmitClaimAsync(other);

        var page = await (await Manager.GetAsync($"/claims?policyId={policy.Id}&status=submitted&page=1&pageSize=2")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.TotalPages);
        Assert.True(page.Items[0].FiledAt > page.Items[1].FiledAt);

        var last = await (await Manager.GetAsync($"/claims?policyId={policy.Id}&status=Submitted&page=2&pageSize=2")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Single(last.Items);

        var defaults = await (await Manager.GetAsync("/claims")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Equal(1, defaults.Page);
        Assert.Equal(20, defaults.PageSize);
        Assert.Equal(5, defaults.TotalCount);

        var mine = await (await ClaimantFor(other).GetAsync("/claims")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Single(mine.Items);
        Assert.Equal(other.Id, mine.Items[0].PolicyId);
    }

    [Theory]
    [InlineData("pageSize=0", "pageSize")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("status=Open", "status")]
    public async Task List_InvalidQuery_Is400WithFieldError(string query, string field)
    {
        var problem = await (await Manager.GetAsync("/claims?" + query)).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains(field, problem.Errors!.Keys);
    }

    [Fact]
    public async Task List_NonNumericPage_Is400InvalidRequest()
    {
        await (await Manager.GetAsync("/claims?page=abc")).AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_request");
    }
}
