using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Tests.Support;

internal sealed class FakeClock : IClock
{
    public static readonly DateTimeOffset Start = new(2026, 3, 15, 10, 30, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = Start;

    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

    public void Advance(TimeSpan by) => UtcNow += by;
}

internal sealed class FakePolicyRepository : IPolicyRepository
{
    public Dictionary<PolicyId, Policy> Policies { get; } = [];

    public Task<Policy?> GetAsync(PolicyId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Policies.GetValueOrDefault(id));

    public Task AddAsync(Policy policy, CancellationToken cancellationToken = default)
    {
        Policies[policy.Id] = policy;
        return Task.CompletedTask;
    }
}

internal sealed class FakeClaimRepository(FakePolicyRepository policies) : IClaimRepository
{
    public List<Claim> Claims { get; } = [];

    public Task<Claim?> GetAsync(ClaimId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Claims.FirstOrDefault(c => c.Id == id));

    public Task AddAsync(Claim claim, CancellationToken cancellationToken = default)
    {
        Claims.Add(claim);
        return Task.CompletedTask;
    }

    public Task<PagedResult<Claim>> ListAsync(ClaimFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        IEnumerable<Claim> query = Claims;
        if (filter.PolicyId is { } policyId)
        {
            query = query.Where(c => c.PolicyId == policyId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filter.OwnedBy is { } holder)
        {
            query = query.Where(c => policies.Policies.TryGetValue(c.PolicyId, out var p) && p.HolderId == holder);
        }

        var all = query.OrderByDescending(c => c.FiledAt).ToList();
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<Claim>(items, all.Count));
    }

    public Task<IReadOnlyList<Claim>> ListOverdueAsync(DateTimeOffset reviewStartedBefore, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Claim>>(Claims
            .Where(c => c.Status == ClaimStatus.UnderReview && c.ReviewStartedAt < reviewStartedBefore)
            .ToList());

    public Task<Money> SumPaidPayoutsAsync(PolicyId policyId, DateRange policyYear, ClaimId? excludeClaimId = null, CancellationToken cancellationToken = default)
    {
        var total = Claims
            .Where(c => c.PolicyId == policyId && c.Status == ClaimStatus.Paid && policyYear.Contains(c.IncidentDate) && c.Id != excludeClaimId)
            .Aggregate(Money.Zero, (sum, c) => sum + c.ApprovedPayout!.Value);
        return Task.FromResult(total);
    }

    public Task<int> CountRecentClaimsForHolderAsync(HolderId holderId, DateTimeOffset filedSince, CancellationToken cancellationToken = default) =>
        Task.FromResult(Claims.Count(c =>
            c.FiledAt >= filedSince
            && c.Status != ClaimStatus.Withdrawn
            && policies.Policies.TryGetValue(c.PolicyId, out var p)
            && p.HolderId == holderId));
}

internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    /// <summary>Keyed by "subject|key", mirroring the composite primary key of the table.</summary>
    public Dictionary<string, IdempotencyRecord> Records { get; } = new(StringComparer.Ordinal);

    public static string KeyOf(string subject, string key) => subject + "|" + key;

    public Task<IdempotencyRecord?> FindAsync(string subject, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Records.GetValueOrDefault(KeyOf(subject, key)));

    public Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default)
    {
        Records[KeyOf(record.Subject, record.Key)] = record;
        return Task.CompletedTask;
    }

    public Task<int> DeleteCreatedBeforeAsync(DateTimeOffset createdBefore, CancellationToken cancellationToken = default)
    {
        var expired = Records.Where(r => r.Value.CreatedAt < createdBefore).Select(r => r.Key).ToList();
        expired.ForEach(k => Records.Remove(k));
        return Task.FromResult(expired.Count);
    }
}

/// <summary>Runs the work inline. <see cref="FailCommitWith"/> simulates a failure raised by SaveChanges.</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Commits { get; private set; }

    public Exception? FailCommitWith { get; set; }

    public bool FailOnlyOnce { get; set; }

    public async Task<T> RunInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        var result = await work(cancellationToken);
        if (FailCommitWith is { } failure)
        {
            if (FailOnlyOnce)
            {
                FailCommitWith = null;
            }

            throw failure;
        }

        Commits++;
        return result;
    }
}
