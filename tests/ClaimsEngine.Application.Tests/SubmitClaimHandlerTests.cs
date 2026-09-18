using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Application.Tests.Support;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Tests;

/// <summary>Rule 10 (idempotent submit) and the portfolio facts the handler feeds into the domain.</summary>
public sealed class SubmitClaimHandlerTests
{
    [Fact]
    public async Task Submit_HappyPath_PersistsClaimAndCommitsOnce()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();

        var result = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy));

        Assert.False(result.Replayed);
        Assert.Equal(ClaimStatus.Submitted, result.Claim.Status);
        Assert.Equal(2_500m, result.Claim.EligiblePayout.Amount);
        Assert.Single(s.Claims.Claims);
        Assert.Equal(1, s.UnitOfWork.Commits);
        Assert.Empty(s.Idempotency.Records);
    }

    [Fact]
    public async Task Submit_UnknownPolicy_IsNotFound()
    {
        var s = new Scenario();
        var command = new SubmitClaimCommand(Guid.NewGuid(), s.Clock.Today, 100m, "x", null, Scenario.Manager);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => s.SubmitHandler().HandleAsync(command));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public async Task Submit_ClaimantOnSomeoneElsesPolicy_IsNotOwner()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();

        var ex = await Assert.ThrowsAsync<NotOwnerException>(() => s.SubmitHandler().HandleAsync(s.SubmitCommand(policy, actor: Scenario.Stranger)));

        Assert.Equal("not_owner", ex.Code);
        Assert.Empty(s.Claims.Claims);
    }

    [Fact]
    public async Task Submit_AdjusterOnBehalfOfHolder_IsAllowed()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();

        var result = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy, actor: Scenario.Adjuster));
        Assert.Equal(ClaimStatus.Submitted, result.Claim.Status);
    }

    [Fact]
    public async Task Submit_NegativeAmount_IsValidationError()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();

        await Assert.ThrowsAsync<ValidationException>(() => s.SubmitHandler().HandleAsync(s.SubmitCommand(policy, amount: -5m)));
    }

    [Fact]
    public async Task Submit_IncidentOutsidePolicy_IsPolicyNotEligible()
    {
        var s = new Scenario();
        var policy = s.AddPolicy(from: new DateOnly(2026, 3, 12));
        var command = s.SubmitCommand(policy) with { IncidentDate = new DateOnly(2026, 3, 11) };

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => s.SubmitHandler().HandleAsync(command));
        Assert.Equal("policy_not_eligible", ex.Code);
    }

    [Fact]
    public async Task Submit_AlreadyPaidInPolicyYear_ReducesEligiblePayout()
    {
        var s = new Scenario();
        var policy = s.AddPolicy(coverageLimit: 10_000m, deductible: 0m);
        s.AddClaim(policy, claimed: 7_000m, status: ClaimStatus.Paid);

        var result = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy, amount: 5_000m));

        Assert.Equal(3_000m, result.Claim.EligiblePayout.Amount);
    }

    [Fact]
    public async Task Submit_PaidClaimFromPreviousPolicyYear_DoesNotConsumeCurrentYear()
    {
        var s = new Scenario();
        var policy = s.AddPolicy(coverageLimit: 10_000m, deductible: 0m, from: new DateOnly(2025, 3, 1), to: new DateOnly(2027, 2, 28));
        s.AddClaim(policy, claimed: 7_000m, incidentDate: new DateOnly(2026, 2, 28), status: ClaimStatus.Paid);

        var result = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy, amount: 4_000m));

        Assert.Equal(4_000m, result.Claim.EligiblePayout.Amount);
        Assert.Equal(ClaimStatus.Submitted, result.Claim.Status);
    }

    [Fact]
    public async Task Submit_ThreeOtherClaimsInWindow_FlagsClaim_WithdrawnExcluded()
    {
        var s = new Scenario();
        var holder = HolderId.New();
        var policy = s.AddPolicy(holder: holder);
        var otherPolicy = s.AddPolicy(holder: holder);
        s.AddClaim(policy);
        s.AddClaim(otherPolicy);
        s.AddClaim(policy, status: ClaimStatus.Withdrawn);

        var notFlagged = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy));
        Assert.Equal(ClaimFlag.None, notFlagged.Claim.Flag);

        var flagged = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy));
        Assert.Equal(ClaimFlag.RequiresInvestigation, flagged.Claim.Flag);
    }

    [Fact]
    public async Task Submit_ClaimFiledExactly365DaysAgo_IsInsideTheWindow()
    {
        var s = new Scenario();
        var holder = HolderId.New();
        var policy = s.AddPolicy(holder: holder, from: new DateOnly(2025, 1, 1), to: new DateOnly(2026, 12, 31));
        var edge = s.Clock.UtcNow - TimeSpan.FromDays(365);
        for (var i = 0; i < 3; i++)
        {
            s.AddClaim(policy, incidentDate: new DateOnly(2025, 3, 1), filedAt: edge);
        }

        var result = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy));
        Assert.Equal(ClaimFlag.RequiresInvestigation, result.Claim.Flag);

        s.Claims.Claims.Clear();
        for (var i = 0; i < 3; i++)
        {
            s.AddClaim(policy, incidentDate: new DateOnly(2025, 3, 1), filedAt: edge.AddSeconds(-1));
        }

        var outside = await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy));
        Assert.Equal(ClaimFlag.None, outside.Claim.Flag);
    }

    [Fact]
    public async Task Idempotency_SameKeySamePayload_ReplaysOriginalClaim()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var handler = s.SubmitHandler();

        var first = await handler.HandleAsync(s.SubmitCommand(policy, key: "k1"));
        var second = await handler.HandleAsync(s.SubmitCommand(policy, key: "k1"));

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Claim.Id, second.Claim.Id);
        Assert.Single(s.Claims.Claims);
        Assert.Single(s.Idempotency.Records);
    }

    [Fact]
    public async Task Idempotency_SameKeyDifferentPayload_IsIdempotencyKeyReused()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var handler = s.SubmitHandler();
        await handler.HandleAsync(s.SubmitCommand(policy, key: "k1"));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => handler.HandleAsync(s.SubmitCommand(policy, amount: 3_001m, key: "k1")));

        Assert.Equal("idempotency_key_reused", ex.Code);
        Assert.Single(s.Claims.Claims);
    }

    [Fact]
    public async Task Idempotency_KeyReusedAcrossPolicies_IsIdempotencyKeyReused()
    {
        var s = new Scenario();
        var holder = HolderId.New();
        var policyA = s.AddPolicy(holder: holder);
        var policyB = s.AddPolicy(holder: holder);
        var handler = s.SubmitHandler();
        await handler.HandleAsync(s.SubmitCommand(policyA, key: "shared"));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => handler.HandleAsync(s.SubmitCommand(policyB, key: "shared")));
        Assert.Equal("idempotency_key_reused", ex.Code);
    }

    [Fact]
    public async Task Idempotency_ExpiredKey_CreatesNewClaimAndRenewsRecord()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var handler = s.SubmitHandler();
        var first = await handler.HandleAsync(s.SubmitCommand(policy, key: "k1"));

        s.Clock.Advance(s.IdempotencySettings.TimeToLive + TimeSpan.FromSeconds(1));
        var second = await handler.HandleAsync(s.SubmitCommand(policy, key: "k1"));

        Assert.False(second.Replayed);
        Assert.NotEqual(first.Claim.Id, second.Claim.Id);
        var record = Assert.Single(s.Idempotency.Records).Value;
        Assert.Equal(new ClaimId(second.Claim.Id), record.ClaimId);
        Assert.Equal(s.Clock.UtcNow, record.CreatedAt);
    }

    [Fact]
    public async Task Idempotency_KeyAtExactlyTtl_StillReplays()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var handler = s.SubmitHandler();
        var command = s.SubmitCommand(policy, key: "k1");
        var first = await handler.HandleAsync(command);

        s.Clock.Advance(s.IdempotencySettings.TimeToLive);
        var second = await handler.HandleAsync(command);

        Assert.True(second.Replayed);
        Assert.Equal(first.Claim.Id, second.Claim.Id);
    }

    [Fact]
    public async Task Idempotency_DuplicateKeyOnCommit_ReplaysWhatTheWinnerStored()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var winner = s.AddClaim(policy);
        var command = s.SubmitCommand(policy, key: "race");
        s.Idempotency.Records[FakeIdempotencyStore.KeyOf(command.Actor.Subject, "race")] = new IdempotencyRecord(command.Actor.Subject, "race", SubmitClaimHandler.HashPayload(command), winner.Id, s.Clock.UtcNow);
        var store = new RaceIdempotencyStore(s.Idempotency);
        s.UnitOfWork.FailCommitWith = new DuplicateKeyException("dup");
        var handler = new SubmitClaimHandler(s.Policies, s.Claims, store, s.UnitOfWork, s.Clock, s.Rules, s.IdempotencySettings);

        var result = await handler.HandleAsync(command);

        Assert.True(result.Replayed);
        Assert.Equal(winner.Id.Value, result.Claim.Id);
    }

    [Fact]
    public async Task Idempotency_DuplicateKeyOnCommitWithDifferentPayload_IsIdempotencyKeyReused()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var winner = s.AddClaim(policy);
        var subject = Scenario.ClaimantFor(policy).Subject;
        s.Idempotency.Records[FakeIdempotencyStore.KeyOf(subject, "race")] = new IdempotencyRecord(subject, "race", "OTHER-HASH", winner.Id, s.Clock.UtcNow);
        var store = new RaceIdempotencyStore(s.Idempotency);
        s.UnitOfWork.FailCommitWith = new DuplicateKeyException("dup");
        var handler = new SubmitClaimHandler(s.Policies, s.Claims, store, s.UnitOfWork, s.Clock, s.Rules, s.IdempotencySettings);

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => handler.HandleAsync(s.SubmitCommand(policy, key: "race")));
        Assert.Equal("idempotency_key_reused", ex.Code);
    }

    [Fact]
    public async Task Idempotency_DuplicateKeyOnCommitButNothingStored_IsNotFound()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        s.UnitOfWork.FailCommitWith = new DuplicateKeyException("dup");

        await Assert.ThrowsAsync<NotFoundException>(() => new SubmitClaimHandler(s.Policies, s.Claims, new RaceIdempotencyStore(s.Idempotency), s.UnitOfWork, s.Clock, s.Rules, s.IdempotencySettings).HandleAsync(s.SubmitCommand(policy, key: "ghost")));
    }

    [Fact]
    public async Task Idempotency_DuplicateKeyWithoutIdempotencyKey_Propagates()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        s.UnitOfWork.FailCommitWith = new DuplicateKeyException("dup");

        await Assert.ThrowsAsync<DuplicateKeyException>(() => s.SubmitHandler().HandleAsync(s.SubmitCommand(policy)));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Idempotency_BlankKey_IsTreatedAsAbsent(string key)
    {
        var s = new Scenario();
        var policy = s.AddPolicy();

        await s.SubmitHandler().HandleAsync(s.SubmitCommand(policy, key: key));

        Assert.Empty(s.Idempotency.Records);
    }

    [Fact]
    public void HashPayload_IsCanonical_TrimsDescriptionAndNormalisesAmount()
    {
        var policyId = Guid.NewGuid();
        var actor = Scenario.Manager;
        var a = new SubmitClaimCommand(policyId, new DateOnly(2026, 3, 1), 100m, "desc", "k", actor);
        var b = new SubmitClaimCommand(policyId, new DateOnly(2026, 3, 1), 100.00m, "  desc  ", "other-key", Scenario.Adjuster);
        var c = new SubmitClaimCommand(policyId, new DateOnly(2026, 3, 1), 100.01m, "desc", "k", actor);
        var d = new SubmitClaimCommand(policyId, new DateOnly(2026, 3, 2), 100m, "desc", "k", actor);

        Assert.Equal(SubmitClaimHandler.HashPayload(a), SubmitClaimHandler.HashPayload(b));
        Assert.NotEqual(SubmitClaimHandler.HashPayload(a), SubmitClaimHandler.HashPayload(c));
        Assert.NotEqual(SubmitClaimHandler.HashPayload(a), SubmitClaimHandler.HashPayload(d));
        Assert.Equal(64, SubmitClaimHandler.HashPayload(a).Length);
    }

    /// <summary>Hides the record during the transaction (as if another request inserted it concurrently) and reveals it for the replay.</summary>
    private sealed class RaceIdempotencyStore(FakeIdempotencyStore inner) : IIdempotencyStore
    {
        private int _calls;

        public Task<IdempotencyRecord?> FindAsync(string subject, string key, CancellationToken cancellationToken = default) =>
            ++_calls == 1 ? Task.FromResult<IdempotencyRecord?>(null) : inner.FindAsync(subject, key, cancellationToken);

        public Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> DeleteCreatedBeforeAsync(DateTimeOffset createdBefore, CancellationToken cancellationToken = default) => inner.DeleteCreatedBeforeAsync(createdBefore, cancellationToken);
    }
}

