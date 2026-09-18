using System.Data.Common;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaimsEngine.Infrastructure.Persistence;

/// <summary>
/// One explicit transaction per use case. EF Core turns the Version / xmin concurrency tokens
/// into "WHERE version = @original" predicates; zero affected rows becomes a 409.
/// </summary>
/// <remarks>
/// The work runs inside the provider's execution strategy so that transient PostgreSQL failures
/// <b>before</b> the commit are retried as a whole (the change tracker is cleared before every
/// attempt). A failure <b>during</b> COMMIT is deliberately not retried: its outcome is unknown
/// (the commit may have succeeded), so re-running the work could create a second claim or answer
/// 409 to a payment that went through. Such failures surface as
/// <see cref="CommitOutcomeUnknownException"/> (HTTP 500); clients retry with an Idempotency-Key.
/// </remarks>
public sealed class EfUnitOfWork(ClaimsDbContext db) : IUnitOfWork
{
    private const string PostgresUniqueViolation = "23505";

    public Task<T> RunInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            T result;
            try
            {
                result = await work(cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                db.ChangeTracker.Clear();
                throw new ConcurrencyException(ex);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex.InnerException))
            {
                db.ChangeTracker.Clear();
                throw new DuplicateKeyException("A record with the same unique key already exists.", ex);
            }

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                throw new CommitOutcomeUnknownException(ex);
            }

            return result;
        });
    }

    private static bool IsUniqueViolation(Exception? inner) => inner switch
    {
        PostgresException pg => pg.SqlState == PostgresUniqueViolation,
        DbException other => other.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal),
        _ => false,
    };
}

/// <summary>
/// COMMIT failed and the database did not tell us whether the transaction was applied. Not a
/// transient error for the execution strategy, so the work is never re-run.
/// </summary>
public sealed class CommitOutcomeUnknownException(Exception innerException)
    : Exception("The transaction commit failed with an unknown outcome; retry the request with an Idempotency-Key.", innerException);
