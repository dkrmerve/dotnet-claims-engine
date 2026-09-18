using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Claims;

/// <summary>One line of the audit trail. Rule 11: every transition appends one.</summary>
public sealed class ClaimHistoryEntry
{
    /// <summary>Upper bound of a stored audit line: the caller's note plus the prefix the claim adds to it.</summary>
    public const int MaxNoteLength = 2_000;

    // EF Core materialisation.
    private ClaimHistoryEntry()
    {
    }

    public ClaimHistoryEntry(DateTimeOffset at, ActorRole actorRole, ClaimStatus? fromStatus, ClaimStatus toStatus, string? note)
    {
        At = at;
        ActorRole = actorRole;
        FromStatus = fromStatus;
        ToStatus = toStatus;
        Note = note;
    }

    public DateTimeOffset At { get; private set; }

    public ActorRole ActorRole { get; private set; }

    /// <summary>Null for the initial submission.</summary>
    public ClaimStatus? FromStatus { get; private set; }

    public ClaimStatus ToStatus { get; private set; }

    public string? Note { get; private set; }
}