/// <summary>Idempotency keys are scoped to the caller, expired keys are renewed safely, and expired rows can be swept.</summary>
public sealed class IdempotencyScopeTests
{
    [Fact]
    public async Task Idempotency_SameKeyFromTwoSubjects_CreatesTwoIndependentClaims()
    {
        var s = new Scenario();
        var holder = HolderId.New();
        var policy = s.AddPolicy(holder: holder);
        var handler = s.SubmitHandler();

        var byClaimant = await handler.HandleAsync(s.SubmitCommand(policy, key: "shared-key"));
        var byAdjuster = await handler.HandleAsync(s.SubmitCommand(policy, key: "shared-key", actor: Scenario.Adjuster));

        Assert.False(byClaimant.Replayed);
        Assert.False(byAdjuster.Replayed);
        Assert.NotEqual(byClaimant.Claim.Id, byAdjuster.Claim.Id);
        Assert.Equal(2, s.Idempotency.Records.Count);
    }

    [Fact]
    public async Task Idempotency_ExpiredKeyRenewedConcurrently_LoserReplaysTheWinner()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var handler = s.SubmitHandler();
        var command = s.SubmitCommand(policy, key: "k1");
        await handler.HandleAsync(command);
        s.Clock.Advance(s.IdempotencySettings.TimeToLive + TimeSpan.FromSeconds(1));

