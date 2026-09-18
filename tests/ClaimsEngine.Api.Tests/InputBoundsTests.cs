using System.Net;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Api.Tests;

/// <summary>
/// Inputs that fit the JSON contract but not the database columns (varchar lengths, numeric(18,2),
/// a 32-bit OFFSET) are refused as 400 at the edge instead of surfacing as a 500 from PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InputBoundsTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task Submit_IdempotencyKeyLongerThanColumn_Is400_AtTheLimit_Is201()
    {
        var policy = await CreatePolicyAsync();

        var tooLong = await SubmitAsync(ClaimantFor(policy), policy.Id, idempotencyKey: new string('k', IdempotencyRecord.MaxKeyLength + 1));
        await tooLong.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_error");

        var atLimit = await SubmitAsync(ClaimantFor(policy), policy.Id, idempotencyKey: new string('k', IdempotencyRecord.MaxKeyLength));
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
    }

    [Fact]
    public async Task Reject_LongestAllowedNote_FitsTheHistoryColumnTogetherWithItsPrefix()
    {
        var policy = await CreatePolicyAsync();
        var claim = await SubmitClaimAsync(policy);
        var note = new string('n', Claim.MaxNoteLength);

        var rejected = await TransitionAsync(Adjuster, claim.Id, "reject", new { reason = nameof(RejectionReason.InsufficientEvidence), note });

        Assert.Equal(ClaimStatus.Rejected, rejected.Status);
        var history = await (await Manager.GetAsync($"/claims/{claim.Id}/history")).ReadOkAsync<List<ClaimHistoryEntryDto>>();
        Assert.True(history[^1].Note!.Length <= ClaimHistoryEntry.MaxNoteLength);
        Assert.EndsWith(note, history[^1].Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reject")]
    [InlineData("clear-flag")]
    [InlineData("review")]
    public async Task Transition_NoteLongerThanMaxNoteLength_Is400(string action)
    {
        var policy = await CreatePolicyAsync();
        var claim = await SubmitClaimAsync(policy);
        var note = new string('n', Claim.MaxNoteLength + 1);

        var response = await Manager.PostJsonAsync($"/claims/{claim.Id}/{action}", new { reason = nameof(RejectionReason.Other), note });

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("note", problem.Errors!.Keys);
    }

    [Fact]
    public async Task CreatePolicy_AmountBeyondNumericPrecision_Is400()
    {
        var response = await Manager.PostJsonAsync("/policies", PolicyBody(coverageLimit: Money.MaxAmount + 1m));

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("coverageLimit", problem.Errors!.Keys);
    }

    [Fact]
    public async Task Submit_ClaimedAmountBeyondNumericPrecision_Is400()
    {
        var policy = await CreatePolicyAsync();

        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, amount: Money.MaxAmount + 1m);

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("claimedAmount", problem.Errors!.Keys);
    }

    [Fact]
    public async Task List_PageWhoseOffsetOverflowsInt32_Is400_LastValidPage_IsEmpty200()
    {
        var overflow = await Manager.GetAsync($"/claims?page={ListClaimsHandler.MaxPage + 1}&pageSize={ListClaimsHandler.MaxPageSize}");
        var problem = await overflow.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("page", problem.Errors!.Keys);

        var last = await Manager.GetAsync($"/claims?page={ListClaimsHandler.MaxPage}&pageSize={ListClaimsHandler.MaxPageSize}");
        var page = await last.ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Empty(page.Items);
    }
}
