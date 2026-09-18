using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClaimsEngine.Infrastructure.Persistence.Configurations;

public sealed class ClaimConfiguration : IEntityTypeConfiguration<Claim>
{
    public void Configure(EntityTypeBuilder<Claim> builder)
    {
        builder.ToTable("claims");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Description).HasMaxLength(Claim.MaxDescriptionLength).IsRequired();
        builder.Property(c => c.Version).IsConcurrencyToken();

        builder.HasIndex(c => new { c.PolicyId, c.Status });
        builder.HasIndex(c => c.FiledAt);
        builder.HasIndex(c => new { c.Status, c.ReviewStartedAt });

        builder.HasOne<Policy>().WithMany().HasForeignKey(c => c.PolicyId).OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(c => c.IsFlagged);

        // The audit trail is an owned collection: loaded with the claim, saved with the claim.
        builder.OwnsMany(c => c.History, history =>
        {
            history.ToTable("claim_history");
            history.WithOwner().HasForeignKey("ClaimId");
            history.Property<long>("Id").ValueGeneratedOnAdd();
            history.HasKey("Id");
            history.Property(h => h.Note).HasMaxLength(ClaimHistoryEntry.MaxNoteLength);
            history.HasIndex("ClaimId", nameof(ClaimHistoryEntry.At));
        });
        builder.Navigation(c => c.History).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
