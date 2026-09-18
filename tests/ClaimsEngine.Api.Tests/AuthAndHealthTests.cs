using System.Net;
using System.Net.Http.Json;
using ClaimsEngine.Api.Auth;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Api.Tests;

/// <summary>JWT authentication (401), role policies (403 forbidden_role), the dev token issuer, and the anonymous operational endpoints.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthAndHealthTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task Auth_NoToken_Is401Unauthenticated()
    {
        var response = await Anonymous.GetAsync("/claims");
        await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Auth_ExpiredToken_Is401()
    {
        var client = Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Manager, "m", lifetime: TimeSpan.FromMinutes(5), issuedAt: DateTimeOffset.UtcNow.AddHours(-2)));

        var response = await client.GetAsync("/claims");

        var problem = await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.Contains("expired", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Auth_WrongSigningKey_Is401()
    {
        var client = Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Manager, "m", signingKey: "another-key-another-key-another-key-123456"));
        var response = await client.GetAsync("/claims");
        await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Auth_GarbageToken_Is401()
    {
        var client = Factory.CreateClient().WithBearer("not.a.jwt");
        await (await client.GetAsync("/claims")).AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Auth_ValidTokenWrongRole_Is403ForbiddenRole()
    {
        var policy = await CreatePolicyAsync();
        var claim = await SubmitClaimAsync(policy);

        await (await ClaimantFor(policy).GetAsync("/claims/overdue")).AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden_role");
        await (await Adjuster.PostJsonAsync("/policies", PolicyBody())).AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden_role");
        await (await Adjuster.PostEmptyAsync($"/claims/{claim.Id}/pay")).AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden_role");
        await (await Adjuster.PostEmptyAsync($"/claims/{claim.Id}/withdraw")).AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden_role");
        await (await SeniorAdjuster.PostJsonAsync($"/claims/{claim.Id}/clear-flag", new { note = "x" })).AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden_role");
    }

    [Fact]
    public async Task Auth_TokenWithUnknownRole_Is403()
    {
        var client = Factory.CreateClient().WithBearer(Tokens.For(ActorRole.System, "sys"));
        await (await client.GetAsync("/claims")).AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden_role");
    }

    [Fact]
    public async Task Auth_ClaimantReadingSomeoneElsesClaim_Is403NotOwner()
    {
        var policy = await CreatePolicyAsync();
        var claim = await SubmitClaimAsync(policy);
        var stranger = As(ActorRole.Claimant, Guid.NewGuid().ToString());

        await (await stranger.GetAsync($"/claims/{claim.Id}")).AssertProblemAsync(HttpStatusCode.Forbidden, "not_owner");
        await (await stranger.GetAsync($"/claims/{claim.Id}/history")).AssertProblemAsync(HttpStatusCode.Forbidden, "not_owner");
        await (await stranger.GetAsync($"/policies/{policy.Id}")).AssertProblemAsync(HttpStatusCode.Forbidden, "not_owner");
        await (await SubmitAsync(stranger, policy.Id)).AssertProblemAsync(HttpStatusCode.Forbidden, "not_owner");
        await (await stranger.PostEmptyAsync($"/claims/{claim.Id}/withdraw")).AssertProblemAsync(HttpStatusCode.Forbidden, "not_owner");
    }

    [Fact]
    public async Task Auth_StrayRoleHeaderOnGet_IsIgnored()
    {
        var policy = await CreatePolicyAsync();
        var client = ClaimantFor(policy);
        client.DefaultRequestHeaders.Add("X-Actor-Role", "Manager");

        await (await client.GetAsync("/claims/overdue")).AssertProblemAsync(HttpStatusCode.Forbidden, "forbidden_role");
    }

    [Fact]
    public async Task DevIssuer_IssuesTokenThatAuthenticates()
    {
        // The dev issuer stamps tokens with IClock; JWT validation uses wall-clock time, so align the test clock.
        Clock.UtcNow = DateTimeOffset.UtcNow;
        var response = await Anonymous.PostJsonAsync("/auth/token", new { subject = "alice", role = "adjuster" });
        var token = await response.ReadOkAsync<DevTokenResponse>();

        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(3600, token.ExpiresInSeconds);
        Assert.Equal("Adjuster", token.Role);

        var client = Factory.CreateClient().WithBearer(token.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/claims")).StatusCode);
    }

    [Fact]
    public async Task DevIssuer_InvalidRequest_Is400WithFieldErrors()
    {
        var response = await Anonymous.PostJsonAsync("/auth/token", new { subject = "", role = "Admin" });

        var problem = await response.AssertProblemAsync(HttpStatusCode.BadRequest, "request_validation_failed");
        Assert.Contains("subject", problem.Errors!.Keys);
        Assert.Contains("Claimant, Adjuster, SeniorAdjuster, Manager", problem.Errors["role"][0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_LiveAndReady_Are200AndAnonymous()
    {
        var live = await Anonymous.GetAsync("/health/live");
        var ready = await Anonymous.GetAsync("/health/ready");
        var overall = await Anonymous.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overall.StatusCode);
        var readyBody = await ready.Content.ReadFromJsonAsync<HealthBody>(Json.Options);
        Assert.Equal("Healthy", readyBody!.Status);
        Assert.Contains(readyBody.Checks, c => c.Name == "database" && c.Status == "Healthy");
    }

    [Fact]
    public async Task Docs_OpenApiDocumentAndUiAndMetrics_AreAnonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await Anonymous.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Anonymous.GetAsync("/docs")).StatusCode);
        var metrics = await Anonymous.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Contains("http_requests_received_total", await metrics.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed record HealthBody(string Status, List<HealthEntry> Checks);

    private sealed record HealthEntry(string Name, string Status);
}

/// <summary>With the dev issuer disabled (the default), the token endpoint does not exist.</summary>
[Collection(ApiCollection.Name)]
public sealed class DevIssuerDisabledTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Auth:DevIssuer:Enabled"] = "false",
    };

    [Fact]
    public async Task DevIssuer_WhenDisabled_TokenEndpointIs404()
    {
        var response = await Anonymous.PostJsonAsync("/auth/token", new { subject = "alice", role = "Manager" });
        await response.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }
}

/// <summary>Tokens that pass the role policy but carry no usable identity, and the external-issuer mode rejecting unknown tokens.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthEdgeTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task Auth_TokenWithoutSubClaim_Is401()
    {
        var descriptor = new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = Tokens.Issuer.Issuer,
            Audience = Tokens.Issuer.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["role"] = "Manager" },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(ClaimsApiFactory.SigningKey)),
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        };
        var client = Factory.CreateClient().WithBearer(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor));

        await (await client.GetAsync("/claims")).AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task ExternalIssuerMode_RejectsDevTokens()
    {
        await using var factory = new ClaimsApiFactory(Database, new FactoryOptions(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = "https://127.0.0.1:1/",
            ["Auth:Audience"] = "claims-engine",
            ["Auth:DevIssuer:Enabled"] = "false",
        }));
        using var client = factory.CreateClient().WithBearer(Tokens.For(ActorRole.Manager, "m"));

        await (await client.GetAsync("/claims")).AssertProblemAsync(HttpStatusCode.Unauthorized, "unauthenticated");
    }
}
