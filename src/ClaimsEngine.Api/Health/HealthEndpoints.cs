using ClaimsEngine.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClaimsEngine.Api.Health;

/// <summary>Readiness probe: a real <c>SELECT 1</c> through the connection pool.</summary>
public sealed class DatabaseHealthCheck(ClaimsDbContext db) : IHealthCheck
{
    public const string Name = "database";
    public const string ReadyTag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy("SELECT 1 succeeded");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("database unreachable", ex);
        }
    }
}

public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string OverallPath = "/health";

    public static IServiceCollection AddClaimsEngineHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, tags: [DatabaseHealthCheck.ReadyTag]);
        return services;
    }

    /// <summary>
    /// /health/live answers as soon as the process serves requests, /health/ready only when the
    /// database answers, /health runs every check. All three are anonymous.
    /// </summary>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync }).AllowAnonymous();
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = r => r.Tags.Contains(DatabaseHealthCheck.ReadyTag), ResponseWriter = WriteAsync }).AllowAnonymous();
        app.MapHealthChecks(OverallPath, new HealthCheckOptions { ResponseWriter = WriteAsync }).AllowAnonymous();
        return app;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                description = entry.Value.Description,
            }),
        };

        return context.Response.WriteAsJsonAsync(payload);
    }
}
