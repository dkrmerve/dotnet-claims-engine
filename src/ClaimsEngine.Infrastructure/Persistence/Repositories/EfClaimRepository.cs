using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace ClaimsEngine.Infrastructure.Persistence.Repositories;

/// <summary>
/// The context defaults to no-tracking queries; <see cref="GetAsync"/> opts back in because
/// callers mutate what it returns.
/// </summary>
public sealed class EfClaimRepository(ClaimsDbContext db) : IClaimRepository
{
    public Task<Claim?> GetAsync(ClaimId id, CancellationToken cancellationToken = default) =>
        db.Claims.AsTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task AddAsync(Claim claim, CancellationToken cancellationToken = default) =>
        await db.Claims.AddAsync(claim, cancellationToken);

    public async Task<PagedResult<Claim>> ListAsync(ClaimFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        IQueryable<Claim> query = db.Claims;

        if (filter.PolicyId is { } policyId)
        {
            query = query.Where(c => c.PolicyId == policyId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filter.OwnedBy is { } holderId)
        {
            query = query.Where(c => db.Policies.Any(p => p.Id == c.PolicyId && p.HolderId == holderId));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.FiledAt)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<Claim>(items, total);
    }

    public async Task<IReadOnlyList<Claim>> ListOverdueAsync(DateTimeOffset reviewStartedBefore, CancellationToken cancellationToken = default) =>
        await db.Claims
            .Where(c => c.Status == ClaimStatus.UnderReview && c.ReviewStartedAt < reviewStartedBefore)
            .OrderBy(c => c.ReviewStartedAt)
            .ToListAsync(cancellationToken);

    public async Task<Money> SumPaidPayoutsAsync(PolicyId policyId, DateRange policyYear, ClaimId? excludeClaimId = null, CancellationToken cancellationToken = default)
    {
        var query = db.Claims.Where(c =>
            c.PolicyId == policyId
            && c.Status == ClaimStatus.Paid
            && c.IncidentDate >= policyYear.Start
            && c.IncidentDate <= policyYear.End);

        if (excludeClaimId is { } exclude)
        {
            query = query.Where(c => c.Id != exclude);
        }

        // Summed client-side: a policy year holds a handful of paid claims, and SQLite (used in
        // the fast test profile) cannot aggregate decimals in SQL.
        var payouts = await query.Select(c => c.ApprovedPayout).ToListAsync(cancellationToken);
        return payouts.Aggregate(Money.Zero, (sum, payout) => payout is { } p ? sum + p : sum);
    }

    public Task<int> CountRecentClaimsForHolderAsync(HolderId holderId, DateTimeOffset filedSince, CancellationToken cancellationToken = default) =>
        db.Claims
            .Where(c => c.FiledAt >= filedSince
                        && c.Status != ClaimStatus.Withdrawn
                        && db.Policies.Any(p => p.Id == c.PolicyId && p.HolderId == holderId))
            .CountAsync(cancellationToken);
}
