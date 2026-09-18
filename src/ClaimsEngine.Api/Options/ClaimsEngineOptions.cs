using System.ComponentModel.DataAnnotations;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Shared;
using Microsoft.Extensions.Options;

namespace ClaimsEngine.Api.Options;

// Every knob is bound from configuration (appsettings.json or environment variables such as
// Rules__FilingWindowDays) and validated at startup: an invalid value stops the process.

public sealed class RulesOptions
{
    public const string Section = "Rules";

    [Range(1, 3650)]
    public int FilingWindowDays { get; set; } = 30;

    [Range(1, 1000)]
    public int FrequentClaimantThreshold { get; set; } = 3;

    [Range(1, 3650)]
    public int FrequentClaimantWindowDays { get; set; } = 365;

    [Range(0.01, 1.0)]
    public decimal HighValueShareOfLimit { get; set; } = 0.80m;

    [Range(1, 3650)]
    public int ReviewSlaDays { get; set; } = 14;

    [Range(0, 1_000_000_000)]
    public decimal AdjusterApprovalLimit { get; set; } = 5_000m;

    [Range(0, 1_000_000_000)]
    public decimal SeniorAdjusterApprovalLimit { get; set; } = 50_000m;

    public ClaimRules ToDomain() => new(
        FilingWindowDays,
        FrequentClaimantThreshold,
        FrequentClaimantWindowDays,
        HighValueShareOfLimit,
        ReviewSlaDays,
        Money.Euro(AdjusterApprovalLimit),
        Money.Euro(SeniorAdjusterApprovalLimit));
}

public sealed class DatabaseOptions
{
    public const string ConnectionName = "Default";

    /// <summary>Npgsql connection string incl. pooling; env ConnectionStrings__Default.</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

public sealed class IdempotencyOptions
{
    public const string Section = "Idempotency";

    [Range(1, 24 * 30)]
    public int TtlHours { get; set; } = 24;

    /// <summary>How often expired keys are deleted; 0 disables the sweeper (tests).</summary>
    [Range(0, 24 * 60)]
    public int SweepIntervalMinutes { get; set; } = 60;
}

public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>Honour X-Forwarded-For / X-Forwarded-Proto from the ingress. Off by default; enable only behind a trusted proxy.</summary>
    public bool TrustForwardedHeaders { get; set; }
}

public sealed class RateLimitingOptions
{
    public const string Section = "RateLimiting";

    /// <summary>Write requests allowed per client address per window.</summary>
    [Range(1, 100_000)]
    public int PermitLimit { get; set; } = 60;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 10;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>OIDC issuer URL (Entra ID, Keycloak, Auth0, ...). When set, tokens are validated against its discovery document.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected audience when <see cref="Authority"/> is set.</summary>
    public string? Audience { get; set; }

    public DevIssuerOptions DevIssuer { get; set; } = new();

    public bool UsesExternalIssuer => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>Symmetric-key issuer for local development and tests. Never enable it in production.</summary>
public sealed class DevIssuerOptions
{
    public const int MinSigningKeyLength = 32;

    /// <summary>Exposes POST /auth/token. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>HMAC key, at least 32 characters. Required whenever no Authority is configured; there is no built-in default.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "claims-engine-dev";

    public string Audience { get; set; } = "claims-engine";

    [Range(1, 24 * 60)]
    public int TokenLifetimeMinutes { get; set; } = 60;
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var failures = new List<string>();

        if (options.UsesExternalIssuer)
        {
            if (string.IsNullOrWhiteSpace(options.Audience))
            {
                failures.Add("Auth:Audience is required when Auth:Authority is set.");
            }

            if (options.DevIssuer.Enabled)
            {
                failures.Add("Auth:DevIssuer:Enabled cannot be true when Auth:Authority is set.");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(options.DevIssuer.SigningKey))
            {
                failures.Add("Auth:DevIssuer:SigningKey is required when no Auth:Authority is configured (env Auth__DevIssuer__SigningKey).");
            }
            else if (options.DevIssuer.SigningKey.Length < DevIssuerOptions.MinSigningKeyLength)
            {
                failures.Add($"Auth:DevIssuer:SigningKey must be at least {DevIssuerOptions.MinSigningKeyLength} characters.");
            }

            if (string.IsNullOrWhiteSpace(options.DevIssuer.Issuer) || string.IsNullOrWhiteSpace(options.DevIssuer.Audience))
            {
                failures.Add("Auth:DevIssuer:Issuer and Auth:DevIssuer:Audience are required.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class OptionsRegistration
{
    public static IServiceCollection AddClaimsEngineOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Configure(o => o.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionName) ?? string.Empty)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "ConnectionStrings:Default is not configured (environment variable ConnectionStrings__Default).")
            .ValidateOnStart();

        services.AddOptions<RulesOptions>()
            .Bind(configuration.GetSection(RulesOptions.Section))
            .ValidateDataAnnotations()
            .Validate(
                o => o.SeniorAdjusterApprovalLimit >= o.AdjusterApprovalLimit,
                "Rules:SeniorAdjusterApprovalLimit must be at least Rules:AdjusterApprovalLimit.")
            .ValidateOnStart();

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ProxyOptions>()
            .Bind(configuration.GetSection(ProxyOptions.Section))
            .ValidateOnStart();

        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RulesOptions>>().Value.ToDomain());
        services.AddSingleton(sp => new IdempotencySettings(TimeSpan.FromHours(sp.GetRequiredService<IOptions<IdempotencyOptions>>().Value.TtlHours)));
        return services;
    }
}
