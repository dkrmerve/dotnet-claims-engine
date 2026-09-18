using System.Net;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ClaimsEngine.Api.Tests;

/// <summary>
/// Real parallelism against real PostgreSQL: optimistic concurrency (Version + xmin),
/// the idempotency unique index, database constraints, and transaction atomicity.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PostgresConcurrencyTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [PostgresFact]
    public async Task Concurrency_ParallelPaysOnSameClaim_ExactlyOneSucceeds()
    {
        var claim = await ApprovedClaimAsync(await CreatePolicyAsync());
        var a = Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Manager, "m1"));
        var b = Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Manager, "m2"));

        var responses = await Task.WhenAll(a.PostEmptyAsync($"/claims/{claim.Id}/pay"), b.PostEmptyAsync($"/claims/{claim.Id}/pay"));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        var problem = await loser.ReadAsAsync<ProblemResponse>();
        Assert.Equal(409, problem.Status);
        Assert.Contains(problem.Code, new[] { "concurrency_conflict", "invalid_transition" });

        var final = await GetClaimAsync(claim.Id);
        Assert.Equal(ClaimStatus.Paid, final.Status);
        Assert.Single(await History(claim.Id), h => h.ToStatus == ClaimStatus.Paid);
    }

    [PostgresFact]
    public async Task Concurrency_ParallelPaysOnOnePolicyExceedingLimit_NeverOvershoot()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 10_000m, deductible: 0m);
        var first = await ApprovedClaimAsync(policy, amount: 7_000m);
        var second = await ApprovedClaimAsync(policy, amount: 5_000m);
        var a = Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Manager, "m1"));
        var b = Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Manager, "m2"));

        var responses = await Task.WhenAll(a.PostEmptyAsync($"/claims/{first.Id}/pay"), b.PostEmptyAsync($"/claims/{second.Id}/pay"));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var loser = await responses.Single(r => r.StatusCode != HttpStatusCode.OK).ReadAsAsync<ProblemResponse>();
        Assert.Contains(loser.Code, new[] { "concurrency_conflict", "limit_exhausted" });

        // The losing claim, retried after the race, hits the limit re-check deterministically.
        var losing = (await GetClaimAsync(first.Id)).Status == ClaimStatus.Paid ? second : first;
        await (await Manager.PostEmptyAsync($"/claims/{losing.Id}/pay")).AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "limit_exhausted");

        var refreshed = await (await Manager.GetAsync($"/policies/{policy.Id}")).ReadOkAsync<PolicyDto>();
        Assert.True(refreshed.LifetimePaid.Amount <= 10_000m);
    }

    [PostgresFact]
    public async Task Concurrency_ParallelSubmitsWithSameIdempotencyKey_CreateExactlyOneClaim()
    {
        var policy = await CreatePolicyAsync();
        var key = Guid.NewGuid().ToString();
        var clients = Enumerable.Range(0, 4).Select(_ => Factory.CreateClient().WithBearer(Tokens.For(ActorRole.Claimant, policy.HolderId.ToString()))).ToList();

        var responses = await Task.WhenAll(clients.Select(c => SubmitAsync(c, policy.Id, idempotencyKey: key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(r => r.ReadAsAsync<ClaimDto>()));
        Assert.Single(ids.Select(c => c.Id).Distinct());

        var list = await (await Manager.GetAsync($"/claims?policyId={policy.Id}")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Equal(1, list.TotalCount);
    }

    [PostgresFact]
    public async Task Concurrency_TwoContextsLoadSameClaim_SecondSaveIsConcurrencyConflict()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        using var scopeA = Factory.Services.CreateScope();
        using var scopeB = Factory.Services.CreateScope();
        var claimsA = scopeA.ServiceProvider.GetRequiredService<IClaimRepository>();
        var claimsB = scopeB.ServiceProvider.GetRequiredService<IClaimRepository>();
        var unitA = scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var unitB = scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var loadedB = await claimsB.GetAsync(new ClaimId(claim.Id));
        await unitA.RunInTransactionAsync(async ct =>
        {
            var loadedA = await claimsA.GetAsync(new ClaimId(claim.Id), ct);
            loadedA!.StartReview(ActorRole.Adjuster, Clock.UtcNow);
            return 0;
        });

        // B still holds the stale row (Version 1, old xmin) and must not be allowed to overwrite A's write.
        var scopeBDb = scopeB.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        loadedB!.StartReview(ActorRole.Adjuster, Clock.UtcNow);
        var ex = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => scopeBDb.SaveChangesAsync());
        Assert.NotNull(ex);

        var unitOfWorkMapping = await Assert.ThrowsAsync<ConcurrencyException>(() => unitB.RunInTransactionAsync(async ct =>
        {
            scopeBDb.ChangeTracker.Clear();
            var stale = await claimsB.GetAsync(new ClaimId(claim.Id), ct);
            await scopeBDb.Database.ExecuteSqlAsync($"UPDATE claims SET \"Version\" = \"Version\" + 1 WHERE \"Id\" = {claim.Id}", ct);
            stale!.Approve(ActorRole.Manager, ClaimRules.Default, Clock.UtcNow);
            return 0;
        }));
        Assert.Equal("concurrency_conflict", unitOfWorkMapping.Code);
    }

    [PostgresFact]
    public async Task Concurrency_XminChangesOnEveryWrite()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        var before = await ReadXminAsync(claim.Id);

        await TransitionAsync(Adjuster, claim.Id, "review");

        Assert.NotEqual(before, await ReadXminAsync(claim.Id));
    }

    [PostgresFact]
    public async Task Constraint_DuplicateIdempotencyKey_IsRejectedByUniqueIndex()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        const string insert = "INSERT INTO idempotency_keys (\"Subject\", \"Key\", \"PayloadHash\", \"ClaimId\", \"CreatedAt\", \"Version\") VALUES ('alice', 'dup-key', 'hash', {0}, now(), 1)";

        await Execute(insert, claim.Id);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(insert, claim.Id));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [PostgresFact]
    public async Task Constraint_NegativeClaimedAmount_IsRejectedByCheckConstraint()
    {
        var claim = await SubmitClaimAsync(await CreatePolicyAsync());
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute("UPDATE claims SET \"ClaimedAmount\" = -1 WHERE \"Id\" = {0}", claim.Id));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Contains("ck_claims_claimed_amount_positive", ex.MessageText, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Constraint_DeductibleAboveLimit_IsRejectedByCheckConstraint()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 1_000m, deductible: 100m);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute("UPDATE policies SET \"Deductible\" = 5000 WHERE \"Id\" = {0}", policy.Id));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [PostgresFact]
    public async Task Constraint_PolicyWithClaims_CannotBeDeleted_OnDeleteRestrict()
    {
        var policy = await CreatePolicyAsync();
        await SubmitClaimAsync(policy);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute("DELETE FROM policies WHERE \"Id\" = {0}", policy.Id));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [PostgresFact]
    public async Task Constraint_ClaimForUnknownPolicy_IsRejectedByForeignKey()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Execute(
            "INSERT INTO claims (\"Id\", \"PolicyId\", \"IncidentDate\", \"FiledAt\", \"ClaimedAmount\", \"Description\", \"Status\", \"EligiblePayout\", \"Flag\", \"Version\") " +
            "VALUES ({0}, {1}, '2026-03-01', now(), 10, 'x', 'Submitted', 0, 'None', 1)",
            Guid.NewGuid(),
            Guid.NewGuid()));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    private async Task<List<ClaimHistoryEntryDto>> History(Guid claimId) =>
        await (await Manager.GetAsync($"/claims/{claimId}/history")).ReadOkAsync<List<ClaimHistoryEntryDto>>();

    private async Task<uint> ReadXminAsync(Guid claimId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        return await db.Claims.Where(c => c.Id == new ClaimId(claimId)).Select(c => EF.Property<uint>(c, "xmin")).SingleAsync();
    }

    private async Task Execute(string sql, params object[] parameters)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
        await db.Database.ExecuteSqlRawAsync(sql, parameters);
    }
}

