using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Claims;

public sealed record GetClaimQuery(Guid ClaimId, Actor Actor);

public sealed class GetClaimHandler(IClaimRepository claims, IPolicyRepository policies)
{
    public async Task<ClaimDto> HandleAsync(GetClaimQuery query, CancellationToken cancellationToken = default)
    {
        var claim = await ClaimAccess.LoadAsync(claims, policies, query.ClaimId, query.Actor, cancellationToken);
        return ClaimDto.From(claim);
    }
}

public sealed class GetClaimHistoryHandler(IClaimRepository claims, IPolicyRepository policies)
{
    public async Task<IReadOnlyList<ClaimHistoryEntryDto>> HandleAsync(GetClaimQuery query, CancellationToken cancellationToken = default)
    {
        var claim = await ClaimAccess.LoadAsync(claims, policies, query.ClaimId, query.Actor, cancellationToken);
        return claim.History.Select(ClaimHistoryEntryDto.From).ToList();
    }
}

/// <param name="Page">1-based page number.</param>
public sealed record ListClaimsQuery(Guid? PolicyId, ClaimStatus? Status, int Page, int PageSize, Actor Actor);

public sealed class ListClaimsHandler(IClaimRepository claims)
{
    public const int MaxPageSize = 100;

    /// <summary>Highest page whose row offset ((page - 1) * pageSize) still fits a 32-bit OFFSET at any page size.</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    public async Task<PagedResponse<ClaimDto>> HandleAsync(ListClaimsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Page is < 1 or > MaxPage)
        {
            throw new ValidationException($"page must be between 1 and {MaxPage}.");
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            throw new ValidationException($"pageSize must be between 1 and {MaxPageSize}.");
        }

        var filter = new ClaimFilter(
            query.PolicyId is { } p ? new PolicyId(p) : null,
            query.Status,
            Ownership.VisibleHolder(query.Actor));

        var result = await claims.ListAsync(filter, query.Page, query.PageSize, cancellationToken);
        return new PagedResponse<ClaimDto>(result.Items.Select(ClaimDto.From).ToList(), query.Page, query.PageSize, result.TotalCount);
    }
}

/// <summary>Rule 8: claims under review for longer than the SLA. Exactly the SLA is not overdue.</summary>
public sealed class ListOverdueClaimsHandler(IClaimRepository claims, IClock clock, ClaimRules rules)
{
    public async Task<IReadOnlyList<ClaimDto>> HandleAsync(CancellationToken cancellationToken = default)
    {
        var threshold = clock.UtcNow - rules.ReviewSla;
        var result = await claims.ListOverdueAsync(threshold, cancellationToken);
        return result.Select(ClaimDto.From).ToList();
    }
}

/// <summary>Loads a claim and enforces that Claimants only read claims on their own policies.</summary>
internal static class ClaimAccess
{
    public static async Task<Claim> LoadAsync(
        IClaimRepository claims,
        IPolicyRepository policies,
        Guid claimId,
        Actor actor,
        CancellationToken cancellationToken)
    {
        var claim = await claims.GetAsync(new ClaimId(claimId), cancellationToken)
            ?? throw new NotFoundException("Claim", claimId);

        if (actor.Role == ActorRole.Claimant)
        {
            var policy = await policies.GetAsync(claim.PolicyId, cancellationToken)
                ?? throw new NotFoundException("Policy", claim.PolicyId);
            Ownership.EnsureCanAccess(actor, policy);
        }

        return claim;
    }
}
