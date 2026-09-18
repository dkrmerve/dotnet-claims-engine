using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ClaimsEngine.Api.Tests.Support;

/// <summary>
/// Fault injection at the database boundary. When armed, throws on the N-th write command
/// (INSERT/UPDATE/DELETE) or on any command matching the predicate, which is how the
/// atomicity and 500-mapping tests force a failure part-way through a use case.
/// </summary>
public sealed class FailingCommandInterceptor : DbCommandInterceptor
{
    private int _writes;

    public bool Armed { get; set; }

    /// <summary>1-based index of the write command to fail on; 0 disables the counter.</summary>
    public int FailOnWriteNumber { get; set; }

    public Func<string, bool>? FailWhen { get; set; }

    public int Failures { get; private set; }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Inspect(command);
        return ValueTask.FromResult(result);
    }

    private void Inspect(DbCommand command)
    {
        if (!Armed)
        {
            return;
        }

        var text = command.CommandText;
        var isWrite = text.Contains("INSERT INTO", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase);
        if (isWrite)
        {
            _writes++;
        }

        if ((FailOnWriteNumber > 0 && isWrite && _writes == FailOnWriteNumber) || FailWhen?.Invoke(text) == true)
        {
            Failures++;
            throw new InvalidOperationException($"Injected failure on: {text[..Math.Min(80, text.Length)]}");
        }
    }
}
