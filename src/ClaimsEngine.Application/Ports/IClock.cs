namespace ClaimsEngine.Application.Ports;

/// <summary>Abstracts "now" so filing windows, SLAs and idempotency TTLs are testable with a fixed clock.</summary>
public interface IClock
{
    /// <summary>Current instant with a zero offset.</summary>
    DateTimeOffset UtcNow { get; }
}
