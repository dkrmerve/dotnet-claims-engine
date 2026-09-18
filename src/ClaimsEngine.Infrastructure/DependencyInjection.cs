using ClaimsEngine.Application.Ports;
using ClaimsEngine.Infrastructure.Persistence;
using ClaimsEngine.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsEngine.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// EF Core on PostgreSQL plus every port adapter. Pooling (Minimum/Maximum Pool Size, Timeout,
    /// Command Timeout) is part of the connection string, see appsettings.json and the README.
    /// </summary>
    public static IServiceCollection AddClaimsEngineInfrastructure(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddDbContext<ClaimsDbContext>((provider, options) => ConfigureNpgsql(options, connectionString(provider)));
        return services.AddClaimsEnginePortAdapters();
    }

    public static void ConfigureNpgsql(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

    /// <summary>Port adapters only; the caller configures <see cref="ClaimsDbContext"/> itself (tests use SQLite or a test database).</summary>
    public static IServiceCollection AddClaimsEnginePortAdapters(this IServiceCollection services)
    {
        services.AddScoped<IPolicyRepository, EfPolicyRepository>();
        services.AddScoped<IClaimRepository, EfClaimRepository>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