        // The other request renewed the record first; this commit loses on the Version token.
        var winner = s.AddClaim(policy);
        var record = s.Idempotency.Records.Values.Single();
        record.Renew(SubmitClaimHandler.HashPayload(command), winner.Id, s.Clock.UtcNow);
        s.UnitOfWork.FailCommitWith = new ConcurrencyException();

        var result = await handler.HandleAsync(command);

        Assert.True(result.Replayed);
        Assert.Equal(winner.Id.Value, result.Claim.Id);
        Assert.Equal(2, record.Version);
    }

    [Fact]
    public async Task Idempotency_ConcurrencyConflictWithoutKey_Propagates()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        s.UnitOfWork.FailCommitWith = new ConcurrencyException();

        await Assert.ThrowsAsync<ConcurrencyException>(() => s.SubmitHandler().HandleAsync(s.SubmitCommand(policy)));
    }

    [Fact]
    public async Task Idempotency_Sweep_DeletesOnlyRecordsOlderThanTheCutoff()
    {
        var s = new Scenario();
        var policy = s.AddPolicy();
        var handler = s.SubmitHandler();
        await handler.HandleAsync(s.SubmitCommand(policy, key: "old"));
        s.Clock.Advance(TimeSpan.FromHours(30));
        await handler.HandleAsync(s.SubmitCommand(policy, key: "fresh"));

        var deleted = await s.Idempotency.DeleteCreatedBeforeAsync(s.Clock.UtcNow - s.IdempotencySettings.TimeToLive);

        Assert.Equal(1, deleted);
        Assert.Single(s.Idempotency.Records);
        Assert.Contains(s.Idempotency.Records.Values, r => r.Key == "fresh");
    }
}
