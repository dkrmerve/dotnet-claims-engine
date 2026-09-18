using System.Net;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Domain.Claims;

namespace ClaimsEngine.Api.Tests;

/// <summary>POST /claims through the whole stack: validation at the edge, rules 1-3 and 6 in the domain, persistence in PostgreSQL.</summary>
[Collection(ApiCollection.Name)]
public sealed class SubmitClaimTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task Submit_HappyPath_Returns201WithLocation_AndIsReadable()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 20_000m, deductible: 500m);

        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, amount: 3_000m);

        var claim = await response.ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
        Assert.Equal($"/claims/{claim.Id}", response.Headers.Location?.ToString());
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Equal(new MoneyDto(2_500m, "EUR"), claim.EligiblePayout);
        Assert.Equal(ClaimFlag.None, claim.Flag);
        Assert.Equal(Clock.UtcNow, claim.FiledAt);
        Assert.Equal(TimeSpan.Zero, claim.FiledAt.Offset);

        var fetched = await GetClaimAsync(claim.Id, ClaimantFor(policy));
        Assert.Equal(claim, fetched);
    }

    [Fact]
    public async Task Submit_ByAdjusterOnBehalfOfHolder_IsAllowed()
    {
        var policy = await CreatePolicyAsync();
        var response = await SubmitAsync(Adjuster, policy.Id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Submit_UnknownPolicy_Is404()
    {
        var response = await SubmitAsync(Manager, Guid.NewGuid());
        await response.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Submit_AllFieldsMissing_ReportsEveryFieldOnce()
    {
        var response = await Manager.PostJsonAsync("/claims", new { });

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Equal(["claimedAmount", "description", "incidentDate", "policyId"], problem.Errors!.Keys.Order().ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task Submit_NonPositiveAmount_Is400(decimal amount)
    {
        var policy = await CreatePolicyAsync();
        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, amount);

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("greater than zero", problem.Errors!["claimedAmount"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_AmountWithThreeDecimals_Is400()
    {
        var policy = await CreatePolicyAsync();
        var problem = await (await SubmitAsync(ClaimantFor(policy), policy.Id, 100.005m)).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("two decimals", problem.Errors!["claimedAmount"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_AmountNaN_Is400InvalidRequest()
    {
        var policy = await CreatePolicyAsync();
        var response = await ClaimantFor(policy).PostRawAsync("/claims", $"{{\"policyId\":\"{policy.Id}\",\"incidentDate\":\"2026-03-10\",\"claimedAmount\":\"NaN\",\"description\":\"x\"}}");
        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Submit_DescriptionOver2000Chars_Is400()
    {
        var policy = await CreatePolicyAsync();
        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, description: new string('d', 2_001));

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("2000", problem.Errors!["description"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_IncidentDateInTheFuture_Is400ValidationError()
    {
        var policy = await CreatePolicyAsync();
        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: Clock.Today.AddDays(1).ToString("yyyy-MM-dd"));
        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "validation_error");
    }

    [Fact]
    public async Task Submit_IncidentDateUnparseable_Is400()
    {
        var policy = await CreatePolicyAsync();
        var problem = await (await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: "10/03/2026 or so")).AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("incidentDate", problem.Errors!.Keys);
    }

    [Fact]
    public async Task Submit_IncidentDateTimeWithPlus3Offset_IsNormalisedToUtcDay()
    {
        var policy = await CreatePolicyAsync();

        // 01:30 on 11 March in UTC+3 is 22:30 on 10 March in UTC.
        var claim = await SubmitClaimAsync(policy, incidentDate: "2026-03-11T01:30:00+03:00");

        Assert.Equal(new DateOnly(2026, 3, 10), claim.IncidentDate);
    }

    [Fact]
    public async Task Submit_IncidentOutsidePolicyPeriod_Is422PolicyNotEligible()
    {
        var policy = await CreatePolicyAsync(from: "2026-03-12", to: "2026-12-31");
        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: "2026-03-11");
        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "policy_not_eligible");
    }

    [Fact]
    public async Task Submit_IncidentOnEffectiveFrom_IsAccepted()
    {
        var policy = await CreatePolicyAsync(from: "2026-03-10", to: "2026-12-31");
        var claim = await SubmitClaimAsync(policy, incidentDate: "2026-03-10");
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
    }

    [Fact]
    public async Task Submit_LateFiling_Creates201RejectedClaimWithAuditTrail()
    {
        var policy = await CreatePolicyAsync();

        var claim = await SubmitClaimAsync(policy, incidentDate: Clock.Today.AddDays(-31).ToString("yyyy-MM-dd"));

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal(RejectionReason.LateFiling, claim.RejectionReason);
        var history = await (await Manager.GetAsync($"/claims/{claim.Id}/history")).ReadOkAsync<List<ClaimHistoryEntryDto>>();
        Assert.Equal(2, history.Count);
        Assert.Equal(Domain.Shared.ActorRole.System, history[1].ActorRole);
    }

    [Fact]
    public async Task Submit_ExactlyThirtyDays_IsAccepted()
    {
        var policy = await CreatePolicyAsync();
        Clock.UtcNow = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

        var claim = await SubmitClaimAsync(policy, incidentDate: "2026-02-13");

        Assert.Equal(ClaimStatus.Submitted, claim.Status);
    }

    [Fact]
    public async Task Submit_BelowDeductible_IsAutoRejected()
    {
        var policy = await CreatePolicyAsync(deductible: 500m);
        var claim = await SubmitClaimAsync(policy, amount: 500m);
        Assert.Equal(RejectionReason.BelowDeductible, claim.RejectionReason);
    }

    [Fact]
    public async Task Submit_HighValueClaim_IsFlagged()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 10_000m);
        var claim = await SubmitClaimAsync(policy, amount: 8_000m);
        Assert.Equal(ClaimFlag.RequiresInvestigation, claim.Flag);
    }

    [Fact]
    public async Task Submit_FourthClaimInAYear_IsFlagged_WithdrawnExcluded()
    {
        var holder = Guid.NewGuid();
        var policy = await CreatePolicyAsync(holderId: holder);
        var other = await CreatePolicyAsync(holderId: holder);
        var claimant = ClaimantFor(policy);

        await SubmitClaimAsync(policy);
        await SubmitClaimAsync(other);
        var withdrawn = await SubmitClaimAsync(policy);
        await TransitionAsync(claimant, withdrawn.Id, "withdraw");

        var third = await SubmitClaimAsync(policy);
        Assert.Equal(ClaimFlag.None, third.Flag);

        var fourth = await SubmitClaimAsync(policy);
        Assert.Equal(ClaimFlag.RequiresInvestigation, fourth.Flag);
    }

    [Fact]
    public async Task Submit_OldClaimsOutsideTheWindow_DoNotCount()
    {
        var policy = await CreatePolicyAsync(from: "2025-01-01", to: "2026-12-31");
        Clock.UtcNow = new DateTimeOffset(2025, 3, 10, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            await SubmitClaimAsync(policy, incidentDate: "2025-03-01");
        }

        Clock.UtcNow = new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);
        var claim = await SubmitClaimAsync(policy);

        Assert.Equal(ClaimFlag.None, claim.Flag);
    }

    [Fact]
    public async Task Submit_LimitConsumedByPreviousPolicyYear_IsNotCounted()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 10_000m, deductible: 0m, from: "2025-03-01", to: "2027-02-28");
        Clock.UtcNow = new DateTimeOffset(2026, 2, 20, 12, 0, 0, TimeSpan.Zero);
        var previousYear = await ApprovedClaimAsync(policy, amount: 7_000m);
        await TransitionAsync(Manager, previousYear.Id, "pay");

        Clock.UtcNow = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero);
        var claim = await SubmitClaimAsync(policy, amount: 6_000m);

        Assert.Equal(6_000m, claim.EligiblePayout.Amount);
    }

    [Fact]
    public async Task Submit_LimitConsumedInSamePolicyYear_CapsAndThenExhausts()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 10_000m, deductible: 0m);
        var first = await ApprovedClaimAsync(policy, amount: 7_000m);
        await TransitionAsync(Manager, first.Id, "pay");

        var capped = await SubmitClaimAsync(policy, amount: 5_000m);
        Assert.Equal(3_000m, capped.EligiblePayout.Amount);

        await TransitionAsync(Adjuster, capped.Id, "review");
        await TransitionAsync(Adjuster, capped.Id, "approve");
        await TransitionAsync(Manager, capped.Id, "pay");

        var exhausted = await SubmitClaimAsync(policy, amount: 100m);
        Assert.Equal(RejectionReason.LimitExhausted, exhausted.RejectionReason);
    }
}
