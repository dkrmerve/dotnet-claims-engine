using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Ports;

public sealed record ClaimFilter(PolicyId? PolicyId, ClaimStatus? Status, HolderId? OwnedBy);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public interface IClaimRepository
{
    /// <summary>Loads a claim for modification (tracked).</summary>
    Task<Claim?> GetAsync(ClaimId id, CancellationToken cancellationToken = default);

    Task AddAsync(Claim claim, CancellationToken cancellationToken = default);

    /// <summary>Newest first. <paramref name="page"/> is 1-based.</summary>
    Task<PagedResult<Claim>> ListAsync(ClaimFilter filter, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Claims that entered UnderReview strictly before <paramref name="reviewStartedBefore"/>.</summary>
    Task<IReadOnlyList<Claim>> ListOverdueAsync(DateTimeOffset reviewStartedBefore, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sum of <see cref="Claim.ApprovedPayout"/> over Paid claims of the policy whose incident date
    /// lies in <paramref name="policyYear"/>, optionally excluding one claim.
    /// </summary>
    Task<Money> SumPaidPayoutsAsync(PolicyId policyId, DateRange policyYear, ClaimId? excludeClaimId = null, CancellationToken cancellationToken = default);

    /// <summary>Non-withdrawn claims filed by the holder, across all their policies, at or after <paramref name="filedSince"/>.</summary>
    Task<int> CountRecentClaimsForHolderAsync(HolderId holderId, DateTimeOffset filedSince, CancellationToken cancellationToken = default);
}
