using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace ClaimsEngine.Infrastructure.Persistence;

public sealed class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : DbContext(options)
{
    public DbSet<Policy> Policies => Set<Policy>();

    public DbSet<Claim> Claims => Set<Claim>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Strongly typed ids and Money are stored as their primitive value.
        configurationBuilder.Properties<PolicyId>().HaveConversion<PolicyIdConverter>();
        configurationBuilder.Properties<ClaimId>().HaveConversion<ClaimIdConverter>();
        configurationBuilder.Properties<HolderId>().HaveConversion<HolderIdConverter>();
        configurationBuilder.Properties<Money>().HaveConversion<MoneyConverter>().HavePrecision(18, 2);
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

        // Enums are stored as their names so the database stays readable and reorder-safe.
        configurationBuilder.Properties<CoverageType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<PolicyStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ClaimStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ClaimFlag>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<RejectionReason>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ActorRole>().HaveConversion<string>().HaveMaxLength(32);

        if (!Database.IsNpgsql())
        {
            // SQLite (tests) cannot compare or order DateTimeOffset columns; store UTC ticks instead.
            // PostgreSQL uses native "timestamp with time zone".
            configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ClaimsDbContext).Assembly);
        AddCheckConstraints(modelBuilder, numericColumnsAreText: !Database.IsNpgsql());

        if (Database.IsNpgsql())
        {
            // PostgreSQL's system column xmin changes on every write: a second, database-managed
            // guard next to the domain-managed Version column.
            modelBuilder.Entity<Policy>().Property<uint>("xmin").IsRowVersion();
            modelBuilder.Entity<Claim>().Property<uint>("xmin").IsRowVersion();
            modelBuilder.Entity<IdempotencyRecord>().Property<uint>("xmin").IsRowVersion();
        }
    }

    /// <summary>
    /// Database-level guards behind the domain invariants. SQLite stores decimals as TEXT, so the
    /// fast test profile compares through CAST; PostgreSQL compares numerics directly.
    /// </summary>
    private static void AddCheckConstraints(ModelBuilder modelBuilder, bool numericColumnsAreText)
    {
        string Num(string column) => numericColumnsAreText ? $"CAST(\"{column}\" AS REAL)" : $"\"{column}\"";

        modelBuilder.Entity<Policy>().ToTable("policies", table =>
        {
            table.HasCheckConstraint("ck_policies_coverage_limit_positive", $"{Num("CoverageLimit")} > 0");
            table.HasCheckConstraint("ck_policies_deductible_below_limit", $"{Num("Deductible")} >= 0 AND {Num("Deductible")} < {Num("CoverageLimit")}");
            table.HasCheckConstraint("ck_policies_period", "\"EffectiveTo\" > \"EffectiveFrom\"");
        });

        modelBuilder.Entity<Claim>().ToTable("claims", table =>
        {
            table.HasCheckConstraint("ck_claims_claimed_amount_positive", $"{Num("ClaimedAmount")} > 0");
            table.HasCheckConstraint("ck_claims_eligible_payout_non_negative", $"{Num("EligiblePayout")} >= 0");
        });
    }
}
