using ClaimsEngine.Application.Ports;
using ClaimsEngine.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaimsEngine.Api.Tests.Support;

/// <summary>Movable clock so filing windows, SLAs and idempotency TTLs can be driven from tests.</summary>
public sealed class TestClock : IClock
{
    public static readonly DateTimeOffset Start = new(2026, 3, 15, 10, 30, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; } = Start;

    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Per-test knobs: configuration overrides, extra EF options (interceptors, batch size) and extra services (log capture).</summary>
public sealed record FactoryOptions(
    IReadOnlyDictionary<string, string?>? Settings = null,
    Action<DbContextOptionsBuilder>? ConfigureDatabase = null,
    Action<IServiceCollection>? ConfigureServices = null,
    string Environment = "Testing");

/// <summary>The real API pipeline (Program.cs) on top of a per-test database, with a test clock and test-only configuration.</summary>
public sealed class ClaimsApiFactory(TestDatabase database, FactoryOptions? options = null) : WebApplicationFactory<Program>
{
    public const string SigningKey = "test-only-signing-key-that-is-at-least-32-chars";

    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(options?.Environment ?? "Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = database.ConnectionString ?? "Host=unused;Database=unused;Username=u;Password=p",
                ["Auth:DevIssuer:Enabled"] = "true",
                ["Auth:DevIssuer:SigningKey"] = SigningKey,
                ["RateLimiting:PermitLimit"] = "100000",
                ["RateLimiting:WindowSeconds"] = "60",
                ["Idempotency:SweepIntervalMinutes"] = "0",
                ["Logging:LogLevel:Default"] = "Warning",
            };

            foreach (var (key, value) in options?.Settings ?? new Dictionary<string, string?>())
            {
                settings[key] = value;
            }

            configuration.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<ClaimsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ClaimsDbContext>>();
            services.AddDbContext<ClaimsDbContext>(db =>
            {
                database.Configure(db);
                db.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
                options?.ConfigureDatabase?.Invoke(db);
            });

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            options?.ConfigureServices?.Invoke(services);
        });
    }
}
