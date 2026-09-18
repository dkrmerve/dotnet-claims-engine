using System.Text.Json.Serialization;
using ClaimsEngine.Api.Auth;
using ClaimsEngine.Api.Composition;
using ClaimsEngine.Api.Endpoints;
using ClaimsEngine.Api.Errors;
using ClaimsEngine.Api.Health;
using ClaimsEngine.Api.Middleware;
using ClaimsEngine.Api.Options;
using ClaimsEngine.Api.Validation;
using ClaimsEngine.Infrastructure;
using ClaimsEngine.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Prometheus;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Structured logging: human-readable in Development, JSON lines everywhere else.
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);
}
else
{
    builder.Logging.AddJsonConsole(options =>
    {
        options.IncludeScopes = true;
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "O";
    });
}

// Kestrel hardening and graceful shutdown. TLS terminates at the edge (see README).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = RequestBodyLimitMiddleware.DefaultMaxBytes;
    kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));

// Configuration, validated at startup.
builder.Services.AddClaimsEngineOptions(builder.Configuration);
// HTTP plumbing: camelCase JSON with string enums, ProblemDetails for every error.
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.Decorate);
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddClaimsEngineAuth();
builder.Services.AddClaimsEngineRateLimiting();
builder.Services.AddClaimsEngineHealthChecks();
builder.Services.AddRequestValidators();

builder.Services.AddClaimsEngineInfrastructure(sp => sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString);
builder.Services.AddClaimsEngineUseCases();
builder.Services.AddHostedService<IdempotencySweeper>();

// Behind a trusted ingress the real client address and scheme arrive in X-Forwarded-* headers.
// The middleware is only added when Proxy:TrustForwardedHeaders is true (see below).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

if (app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.TrustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestBodyLimitMiddleware>(RequestBodyLimitMiddleware.DefaultMaxBytes);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.UseHttpMetrics();

app.MapOpenApi().AllowAnonymous();
app.MapScalarApiReference(SecurityHeadersMiddleware.DocsPath, options => options.WithTitle("Claims Engine API")).AllowAnonymous();
app.MapMetrics("/metrics").AllowAnonymous();

app.MapHealth();
app.MapDevTokenEndpoint(app.Services.GetRequiredService<IOptions<AuthOptions>>().Value);
app.MapPolicies();
app.MapClaims();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClaimsDbContext>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    await DatabaseInitializer.InitializeAsync(db, logger, cancellationToken: app.Lifetime.ApplicationStopping);
}

await app.RunAsync();

/// <summary>Exposes the entry point to WebApplicationFactory in the API tests.</summary>
public partial class Program;
