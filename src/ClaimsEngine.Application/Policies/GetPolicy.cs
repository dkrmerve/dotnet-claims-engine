using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Policies;

public sealed record GetPolicyQuery(Guid PolicyId, Actor Actor);

public sealed class GetPolicyHandler(IPolicyRepository policies)
{
    public async Task<PolicyDto> HandleAsync(GetPolicyQuery query, CancellationToken cancellationToken = default)
    {
        var policy = await policies.GetAsync(new PolicyId(query.PolicyId), cancellationToken)
            ?? throw new NotFoundException("Policy", query.PolicyId);

        Ownership.EnsureCanAccess(query.Actor, policy);
        return PolicyDto.From(policy);
    }
}
