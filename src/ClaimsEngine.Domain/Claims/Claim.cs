using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Claims;

/// <summary>
/// A request for money under a policy. The aggregate owns the full lifecycle
/// (Submitted -> UnderReview -> Approved -> Paid, with Rejected / Withdrawn as exits and
/// Approved -> Rejected only for an approval the annual limit can no longer honour)
/// and every business rule that concerns a single claim.
/// </summary>
public sealed class Claim
{
    public const int MaxDescriptionLength = 2_000;

    /// <summary>
    /// Longest note a caller may attach to a transition. Lower than
    /// <see cref="ClaimHistoryEntry.MaxNoteLength"/> because the audit line wraps the note
    /// ("Rejected (InsufficientEvidence): ...", "Investigation flag cleared: ...").
    /// </summary>
    public const int MaxNoteLength = 1_900;

    private readonly List<ClaimHistoryEntry> _history = [];

    // EF Core materialisation.
    private Claim()
    {
    }

    public ClaimId Id { get; private set; }

    public PolicyId PolicyId { get; private set; }

    public DateOnly IncidentDate { get; private set; }

    public DateTimeOffset FiledAt { get; private set; }

    public Money ClaimedAmount { get; private set; }

    public string Description { get; private set; } = default!;

    public ClaimStatus Status { get; private set; }

    /// <summary>Rule 3: what the policy would pay for this claim, fixed at submission.</summary>
    public Money EligiblePayout { get; private set; }

    /// <summary>Set on approval; equals <see cref="EligiblePayout"/>. Cleared again by <see cref="RejectUnpayable"/>.</summary>
    public Money? ApprovedPayout { get; private set; }

    public ClaimFlag Flag { get; private set; }

    public RejectionReason? RejectionReason { get; private set; }

    /// <summary>Rule 8: the instant the claim entered UnderReview (same instant as the history entry).</summary>
    public DateTimeOffset? ReviewStartedAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>Optimistic-concurrency token, bumped on every mutation.</summary>
    public long Version { get; private set; }

    public IReadOnlyList<ClaimHistoryEntry> History => _history;

    public bool IsFlagged => Flag == ClaimFlag.RequiresInvestigation;

