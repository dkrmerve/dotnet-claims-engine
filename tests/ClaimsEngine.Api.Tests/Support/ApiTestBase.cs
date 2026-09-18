using System.Net;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsEngine.Api.Tests.Support;

/// <summary>
/// Base for API tests: a fresh database and application per test (xUnit instantiates the class
/// per test method), plus role-scoped HTTP clients and the recurring scenario steps.
/// Derived classes must carry [Collection(ApiCollection.Name)].
/// </summary>
public abstract class ApiTestBase(DatabaseFixture databases) : IAsyncLifetime
{
    private readonly Dictionary<string, HttpClient> _clients = new(StringComparer.Ordinal);

    protected ClaimsApiFactory Factory { get; private set; } = default!;

    protected TestDatabase Database { get; private set; } = default!;

    protected TestClock Clock => Factory.Clock;

    /// <summary>Anonymous client.</summary>
    protected HttpClient Anonymous { get; private set; } = default!;

    protected virtual IReadOnlyDictionary<string, string?>? Settings => null;

    protected virtual string Environment => "Testing";

    /// <summary>Extra EF options (interceptors, batch size) for this test class.</summary>
    protected virtual Action<DbContextOptionsBuilder>? ConfigureDatabase => null;

    /// <summary>Extra service registrations (for example a log capture) for this test class.</summary>
    protected virtual Action<IServiceCollection>? ConfigureServices => null;

    public virtual async Task InitializeAsync()
    {
        Database = await databases.CreateDatabaseAsync();
        Factory = new ClaimsApiFactory(Database, new FactoryOptions(Settings, ConfigureDatabase, ConfigureServices, Environment));
        Anonymous = Factory.CreateClient();
    }

    public virtual async Task DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        Anonymous.Dispose();
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>A client authenticated as the given role; claimants use their holder id as subject.</summary>
    protected HttpClient As(ActorRole role, string? subject = null)
    {
        subject ??= role.ToString().ToLowerInvariant() + "-1";
        var key = role + "|" + subject;
        if (!_clients.TryGetValue(key, out var client))
        {
            client = Factory.CreateClient().WithBearer(Tokens.For(role, subject));
            _clients[key] = client;
        }

        return client;
    }

    protected HttpClient Manager => As(ActorRole.Manager);

    protected HttpClient Adjuster => As(ActorRole.Adjuster);

    protected HttpClient SeniorAdjuster => As(ActorRole.SeniorAdjuster);

    protected HttpClient ClaimantFor(PolicyDto policy) => As(ActorRole.Claimant, policy.HolderId.ToString());

    protected static object PolicyBody(
        decimal coverageLimit = 20_000m,
        decimal deductible = 500m,
        Guid? holderId = null,
        string? from = "2026-01-01",
        string? to = "2026-12-31",
        string coverageType = "Auto",
        string? policyNumber = null) => new
        {
            policyNumber = policyNumber ?? "POL-" + Guid.NewGuid().ToString("N")[..10],
            holderId = holderId ?? Guid.NewGuid(),
            coverageType,
            coverageLimit,
            deductible,
            effectiveFrom = from,
            effectiveTo = to,
        };

    protected async Task<PolicyDto> CreatePolicyAsync(
        decimal coverageLimit = 20_000m,
        decimal deductible = 500m,
        Guid? holderId = null,
        string from = "2026-01-01",
        string to = "2026-12-31")
    {
        var response = await Manager.PostJsonAsync("/policies", PolicyBody(coverageLimit, deductible, holderId, from, to));
        return await response.ReadOkAsync<PolicyDto>(HttpStatusCode.Created);
    }

    protected Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        Guid policyId,
        decimal amount = 3_000m,
        string? incidentDate = null,
        string? idempotencyKey = null,
        string description = "Rear-ended at a traffic light.") =>
        client.PostJsonAsync(
            "/claims",
            new { policyId, incidentDate = incidentDate ?? Clock.Today.AddDays(-5).ToString("yyyy-MM-dd"), claimedAmount = amount, description },
            idempotencyKey);

    protected async Task<ClaimDto> SubmitClaimAsync(PolicyDto policy, decimal amount = 3_000m, string? incidentDate = null, string? idempotencyKey = null)
    {
        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, amount, incidentDate, idempotencyKey);
        return await response.ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
    }

    protected async Task<ClaimDto> TransitionAsync(HttpClient client, Guid claimId, string action, object? body = null)
    {
        var response = body is null
            ? await client.PostEmptyAsync($"/claims/{claimId}/{action}")
            : await client.PostJsonAsync($"/claims/{claimId}/{action}", body);
        return await response.ReadOkAsync<ClaimDto>();
    }

    protected async Task<ClaimDto> ApprovedClaimAsync(PolicyDto policy, decimal amount = 3_000m, HttpClient? approver = null)
    {
        var claim = await SubmitClaimAsync(policy, amount);
        await TransitionAsync(Adjuster, claim.Id, "review");
        return await TransitionAsync(approver ?? Manager, claim.Id, "approve");
    }

    protected async Task<ClaimDto> GetClaimAsync(Guid claimId, HttpClient? client = null) =>
        await (await (client ?? Manager).GetAsync($"/claims/{claimId}")).ReadOkAsync<ClaimDto>();
}
