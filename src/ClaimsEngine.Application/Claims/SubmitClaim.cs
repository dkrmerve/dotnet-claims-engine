using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Claims;

public sealed record SubmitClaimCommand(
    Guid PolicyId,
    DateOnly IncidentDate,
    decimal ClaimedAmount,
    string Description,
    string? IdempotencyKey,
    Actor Actor);

/// <param name="Claim">The created (or replayed) claim.</param>
/// <param name="Replayed">True when an earlier request with the same Idempotency-Key was answered instead of creating a new claim.</param>
public sealed record SubmitClaimResult(ClaimDto Claim, bool Replayed);

/// <summary>
/// Rule 10 (idempotent submit) around the domain's <see cref="Claim.Submit"/>, which applies
/// rules 1, 2, 3 and 6. The handler gathers the portfolio facts the domain cannot query itself.
/// </summary>
public sealed class SubmitClaimHandler(
    IPolicyRepository policies,
    IClaimRepository claims,
    IIdempotencyStore idempotency,
    IUnitOfWork unitOfWork,
    IClock clock,
    ClaimRules rules,
    IdempotencySettings idempotencySettings)
{
    public async Task<SubmitClaimResult> HandleAsync(SubmitClaimCommand command, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(command.IdempotencyKey) ? null : command.IdempotencyKey.Trim();
        var payloadHash = key is null ? null : HashPayload(command);

        try
        {
            return await unitOfWork.RunInTransactionAsync(token => SubmitAsync(command, key, payloadHash, token), cancellationToken);
        }
        catch (DomainException ex) when (key is not null && ex is DuplicateKeyException or ConcurrencyException)
        {
            // Two requests with the same key raced: either both tried to insert a new key (the unique
            // index let exactly one win) or both tried to renew an expired one (the Version token let
            // exactly one win). Answer this one with what the winner stored.
            return await ReplayAsync(command.Actor.Subject, key, payloadHash!, cancellationToken)
                ?? throw new NotFoundException("Idempotency-Key", key);
        }
    }

    private async Task<SubmitClaimResult> SubmitAsync(SubmitClaimCommand command, string? key, string? payloadHash, CancellationToken token)
    {
        var now = clock.UtcNow;
        IdempotencyRecord? record = null;
        if (key is not null)
        {
            record = await idempotency.FindAsync(command.Actor.Subject, key, token);
            if (record is not null && !record.IsExpired(now, idempotencySettings.TimeToLive))
            {
                if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    throw new RuleViolationException(
                        ErrorCodes.IdempotencyKeyReused,
                        $"Idempotency-Key '{key}' was already used with a different payload.");
                }

                var original = await claims.GetAsync(record.ClaimId, token)
                    ?? throw new NotFoundException("Claim", record.ClaimId);
                return new SubmitClaimResult(ClaimDto.From(original), Replayed: true);
            }
        }

        var policyId = new PolicyId(command.PolicyId);
        var policy = await policies.GetAsync(policyId, token)
            ?? throw new NotFoundException("Policy", command.PolicyId);
        Ownership.EnsureCanAccess(command.Actor, policy);

        // Money's constructor rejects negatives and sub-cent amounts; zero is rejected by Claim.Submit.
        var claimedAmount = new Money(command.ClaimedAmount);

        // Rule 1 is checked here first so that the policy-year lookup below is well defined;
        // Claim.Submit checks it again because the domain never trusts its callers.
        policy.EnsureEligibleFor(command.IncidentDate);

        var policyYear = policy.PolicyYearContaining(command.IncidentDate);
        var alreadyPaid = await claims.SumPaidPayoutsAsync(policyId, policyYear, null, token);
        var recentClaims = await claims.CountRecentClaimsForHolderAsync(policy.HolderId, now - rules.FrequentClaimantWindow, token);

        var claim = Claim.Submit(
            ClaimId.New(),
            policy,
            command.IncidentDate,
            claimedAmount,
            command.Description,
            new ClaimSubmissionContext(alreadyPaid, recentClaims),
            rules,
            now);

        await claims.AddAsync(claim, token);

        if (key is not null)
        {
            if (record is null)
            {
                await idempotency.AddAsync(new IdempotencyRecord(command.Actor.Subject, key, payloadHash!, claim.Id, now), token);
            }
            else
            {
                record.Renew(payloadHash!, claim.Id, now);
            }
        }

        return new SubmitClaimResult(ClaimDto.From(claim), Replayed: false);
    }

    private async Task<SubmitClaimResult?> ReplayAsync(string subject, string key, string payloadHash, CancellationToken cancellationToken)
    {
        var record = await idempotency.FindAsync(subject, key, cancellationToken);
        if (record is null)
        {
            return null;
        }

        if (!string.Equals(record.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            throw new RuleViolationException(
                ErrorCodes.IdempotencyKeyReused,
                $"Idempotency-Key '{key}' was already used with a different payload.");
        }

        var claim = await claims.GetAsync(record.ClaimId, cancellationToken);
        return claim is null ? null : new SubmitClaimResult(ClaimDto.From(claim), Replayed: true);
    }

    /// <summary>Canonical, culture-invariant fingerprint of the fields that define a submission.</summary>
    internal static string HashPayload(SubmitClaimCommand command)
    {
        var canonical = string.Join(
            '|',
            command.PolicyId.ToString("D"),
            command.IncidentDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            command.ClaimedAmount.ToString("0.00", CultureInfo.InvariantCulture),
            command.Description.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
