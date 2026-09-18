using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace ClaimsEngine.Infrastructure.Persistence.Repositories;

public sealed class EfPolicyRepository(ClaimsDbContext db) : IPolicyRepository
{
    public Task<Policy?> GetAsync(PolicyId id, CancellationToken cancellationToken = default) =>
        db.Policies.AsTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task AddAsync(Policy policy, CancellationToken cancellationToken = default) =>
        await db.Policies.AddAsync(policy, cancellationToken);
}
