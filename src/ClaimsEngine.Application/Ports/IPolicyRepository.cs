using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Ports;

public interface IPolicyRepository
{
    Task<Policy?> GetAsync(PolicyId id, CancellationToken cancellationToken = default);

    Task AddAsync(Policy policy, CancellationToken cancellationToken = default);
}
