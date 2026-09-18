using System.Net;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Application.Dtos;

namespace ClaimsEngine.Api.Tests;

/// <summary>Rule 10: Idempotency-Key on POST /claims makes retries safe; the key is bound to the payload and expires after the TTL.</summary>
[Collection(ApiCollection.Name)]
public sealed class IdempotencyTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task Idempotency_SameKeySameBody_ReturnsIdentical201AndSameClaimId()
    {
        var policy = await CreatePolicyAsync();
        var client = ClaimantFor(policy);
        var key = Guid.NewGuid().ToString();

        var first = await SubmitAsync(client, policy.Id, idempotencyKey: key);
        var second = await SubmitAsync(client, policy.Id, idempotencyKey: key);

        var firstClaim = await first.ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
        var secondClaim = await second.ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
        Assert.Equal(firstClaim, secondClaim);
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());

        var list = await (await Manager.GetAsync($"/claims?policyId={policy.Id}")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Equal(1, list.TotalCount);
    }

    [Fact]
    public async Task Idempotency_SameKeyDifferentBody_Is422()
    {
        var policy = await CreatePolicyAsync();
        var client = ClaimantFor(policy);
        var key = Guid.NewGuid().ToString();
        await SubmitAsync(client, policy.Id, amount: 3_000m, idempotencyKey: key);

        var response = await SubmitAsync(client, policy.Id, amount: 3_001m, idempotencyKey: key);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
    }

    [Fact]
    public async Task Idempotency_KeyReusedAcrossPolicies_Is422()
    {
        var holder = Guid.NewGuid();
        var a = await CreatePolicyAsync(holderId: holder);
        var b = await CreatePolicyAsync(holderId: holder);
        var client = ClaimantFor(a);
        var key = Guid.NewGuid().ToString();
        await SubmitAsync(client, a.Id, idempotencyKey: key);

        await (await SubmitAsync(client, b.Id, idempotencyKey: key)).AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
    }

    [Fact]
    public async Task Idempotency_KeyExpiresAfter24Hours_ThenCreatesNewClaim()
    {
        var policy = await CreatePolicyAsync();
        var client = ClaimantFor(policy);
        var key = Guid.NewGuid().ToString();
        var incident = Clock.Today.AddDays(-5).ToString("yyyy-MM-dd");
        var first = await (await SubmitAsync(client, policy.Id, incidentDate: incident, idempotencyKey: key)).ReadOkAsync<ClaimDto>(HttpStatusCode.Created);

        Clock.Advance(TimeSpan.FromHours(24));
        var replayed = await (await SubmitAsync(client, policy.Id, incidentDate: incident, idempotencyKey: key)).ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
        Assert.Equal(first.Id, replayed.Id);

        Clock.Advance(TimeSpan.FromSeconds(1));
        var response = await SubmitAsync(client, policy.Id, incidentDate: incident, idempotencyKey: key);
        var fresh = await response.ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
        Assert.NotEqual(first.Id, fresh.Id);
        Assert.False(response.Headers.Contains("Idempotent-Replayed"));
    }

    [Fact]
    public async Task Idempotency_WithoutKey_EveryRequestCreatesAClaim()
    {
        var policy = await CreatePolicyAsync();
        var a = await SubmitClaimAsync(policy);
        var b = await SubmitClaimAsync(policy);
        Assert.NotEqual(a.Id, b.Id);
    }
}
