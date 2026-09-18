using ClaimsEngine.Domain.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClaimsEngine.Infrastructure.Persistence.Configurations;

public sealed class PolicyConfiguration : IEntityTypeConfiguration<Policy>
{
    public void Configure(EntityTypeBuilder<Policy> builder)
    {
        builder.ToTable("policies");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.PolicyNumber).HasMaxLength(64).IsRequired();
        builder.HasIndex(p => p.PolicyNumber).IsUnique();
        builder.HasIndex(p => p.HolderId);
        builder.Property(p => p.Version).IsConcurrencyToken();
    }
}
