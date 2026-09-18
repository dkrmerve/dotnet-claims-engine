using System.Data.Common;
using System.Net;
using ClaimsEngine.Api.Composition;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ClaimsEngine.Api.Tests;

/// <summary>Idempotency keys are scoped to the caller and expired keys are renewed safely under real parallelism.</summary>
[Collection(ApiCollection.Name)]
public sealed class IdempotencyScopeApiTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public async Task Idempotency_SameKeyFromTwoSubjects_CreatesTwoIndependentClaims()
    {
        var policy = await CreatePolicyAsync();
        var key = Guid.NewGuid().ToString();

        var byClaimant = await (await SubmitAsync(ClaimantFor(policy), policy.Id, idempotencyKey: key)).ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
        var byAdjuster = await (await SubmitAsync(Adjuster, policy.Id, idempotencyKey: key)).ReadOkAsync<ClaimDto>(HttpStatusCode.Created);

        Assert.NotEqual(byClaimant.Id, byAdjuster.Id);
        var list = await (await Manager.GetAsync($"/claims?policyId={policy.Id}")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Equal(2, list.TotalCount);
    }

    [PostgresFact]
    public async Task Idempotency_ParallelSubmitsWithSameExpiredKey_RenewExactlyOnce()
    {
        var policy = await CreatePolicyAsync();
        var key = Guid.NewGuid().ToString();
        var incident = Clock.Today.AddDays(-5).ToString("yyyy-MM-dd");
        var original = await (await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: incident, idempotencyKey: key)).ReadOkAsync<ClaimDto>(HttpStatusCode.Created);
        Clock.Advance(TimeSpan.FromHours(25));
        var clients = Enumerable.Range(0, 3).Select(_ => Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Claimant, policy.HolderId.ToString()))).ToList();

        var responses = await Task.WhenAll(clients.Select(c => SubmitAsync(c, policy.Id, incidentDate: incident, idempotencyKey: key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = (await Task.WhenAll(responses.Select(r => r.ReadAsAsync<ClaimDto>()))).Select(c => c.Id).Distinct().ToList();
        var renewed = Assert.Single(ids);
        Assert.NotEqual(original.Id, renewed);
        var list = await (await Manager.GetAsync($"/claims?policyId={policy.Id}")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Equal(2, list.TotalCount);
    }
}

/// <summary>Rate limiting is per authenticated subject; anonymous calls fall back to the client address.</summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitPartitionTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["RateLimiting:PermitLimit"] = "2",
        ["RateLimiting:WindowSeconds"] = "60",
    };

    [Fact]
    public async Task RateLimit_IsPartitionedBySubject_NotSharedAcrossCallersOnOneAddress()
    {
        var policy = await CreatePolicyAsync();
        var first = As(ActorRole.Adjuster, "adjuster-a");
        var second = As(ActorRole.Adjuster, "adjuster-b");

        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(first, policy.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(first, policy.Id)).StatusCode);
        await (await SubmitAsync(first, policy.Id)).AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate_limited");

        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(second, policy.Id)).StatusCode);
    }

    [Fact]
    public async Task RateLimit_ForwardedForIsIgnored_WhenProxyIsNotTrusted()
    {
        var problem = await AnonymousTokenCalls("10.0.0.1", "10.0.0.2", "10.0.0.3");
        Assert.Equal("rate_limited", problem.Code);
    }

    private async Task<ProblemResponse> AnonymousTokenCalls(params string[] forwardedFor)
    {
        HttpResponseMessage last = null!;
        foreach (var address in forwardedFor)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { subject = "x", role = "Manager" }),
            };
            request.Headers.Add("X-Forwarded-For", address);
            last = await Anonymous.SendAsync(request);
        }

        return await last.ReadAsAsync<ProblemResponse>();
    }
}

/// <summary>With Proxy:TrustForwardedHeaders the real client address from X-Forwarded-For partitions anonymous calls.</summary>
[Collection(ApiCollection.Name)]
public sealed class TrustedProxyTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["RateLimiting:PermitLimit"] = "2",
        ["RateLimiting:WindowSeconds"] = "60",
        ["Proxy:TrustForwardedHeaders"] = "true",
    };

    [Fact]
    public async Task RateLimit_UsesForwardedFor_WhenProxyIsTrusted()
    {
        Assert.Equal(HttpStatusCode.OK, (await TokenFrom("10.0.0.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TokenFrom("10.0.0.1")).StatusCode);
        await (await TokenFrom("10.0.0.1")).AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate_limited");

        Assert.Equal(HttpStatusCode.OK, (await TokenFrom("10.0.0.2")).StatusCode);
    }

    private Task<HttpResponseMessage> TokenFrom(string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { subject = "x", role = "Manager" }),
        };
        request.Headers.Add("X-Forwarded-For", address);
        return Anonymous.SendAsync(request);
    }
}

