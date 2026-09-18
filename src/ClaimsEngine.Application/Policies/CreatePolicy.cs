using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Policies;

public sealed record CreatePolicyCommand(
    string PolicyNumber,
    Guid HolderId,
    CoverageType CoverageType,
    decimal CoverageLimit,
    decimal Deductible,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo);

public sealed class CreatePolicyHandler(IPolicyRepository policies, IUnitOfWork unitOfWork)
{
    public Task<PolicyDto> HandleAsync(CreatePolicyCommand command, CancellationToken cancellationToken = default) =>
        unitOfWork.RunInTransactionAsync(
            async token =>
            {
                var policy = Policy.Create(
                    PolicyId.New(),
                    command.PolicyNumber,
                    new HolderId(command.HolderId),
                    command.CoverageType,
                    new Money(command.CoverageLimit),
                    new Money(command.Deductible),
                    command.EffectiveFrom,
                    command.EffectiveTo);

                await policies.AddAsync(policy, token);
                return PolicyDto.From(policy);
            },
            cancellationToken);
}
