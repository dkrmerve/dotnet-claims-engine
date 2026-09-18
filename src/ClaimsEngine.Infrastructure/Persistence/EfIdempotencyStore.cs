using ClaimsEngine.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace ClaimsEngine.Infrastructure.Persistence;

public sealed class EfIdempotencyStore(ClaimsDbContext db) : IIdempotencyStore
{
    public Task<IdempotencyRecord?> FindAsync(string subject, string key, CancellationToken cancellationToken = default) =>
        db.IdempotencyRecords.AsTracking().SingleOrDefaultAsync(r => r.Subject == subject && r.Key == key, cancellationToken);

    public async Task AddAsync(IdempotencyRecord record, CancellationToken cancellationToken = default) =>
        await db.IdempotencyRecords.AddAsync(record, cancellationToken);

    public Task<int> DeleteCreatedBeforeAsync(DateTimeOffset createdBefore, CancellationToken cancellationToken = default) =>
        db.IdempotencyRecords.Where(r => r.CreatedAt < createdBefore).ExecuteDeleteAsync(cancellationToken);
}
