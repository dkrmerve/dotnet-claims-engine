using ClaimsEngine.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClaimsEngine.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");

        // Keys are scoped to the caller: the same key value from two subjects is two rows.
        builder.HasKey(r => new { r.Subject, r.Key });
        builder.Property(r => r.Subject).HasMaxLength(128);
        builder.Property(r => r.Key).HasMaxLength(128);
        builder.Property(r => r.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(r => r.Version).IsConcurrencyToken();
        builder.HasIndex(r => r.CreatedAt);
    }
}