    /// <summary>
    /// Files a claim, applying rules 1 (eligibility), 2 (filing window), 3 (payout formula) and
    /// 6 (investigation flag). Late or worthless claims are still created, already Rejected,
    /// so that the audit trail exists.
    /// </summary>
    public static Claim Submit(
        ClaimId id,
        Policy policy,
        DateOnly incidentDate,
        Money claimedAmount,
        string? description,
        ClaimSubmissionContext context,
        ClaimRules rules,
        DateTimeOffset filedAt)
    {
        if (claimedAmount.IsZero)
        {
            throw new ValidationException("claimedAmount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ValidationException("description is required.");
        }

        if (description.Length > MaxDescriptionLength)
        {
            throw new ValidationException($"description must be at most {MaxDescriptionLength} characters.");
        }

        var filedOn = DateOnly.FromDateTime(filedAt.UtcDateTime);
        if (incidentDate > filedOn)
        {
            throw new ValidationException($"incidentDate {incidentDate:yyyy-MM-dd} cannot be in the future.");
        }

        // Rule 1
        policy.EnsureEligibleFor(incidentDate);

        var claim = new Claim
        {
            Id = id,
            PolicyId = policy.Id,
            IncidentDate = incidentDate,
            FiledAt = filedAt,
            ClaimedAmount = claimedAmount,
            Description = description.Trim(),
            Status = ClaimStatus.Submitted,
            EligiblePayout = Money.Zero,
            Flag = ClaimFlag.None,
            Version = 1,
        };

        // Rule 6, evaluated before any auto-rejection so the flag is visible in the audit trail.
        var flagReason = InvestigationReason(policy, claimedAmount, context, rules);
        if (flagReason is not null)
        {
            claim.Flag = ClaimFlag.RequiresInvestigation;
        }

        var submitNote = flagReason is null
            ? "Claim submitted."
            : $"Claim submitted; flagged for investigation ({flagReason}).";
        claim._history.Add(new ClaimHistoryEntry(filedAt, ActorRole.Claimant, null, ClaimStatus.Submitted, submitNote));

        // Rule 2, measured from 00:00 UTC on the incident date.
        var incidentStart = new DateTimeOffset(incidentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var sinceIncident = filedAt - incidentStart;
        if (sinceIncident > rules.FilingWindow)
        {
            claim.Reject(
                ActorRole.System,
                Claims.RejectionReason.LateFiling,
                $"Filed {sinceIncident.TotalDays:0.##} days after the incident; the filing window is {rules.FilingWindowDays} days.",
                filedAt);
            return claim;
        }

        // Rule 3
        var afterDeductible = claimedAmount.Amount - policy.Deductible.Amount;
        if (afterDeductible <= 0)
        {
            claim.Reject(
                ActorRole.System,
                Claims.RejectionReason.BelowDeductible,
                $"Claimed amount {claimedAmount} does not exceed the deductible {policy.Deductible}.",
                filedAt);
            return claim;
        }

        var remainingLimit = policy.CoverageLimit.Amount - context.AlreadyPaidInPolicyYear.Amount;
        if (remainingLimit <= 0)
        {
            claim.Reject(
                ActorRole.System,
                Claims.RejectionReason.LimitExhausted,
                $"Annual coverage limit {policy.CoverageLimit} is exhausted for this policy year.",
                filedAt);
            return claim;
        }

        claim.EligiblePayout = new Money(Math.Min(afterDeductible, remainingLimit), claimedAmount.Currency);
        return claim;
    }

    /// <summary>Submitted -> UnderReview. Adjusters and above.</summary>
    public void StartReview(ActorRole actor, DateTimeOffset now, string? note = null)
    {
        EnsureTransition(ClaimStatus.UnderReview, ClaimStatus.Submitted);
        EnsureAdjusterOrAbove(actor, "start a review");
        EnsureNoteFits(note);

        ReviewStartedAt = now;
        Transition(actor, ClaimStatus.UnderReview, note ?? "Review started.", now);
    }

    /// <summary>UnderReview -> Approved. Rule 5 (authority by amount) and rule 6 (no flagged approvals).</summary>
    public void Approve(ActorRole actor, ClaimRules rules, DateTimeOffset now, string? note = null)
    {
        EnsureTransition(ClaimStatus.Approved, ClaimStatus.UnderReview);
        EnsureNoteFits(note);

        if (!ApprovalAuthority.CanApprove(actor, EligiblePayout, rules))
        {
            throw new InsufficientAuthorityException(
                $"A payout of {EligiblePayout} requires {ApprovalAuthority.DescribeRequiredRoles(EligiblePayout, rules)}; actor is {actor}.");
        }

        if (IsFlagged)
        {
            throw new InvalidTransitionException(
                "This claim is flagged for investigation and cannot be approved until a Manager clears the flag.",
                ErrorCodes.InvestigationPending);
        }

        ApprovedPayout = EligiblePayout;
        Transition(actor, ClaimStatus.Approved, note ?? $"Approved for {EligiblePayout}.", now);
    }

    /// <summary>Submitted|UnderReview -> Rejected. Rule 9: a reason is mandatory and Other needs a note.</summary>
    public void Reject(ActorRole actor, RejectionReason reason, string? note, DateTimeOffset now)
    {
        EnsureTransition(ClaimStatus.Rejected, ClaimStatus.Submitted, ClaimStatus.UnderReview);

        if (actor != ActorRole.System)
        {
            EnsureAdjusterOrAbove(actor, "reject a claim");
        }

        if (reason == Claims.RejectionReason.Other && string.IsNullOrWhiteSpace(note))
        {
            throw new ValidationException("Rejection reason Other requires an explanatory note.", ErrorCodes.RejectionNoteRequired);
        }

        EnsureNoteFits(note);

        RejectionReason = reason;
        var text = string.IsNullOrWhiteSpace(note) ? $"Rejected: {reason}." : $"Rejected ({reason}): {note.Trim()}";
        Transition(actor, ClaimStatus.Rejected, text, now);
    }

    /// <summary>
    /// Approved -> Paid. Rule 7: re-checks the annual aggregate limit against what was paid in the
    /// meantime and records the payout on the policy, which bumps the policy version so that
    /// concurrent payments on one policy conflict instead of both committing.
    /// </summary>
    public void Pay(ActorRole actor, Policy policy, Money alreadyPaidInPolicyYear, DateTimeOffset now)
    {
        EnsureTransition(ClaimStatus.Paid, ClaimStatus.Approved);
        EnsureAdjusterOrAbove(actor, "pay a claim");

        if (policy.Id != PolicyId)
        {
            throw new ValidationException("The supplied policy does not belong to this claim.");
        }

        var payout = ApprovedPayout ?? throw new InvalidTransitionException("Approved claim has no approved payout.");
        if (ExceedsLimit(policy, payout, alreadyPaidInPolicyYear, out var projectedTotal))
        {
            throw new RuleViolationException(
                ErrorCodes.LimitExhausted,
                $"Paying {payout} would bring this policy year's total to {projectedTotal:0.00} {payout.Currency}, " +
                $"above the coverage limit {policy.CoverageLimit}.");
        }

        PaidAt = now;
        policy.RecordPayout(payout);
        Transition(actor, ClaimStatus.Paid, $"Paid {payout}.", now);
    }

    /// <summary>
    /// Approved -> Rejected (LimitExhausted). Rule 7's exit: approvals do not reserve budget, so a
    /// claim approved while the limit still had room can become unpayable once other claims of the
    /// same policy year are paid. Only a Manager may close it, only as LimitExhausted, and only
    /// while <see cref="Pay"/> would really be refused; a payable approval must be paid instead.
    /// </summary>
    public void RejectUnpayable(
        ActorRole actor,
        RejectionReason reason,
        string? note,
        Policy policy,
        Money alreadyPaidInPolicyYear,
        DateTimeOffset now)
    {
        EnsureTransition(ClaimStatus.Rejected, ClaimStatus.Approved);

        if (actor != ActorRole.Manager)
        {
            throw new InsufficientAuthorityException($"Only a Manager can reject an approved claim; actor is {actor}.");
        }

        if (reason != Claims.RejectionReason.LimitExhausted)
        {
            throw new InvalidTransitionException(
                $"An approved claim can only be rejected as {Claims.RejectionReason.LimitExhausted}; reason was {reason}.");
        }

        if (policy.Id != PolicyId)
        {
            throw new ValidationException("The supplied policy does not belong to this claim.");
        }

        EnsureNoteFits(note);

        var payout = ApprovedPayout ?? throw new InvalidTransitionException("Approved claim has no approved payout.");
        if (!ExceedsLimit(policy, payout, alreadyPaidInPolicyYear, out var projectedTotal))
        {
            throw new InvalidTransitionException(
                $"Paying {payout} would bring this policy year's total to {projectedTotal:0.00} {payout.Currency}, " +
                $"within the coverage limit {policy.CoverageLimit}; the claim is payable and cannot be rejected.",
                ErrorCodes.LimitNotExhausted);
        }

        RejectionReason = reason;
        ApprovedPayout = null;
        // Kept short: with the largest amount and the longest note the line still fits ClaimHistoryEntry.MaxNoteLength.
        var text = $"Rejected ({reason}): {payout} no longer fits the annual limit.";
        Transition(actor, ClaimStatus.Rejected, string.IsNullOrWhiteSpace(note) ? text : $"{text} {note.Trim()}", now);
    }

    /// <summary>Submitted|UnderReview -> Withdrawn. Only the claimant.</summary>
    public void Withdraw(ActorRole actor, DateTimeOffset now, string? note = null)
    {
        EnsureTransition(ClaimStatus.Withdrawn, ClaimStatus.Submitted, ClaimStatus.UnderReview);

        if (actor != ActorRole.Claimant)
        {
            throw new InsufficientAuthorityException($"Only the Claimant can withdraw a claim; actor is {actor}.");
        }

        EnsureNoteFits(note);

        Transition(actor, ClaimStatus.Withdrawn, note ?? "Withdrawn by claimant.", now);
    }

    /// <summary>Rule 6: a Manager clears the investigation flag with a non-empty note.</summary>
    public void ClearFlag(ActorRole actor, string? note, DateTimeOffset now)
    {
        if (actor != ActorRole.Manager)
        {
            throw new InsufficientAuthorityException($"Only a Manager can clear an investigation flag; actor is {actor}.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ValidationException("Clearing an investigation flag requires a note explaining the outcome.", ErrorCodes.NoteRequired);
        }

        EnsureNoteFits(note);

        if (Status.IsTerminal())
        {
            throw new InvalidTransitionException($"Claim is {Status}; flags on closed claims cannot be changed.");
        }

        if (!IsFlagged)
        {
            throw new InvalidTransitionException("This claim is not flagged for investigation.", ErrorCodes.FlagNotSet);
        }

        Flag = ClaimFlag.None;
        Version++;
        _history.Add(new ClaimHistoryEntry(now, actor, Status, Status, $"Investigation flag cleared: {note.Trim()}"));
    }

    private static string? InvestigationReason(Policy policy, Money claimedAmount, ClaimSubmissionContext context, ClaimRules rules)
    {
        if (context.OtherClaimsByHolderInWindow >= rules.FrequentClaimantThreshold)
        {
            return $"holder has {context.OtherClaimsByHolderInWindow} other claims in the last {rules.FrequentClaimantWindowDays} days";
        }

        var highValueThreshold = policy.CoverageLimit * rules.HighValueShareOfLimit;
        if (claimedAmount >= highValueThreshold)
        {
            return $"claimed amount {claimedAmount} is at least {rules.HighValueShareOfLimit:P0} of the coverage limit {policy.CoverageLimit}";
        }

        return null;
    }

    private void EnsureTransition(ClaimStatus target, params ReadOnlySpan<ClaimStatus> allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new InvalidTransitionException($"Cannot move claim from {Status} to {target}.");
        }
    }

    private static void EnsureAdjusterOrAbove(ActorRole actor, string action)
    {
        if (!actor.IsAdjusterOrAbove())
        {
            throw new InsufficientAuthorityException($"Only Adjuster, SeniorAdjuster or Manager can {action}; actor is {actor}.");
        }
    }

    private static bool ExceedsLimit(Policy policy, Money payout, Money alreadyPaidInPolicyYear, out decimal projectedTotal)
    {
        projectedTotal = alreadyPaidInPolicyYear.Amount + payout.Amount;
        return projectedTotal > policy.CoverageLimit.Amount;
    }

    private static void EnsureNoteFits(string? note)
    {
        if (note is { Length: > MaxNoteLength })
        {
            throw new ValidationException($"note must be at most {MaxNoteLength} characters.");
        }
    }

    private void Transition(ActorRole actor, ClaimStatus to, string note, DateTimeOffset now)
    {
        var from = Status;
        Status = to;
        Version++;
        _history.Add(new ClaimHistoryEntry(now, actor, from, to, note));
    }
}
