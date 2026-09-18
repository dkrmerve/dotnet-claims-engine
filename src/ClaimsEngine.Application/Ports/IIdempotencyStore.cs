using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Ports;

/// <summary>
/// What an earlier request with a given Idempotency-Key produced. Keys are scoped to the caller
/// (<see cref="Subject"/>): two callers using the same key value never see each other's records.
/// </summary>
public sealed class IdempotencyRecord
{
    // EF Core materialisation.
    private IdempotencyRecord()
    {
    }

    public IdempotencyRecord(string subject, string key, string payloadHash, ClaimId claimId, DateTimeOffset createdAt)
    {
        Subject = subject;
        Key = key;
        PayloadHash = payloadHash;
        ClaimId = claimId;
        CreatedAt = createdAt;
        Version = 1;
    }

    /// <summary>The <c>sub</c> claim of the caller that used the key.</summary>
    public string Subject { get; private set; } = default!;

    public string Key { get; private set; } = default!;

    /// <summary>SHA-256 of the canonical request payload; detects key reuse with a different body.</summary>
    public string PayloadHash { get; private set; } = default!;

    public ClaimId ClaimId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Concurrency token: two requests renewing the same expired key cannot both win.</summary>
    public long Version { get; private set; }

    public bool IsExpired(DateTimeOffset now, TimeSpan timeToLive) => now - CreatedAt > timeToLive;

    /// <summary>Re-points an expired key at a new claim instead of deleting and re-inserting the row.</summary>
    public void Renew(string payloadHash, ClaimId claimId, DateTimeOffset createdAt)
    {
        PayloadHash = payloadHash;
        ClaimId = claimId;
        CreatedAt = createdAt;
        Version++;
    }
}

/// <summary>How long a stored Idempotency-Key answers replays before it may be reused.</summary>
public sealed record IdempotencySettings(TimeSpan TimeToLive)
{
    public static IdempotencySettings Default => new(TimeSpan.FromHours(24));
}

public interface IIdempotencyStore
{
    /// <summary>Loads the caller's record for modification (tracked).</summary>
    Task<IdempotencyRecord?> FindAsync(string subject, string key, CancellationToken cancellationToken = default);

    Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default);

    /// <summary>Deletes records created before <paramref name="createdBefore"/>; returns how many were removed.</summary>
    Task<int> DeleteCreatedBeforeAsync(DateTimeOffset createdBefore, CancellationToken cancellationToken = default);
}