/// <summary>Atomicity: a failure after the first write inside a use case leaves nothing behind (MaxBatchSize 1 so each row is its own command).</summary>
[Collection(ApiCollection.Name)]
public sealed class PostgresAtomicityTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    private readonly FailingCommandInterceptor _interceptor = new();

    protected override Action<DbContextOptionsBuilder> ConfigureDatabase => options =>
    {
        if (Database.IsPostgres)
        {
            options.UseNpgsql(Database.ConnectionString, npgsql => npgsql.MaxBatchSize(1));
        }

        options.AddInterceptors(_interceptor);
    };

    [PostgresFact]
    public async Task Atomicity_PayFailingAfterFirstWrite_PersistsNothing()
    {
        var policy = await CreatePolicyAsync(coverageLimit: 20_000m, deductible: 0m);
        var claim = await ApprovedClaimAsync(policy, amount: 4_000m);
        _interceptor.Armed = true;
        _interceptor.FailOnWriteNumber = 2;

        var response = await Manager.PostEmptyAsync($"/claims/{claim.Id}/pay");
        _interceptor.Armed = false;

        await response.AssertProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(1, _interceptor.Failures);
        var after = await GetClaimAsync(claim.Id);
        Assert.Equal(ClaimStatus.Approved, after.Status);
        Assert.Equal(claim.Version, after.Version);
        Assert.DoesNotContain(await (await Manager.GetAsync($"/claims/{claim.Id}/history")).ReadOkAsync<List<ClaimHistoryEntryDto>>(), h => h.ToStatus == ClaimStatus.Paid);
        var refreshed = await (await Manager.GetAsync($"/policies/{policy.Id}")).ReadOkAsync<PolicyDto>();
        Assert.Equal(0m, refreshed.LifetimePaid.Amount);
        Assert.Equal(policy.Version, refreshed.Version);
    }

    [PostgresFact]
    public async Task Atomicity_SubmitFailingAfterFirstWrite_PersistsNeitherClaimNorIdempotencyKey()
    {
        var policy = await CreatePolicyAsync();
        _interceptor.Armed = true;
        _interceptor.FailOnWriteNumber = 2;
        var key = Guid.NewGuid().ToString();

        var response = await SubmitAsync(ClaimantFor(policy), policy.Id, idempotencyKey: key);
        _interceptor.Armed = false;

        await response.AssertProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        var list = await (await Manager.GetAsync($"/claims?policyId={policy.Id}")).ReadOkAsync<PagedResponse<ClaimDto>>();
        Assert.Equal(0, list.TotalCount);

        // The key was never stored, so the retry creates the claim normally.
        var retry = await SubmitAsync(ClaimantFor(policy), policy.Id, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(retry.Headers.Contains("Idempotent-Replayed"));
    }
}
