using ClaimsEngine.Api.Options;
using ClaimsEngine.Application.Ports;
using Microsoft.Extensions.Options;

namespace ClaimsEngine.Api.Composition;

/// <summary>
/// Deletes idempotency keys older than the TTL every Idempotency:SweepIntervalMinutes (0 = off),
/// so the table does not grow forever. Runs once at startup, then on the interval; every sweep
/// logs how many rows it removed.
/// </summary>
public sealed class IdempotencySweeper(
    IServiceScopeFactory scopes,
    IClock clock,
    IdempotencySettings settings,
    IOptions<IdempotencyOptions> options,
    ILogger<IdempotencySweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Value.SweepIntervalMinutes);
        if (interval <= TimeSpan.Zero)
        {
            logger.LogInformation("Idempotency key sweeper is disabled (Idempotency:SweepIntervalMinutes = 0).");
            return;
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Idempotency key sweep failed; will retry after {Interval}.", interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One sweep: delete every key created before now - TTL. Returns the number of rows removed.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var cutoff = clock.UtcNow - settings.TimeToLive;

        var deleted = await store.DeleteCreatedBeforeAsync(cutoff, cancellationToken);
        logger.LogInformation("Idempotency key sweep removed {Deleted} key(s) created before {Cutoff:O}.", deleted, cutoff);
        return deleted;
    }
}