/// <summary>Operational plumbing: probe requests are not logged at Information, the sweeper removes expired keys.</summary>
[Collection(ApiCollection.Name)]
public sealed class ProbeLoggingAndSweeperTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    private readonly CapturingLoggerProvider _logs = new();

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["Idempotency:SweepIntervalMinutes"] = "1",
        ["Logging:LogLevel:Default"] = "Debug",
    };

    protected override Action<IServiceCollection> ConfigureServices => services => services.AddLogging(logging => logging.AddProvider(_logs));

    [Fact]
    public async Task RequestLogging_HealthAndMetricsProbes_AreDebugNotInformation()
    {
        await Anonymous.GetAsync("/health/live");
        await Anonymous.GetAsync("/health/ready");
        await Anonymous.GetAsync("/metrics");
        await Manager.GetAsync("/claims");

        var requestLog = _logs.Entries.Where(e => e.Category.EndsWith("RequestLoggingMiddleware", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(requestLog, e => e.Level == LogLevel.Information && (e.Message.Contains("/health", StringComparison.Ordinal) || e.Message.Contains("/metrics", StringComparison.Ordinal)));
        Assert.Contains(requestLog, e => e.Level == LogLevel.Debug && e.Message.Contains("/health/live", StringComparison.Ordinal));
        Assert.Contains(requestLog, e => e.Level == LogLevel.Information && e.Message.Contains("GET /claims responded 200", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sweeper_RunsAtStartup_AndDeletesOnlyExpiredKeys()
    {
        Assert.Contains(_logs.Entries, e => e.Message.StartsWith("Idempotency key sweep removed", StringComparison.Ordinal));

        var policy = await CreatePolicyAsync();
        var incident = Clock.Today.AddDays(-5).ToString("yyyy-MM-dd");
        await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: incident, idempotencyKey: "old-key");
        Clock.Advance(TimeSpan.FromHours(30));
        await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: incident, idempotencyKey: "fresh-key");
        var sweeper = Factory.Services.GetServices<IHostedService>().OfType<IdempotencySweeper>().Single();

        var deleted = await sweeper.SweepOnceAsync();

        Assert.Equal(1, deleted);
        var replayed = await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: incident, idempotencyKey: "fresh-key");
        Assert.Equal("true", replayed.Headers.GetValues("Idempotent-Replayed").Single());
        var recreated = await SubmitAsync(ClaimantFor(policy), policy.Id, incidentDate: incident, idempotencyKey: "old-key");
        Assert.False(recreated.Headers.Contains("Idempotent-Replayed"));
    }
}

/// <summary>The unit of work: the work runs exactly once on success, and a commit-phase failure is never retried.</summary>
[Collection(ApiCollection.Name)]
public sealed class UnitOfWorkTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    private readonly CommitFailureInterceptor _commit = new();

    protected override Action<DbContextOptionsBuilder> ConfigureDatabase => options => options.AddInterceptors(_commit);

    [Fact]
    public async Task UnitOfWork_WorkRunsOnce_WhenCommitSucceeds()
    {
        using var scope = Factory.Services.CreateScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var runs = 0;

        var result = await unit.RunInTransactionAsync(ct =>
        {
            runs++;
            return Task.FromResult(42);
        });

        Assert.Equal(42, result);
        Assert.Equal(1, runs);
    }

    [PostgresFact]
    public async Task UnitOfWork_TransientFailureDuringCommit_IsNotRetried_AndSurfacesAsUnknownOutcome()
    {
        using var scope = Factory.Services.CreateScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var runs = 0;
        _commit.FailNextCommitTransiently = true;

        var ex = await Assert.ThrowsAsync<CommitOutcomeUnknownException>(() => unit.RunInTransactionAsync(ct =>
        {
            runs++;
            return Task.FromResult(0);
        }));

        Assert.Equal(1, runs);
        Assert.IsType<NpgsqlException>(ex.InnerException);
        Assert.True(((NpgsqlException)ex.InnerException).IsTransient);
    }

    [PostgresFact]
    public async Task Pay_WhenCommitOutcomeIsUnknown_Is500_AndNothingIsPersisted()
    {
        var claim = await ApprovedClaimAsync(await CreatePolicyAsync());
        _commit.FailNextCommitTransiently = true;

        var response = await Manager.PostEmptyAsync($"/claims/{claim.Id}/pay");

        await response.AssertProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(claim.Status, (await GetClaimAsync(claim.Id)).Status);
    }

    /// <summary>Simulates a network failure exactly at COMMIT: the kind of error the retrying strategy would otherwise re-run.</summary>
    private sealed class CommitFailureInterceptor : DbTransactionInterceptor
    {
        public bool FailNextCommitTransiently { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (FailNextCommitTransiently)
            {
                FailNextCommitTransiently = false;
                throw new NpgsqlException("simulated connection loss during COMMIT", new IOException("connection reset"));
            }

            return ValueTask.FromResult(result);
        }
    }
}
