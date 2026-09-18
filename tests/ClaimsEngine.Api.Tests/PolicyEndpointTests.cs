using System.Net;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Api.Tests;

/// <summary>POST/GET /policies plus the input-hardening cases that every endpoint shares (validation errors, malformed JSON, unknown routes).</summary>
[Collection(ApiCollection.Name)]
public sealed class PolicyEndpointTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task CreatePolicy_Returns201WithLocationAndBody()
    {
        var holder = Guid.NewGuid();
        var response = await Manager.PostJsonAsync("/policies", PolicyBody(holderId: holder, coverageType: "home"));

        var policy = await response.ReadOkAsync<PolicyDto>(HttpStatusCode.Created);
        Assert.Equal($"/policies/{policy.Id}", response.Headers.Location?.ToString());
        Assert.Equal(holder, policy.HolderId);
        Assert.Equal(CoverageType.Home, policy.CoverageType);
        Assert.Equal(PolicyStatus.Active, policy.Status);
        Assert.Equal(new MoneyDto(20_000m, "EUR"), policy.CoverageLimit);

        var fetched = await (await Manager.GetAsync(response.Headers.Location!.ToString())).ReadOkAsync<PolicyDto>();
        Assert.Equal(policy, fetched);
    }

    [Fact]
    public async Task GetPolicy_OwnerSeesIt_AdjusterSeesIt()
    {
        var policy = await CreatePolicyAsync();

        Assert.Equal(HttpStatusCode.OK, (await ClaimantFor(policy).GetAsync($"/policies/{policy.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Adjuster.GetAsync($"/policies/{policy.Id}")).StatusCode);
    }

    [Fact]
    public async Task GetPolicy_Unknown_Is404NotFound()
    {
        var response = await Manager.GetAsync($"/policies/{Guid.NewGuid()}");
        await response.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Route_NonGuidId_Is404WithProblem()
    {
        await (await Manager.GetAsync("/policies/not-a-guid")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        await (await Manager.GetAsync("/claims/123")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task CreatePolicy_MissingFields_ReportsAllErrorsAtOnce()
    {
        var response = await Manager.PostJsonAsync("/policies", new { });

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Equal(
            ["coverageLimit", "coverageType", "deductible", "effectiveFrom", "effectiveTo", "holderId", "policyNumber"],
            problem.Errors!.Keys.Order().ToArray());
    }

    [Fact]
    public async Task CreatePolicy_UnknownEnum_ListsAllowedValues()
    {
        var response = await Manager.PostJsonAsync("/policies", PolicyBody(coverageType: "Boat"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Equal("coverageType must be one of: Auto, Home, Health.", problem.Errors!["coverageType"][0]);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1000, 1500)]
    public async Task CreatePolicy_DeductibleNotBelowLimit_Is400(decimal limit, decimal deductible)
    {
        var response = await Manager.PostJsonAsync("/policies", PolicyBody(limit, deductible));

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("lower than coverageLimit", problem.Errors!["deductible"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePolicy_MoreThanTwoDecimals_AndBadPeriod_Are400()
    {
        var response = await Manager.PostJsonAsync("/policies", PolicyBody(coverageLimit: 1000.005m, deductible: 0.001m, from: "2026-05-01", to: "2026-05-01"));

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("two decimals", problem.Errors!["coverageLimit"][0], StringComparison.Ordinal);
        Assert.Contains("two decimals", problem.Errors["deductible"][0], StringComparison.Ordinal);
        Assert.Contains("after effectiveFrom", problem.Errors["effectiveTo"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatePolicy_DuplicatePolicyNumber_Is409DuplicateKey()
    {
        var body = PolicyBody(policyNumber: "POL-DUP-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.Equal(HttpStatusCode.Created, (await Manager.PostJsonAsync("/policies", body)).StatusCode);

        var response = await Manager.PostJsonAsync("/policies", body);
        await response.AssertProblemAsync(HttpStatusCode.Conflict, "duplicate_key");
    }

    [Fact]
    public async Task Request_MalformedJson_Is400InvalidRequest_Not500()
    {
        var response = await Manager.PostRawAsync("/policies", "{ this is not json");
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_request");
        Assert.DoesNotContain("   at ", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_EmptyBody_Is400()
    {
        var response = await Manager.PostEmptyAsync("/policies");
        await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task Request_WrongJsonType_Is400WithPath()
    {
        var response = await Manager.PostRawAsync("/policies", "{\"coverageLimit\": \"NaN\"}");
        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_request");
        Assert.Contains("coverageLimit", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_UnsupportedMediaType_Is415WithProblem()
    {
        var response = await Manager.PostRawAsync("/policies", "policyNumber=1", "text/plain");
        await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
    }

    [Fact]
    public async Task Request_WrongMethod_Is405WithProblem()
    {
        var response = await Manager.DeleteAsync($"/policies/{Guid.NewGuid()}");
        await response.AssertProblemAsync(HttpStatusCode.MethodNotAllowed, "method_not_allowed");
    }

    [Fact]
    public async Task Request_BodyOver64KiB_Is413PayloadTooLarge()
    {
        var response = await Manager.PostRawAsync("/policies", "{\"policyNumber\": \"" + new string('x', 70_000) + "\"}");
        await response.AssertProblemAsync(HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
    }

    [Fact]
    public async Task Response_CarriesSecurityHeaders_NoServerHeader_AndCorrelationId()
    {
        var response = await Manager.GetAsync($"/policies/{Guid.NewGuid()}");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Server"));
        Assert.Single(response.Headers.GetValues("X-Correlation-Id"));
    }

    [Fact]
    public async Task Docs_PageHasNoContentSecurityPolicy()
    {
        var response = await Anonymous.GetAsync("/docs");
        Assert.False(response.Headers.Contains("Content-Security-Policy"));
    }

    [Fact]
    public async Task CorrelationId_IsEchoedWhenSupplied_AndGeneratedOtherwise()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "req-42");
        var echoed = await Anonymous.SendAsync(request);
        Assert.Equal("req-42", echoed.Headers.GetValues("X-Correlation-Id").Single());

        var bad = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        bad.Headers.Add("X-Correlation-Id", "not acceptable because of spaces");
        var generated = await Anonymous.SendAsync(bad);
        var id = generated.Headers.GetValues("X-Correlation-Id").Single();
        Assert.Equal(32, id.Length);

        var problem = await (await Manager.GetAsync($"/policies/{Guid.NewGuid()}")).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Equal(32, problem.CorrelationId!.Length);
    }

    [Fact]
    public async Task Actor_ClaimantWithGuidSubject_IsResolvedFromToken()
    {
        var holder = Guid.NewGuid();
        var policy = await CreatePolicyAsync(holderId: holder);

        var response = await As(ActorRole.Claimant, holder.ToString()).GetAsync($"/policies/{policy.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
