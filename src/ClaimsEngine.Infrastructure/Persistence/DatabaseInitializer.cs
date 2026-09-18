using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClaimsEngine.Infrastructure.Persistence;

/// <summary>
/// Brings the schema up to date at startup. PostgreSQL gets the checked-in migrations, any other
/// provider (SQLite in the fast test profile) gets EnsureCreated because the migrations are
/// Npgsql-specific. Retries with exponential back-off so "docker compose up" works even when the
/// API container starts before PostgreSQL accepts connections.
/// </summary>
public static class DatabaseInitializer
{
    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(60);

    public static async Task InitializeAsync(
        ClaimsDbContext db,
        ILogger logger,
        TimeSpan? maxWait = null,
        TimeSpan? initialDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        var deadline = DateTimeOffset.UtcNow + (maxWait ?? DefaultMaxWait);
        var delay = initialDelay ?? TimeSpan.FromSeconds(1);
        var attempt = 0;

        while (true)
        {
            attempt++;
            try
            {
                if (db.Database.IsNpgsql())
                {
                    await db.Database.MigrateAsync(cancellationToken);
                    logger.LogInformation("Database migrations applied (attempt {Attempt}).", attempt);
                }
                else
                {
                    await db.Database.EnsureCreatedAsync(cancellationToken);
                    logger.LogInformation("Database schema created from model for provider {Provider}.", db.Database.ProviderName);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTimeOffset.UtcNow + delay < deadline)
            {
                logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying in {Delay}.", attempt, delay);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(16).Ticks));
            }
        }
    }
}
