using ClaimsEngine.Domain.Exceptions;

namespace ClaimsEngine.Application.Ports;

/// <summary>
/// Runs a use case inside one database transaction: everything the callback loads and changes
/// through the repositories is committed together or not at all.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyException">An aggregate changed between load and save.</exception>
    /// <exception cref="DuplicateKeyException">A unique constraint rejected an insert.</exception>
    Task<T> RunInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);
}
