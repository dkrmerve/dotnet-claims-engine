using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaimsEngine.Infrastructure.Persistence;

/// <summary>Used only by "dotnet ef migrations add"; no database connection is opened.</summary>
public sealed class ClaimsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ClaimsDbContext>
{
    public ClaimsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ClaimsDbContext>()
            .UseNpgsql("Host=localhost;Database=claims;Username=claims;Password=claims")
            .Options;
        return new ClaimsDbContext(options);
    }
}
