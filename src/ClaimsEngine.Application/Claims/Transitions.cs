using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Claims;

// One class per command. Each runs in a transaction, loads the aggregate, delegates the rule to
// the domain and lets the unit of work commit. Domain exceptions bubble up unchanged.

public sealed record StartReviewCommand(Guid ClaimId, Actor Actor, string? Note);

public sealed class StartReviewHandler(IClaimRepository claims, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<ClaimDto> HandleAsync(StartReviewCommand command, CancellationToken cancellationToken = default) =>
        unitOfWork.RunInTransactionAsync(
            async token =>
            {
                var claim = await claims.GetAsync(new ClaimId(command.ClaimId), token)
                    ?? throw new NotFoundException("Claim", command.ClaimId);
                claim.StartReview(command.Actor.Role, clock.UtcNow, command.Note);
                return ClaimDto.From(claim);
            },
            cancellationToken);
}

public sealed record ApproveClaimCommand(Guid ClaimId, Actor Actor, string? Note);

public sealed class ApproveClaimHandler(IClaimRepository claims, IUnitOfWork unitOfWork, IClock clock, ClaimRules rules)
{
    public Task<ClaimDto> HandleAsync(ApproveClaimCommand command, CancellationToken cancellationToken = default) =>
        unitOfWork.RunInTransactionAsync(
            async token =>
            {
                var claim = await claims.GetAsync(new ClaimId(command.ClaimId), token)
                    ?? throw new NotFoundException("Claim", command.ClaimId);
                claim.Approve(command.Actor.Role, rules, clock.UtcNow, command.Note);
                return ClaimDto.From(claim);
            },
            cancellationToken);
}

public sealed record RejectClaimCommand(Guid ClaimId, Actor Actor, RejectionReason Reason, string? Note);

public sealed class RejectClaimHandler(IClaimRepository claims, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<ClaimDto> HandleAsync(RejectClaimCommand command, CancellationToken cancellationToken = default) =>
        unitOfWork.RunInTransactionAsync(
            async token =>
            {
                var claim = await claims.GetAsync(new ClaimId(command.ClaimId), token)
                    ?? throw new NotFoundException("Claim", command.ClaimId);
                claim.Reject(command.Actor.Role, command.Reason, command.Note, clock.UtcNow);
                return ClaimDto.From(claim);
            },
            cancellationToken);
}

public sealed record PayClaimCommand(Guid ClaimId, Actor Actor);

/// <summary>
/// Rule 7: the annual limit is re-checked with fresh data inside the payment transaction. The
/// claim and the policy are both versioned, so a racing payment on the same policy ends in a
/// concurrency conflict instead of silently overshooting the limit.
/// </summary>
public sealed class PayClaimHandler(IClaimRepository claims, IPolicyRepository policies, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<ClaimDto> HandleAsync(PayClaimCommand command, CancellationToken cancellationToken = default) =>
        unitOfWork.RunInTransactionAsync(
            async token =>
            {
                var claim = await claims.GetAsync(new ClaimId(command.ClaimId), token)
                    ?? throw new NotFoundException("Claim", command.ClaimId);
                var policy = await policies.GetAsync(claim.PolicyId, token)
                    ?? throw new NotFoundException("Policy", claim.PolicyId);

                var policyYear = policy.PolicyYearContaining(claim.IncidentDate);
                var alreadyPaid = await claims.SumPaidPayoutsAsync(policy.Id, policyYear, claim.Id, token);

                claim.Pay(command.Actor.Role, policy, alreadyPaid, clock.UtcNow);
                return ClaimDto.From(claim);
            },
            cancellationToken);
}

public sealed record WithdrawClaimCommand(Guid ClaimId, Actor Actor, string? Note);

public sealed class WithdrawClaimHandler(IClaimRepository claims, IPolicyRepository policies, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<ClaimDto> HandleAsync(WithdrawClaimCommand command, CancellationToken cancellationToken = default) =>
        unitOfWork.RunInTransactionAsync(
            async token =>
            {
                var claim = await ClaimAccess.LoadAsync(claims, policies, command.ClaimId, command.Actor, token);
                claim.Withdraw(command.Actor.Role, clock.UtcNow, command.Note);
                return ClaimDto.From(claim);
            },
            cancellationToken);
}

public sealed record ClearClaimFlagCommand(Guid ClaimId, Actor Actor, string? Note);

public sealed class ClearClaimFlagHandler(IClaimRepository claims, IUnitOfWork unitOfWork, IClock clock)
{
    public Task<ClaimDto> HandleAsync(ClearClaimFlagCommand command, CancellationToken cancellationToken = default) =>
        unitOfWork.RunInTransactionAsync(
            async token =>
            {
                var claim = await claims.GetAsync(new ClaimId(command.ClaimId), token)
                    ?? throw new NotFoundException("Claim", command.ClaimId);
                claim.ClearFlag(command.Actor.Role, command.Note, clock.UtcNow);
                return ClaimDto.From(claim);
            },
            cancellationToken);
}
