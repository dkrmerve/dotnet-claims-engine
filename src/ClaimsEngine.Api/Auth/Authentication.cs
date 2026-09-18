using System.Security.Claims;
using System.Text;
using ClaimsEngine.Api.Errors;
using ClaimsEngine.Api.Options;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ClaimsEngine.Api.Auth;

/// <summary>Names of the authorization policies applied to endpoints.</summary>
public static class AuthorizationPolicies
{
    public const string AnyRole = "AnyRole";
    public const string Claimant = "Claimant";
    public const string AdjusterOrAbove = "AdjusterOrAbove";
    public const string Manager = "Manager";
}

/// <summary>The business roles a token may carry in its <c>role</c> claim.</summary>
public static class Roles
{
    public static readonly string[] All =
    [
        nameof(ActorRole.Claimant),
        nameof(ActorRole.Adjuster),
        nameof(ActorRole.SeniorAdjuster),
        nameof(ActorRole.Manager),
    ];

    public static readonly string[] AdjusterOrAbove =
    [
        nameof(ActorRole.Adjuster),
        nameof(ActorRole.SeniorAdjuster),
        nameof(ActorRole.Manager),
    ];

    public static string AllowedList => string.Join(", ", All);

    public static ActorRole? TryParse(string? value)
    {
        foreach (var name in All)
        {
            if (string.Equals(name, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<ActorRole>(name);
            }
        }

        return null;
    }
}

/// <summary>
/// JWT bearer authentication in one of two modes, chosen by configuration:
/// an external OIDC issuer (Auth:Authority + Auth:Audience) or the symmetric dev issuer.
/// Failures are answered as ProblemDetails (401 unauthenticated / 403 forbidden_role).
/// </summary>
public static class Authentication
{
    public const string SubjectClaim = "sub";
    public const string RoleClaim = "role";

    public static IServiceCollection AddClaimsEngineAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) => Configure(jwt, auth.Value));

        // No fallback policy: every endpoint names its policy or AllowAnonymous explicitly, and
        // EndpointSecurityTests fails the build if one is missing. Unknown routes stay 404.
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.AnyRole, policy => policy.RequireRole(Roles.All))
            .AddPolicy(AuthorizationPolicies.Claimant, policy => policy.RequireRole(nameof(ActorRole.Claimant)))
            .AddPolicy(AuthorizationPolicies.AdjusterOrAbove, policy => policy.RequireRole(Roles.AdjusterOrAbove))
            .AddPolicy(AuthorizationPolicies.Manager, policy => policy.RequireRole(nameof(ActorRole.Manager)));

        return services;
    }

    /// <summary>The caller as the domain sees it. Only valid behind an authorization policy.</summary>
    public static Actor Actor(this HttpContext http) => Resolve(http.User);

    public static Actor Resolve(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var subject = user.FindFirst(SubjectClaim)?.Value;
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new UnauthenticatedException("The token carries no sub claim.");
        }

        foreach (var claim in user.FindAll(RoleClaim))
        {
            if (Roles.TryParse(claim.Value) is { } role)
            {
                return new Actor(subject, role);
            }
        }

        throw new UnauthenticatedException($"The token carries no known role claim. Known roles: {Roles.AllowedList}.");
    }

    private static void Configure(JwtBearerOptions jwt, AuthOptions auth)
    {
        // Keep the raw JWT claim names ("sub", "role") instead of the legacy SOAP-style URIs.
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters.NameClaimType = SubjectClaim;
        jwt.TokenValidationParameters.RoleClaimType = RoleClaim;

        if (auth.UsesExternalIssuer)
        {
            jwt.Authority = auth.Authority;
            jwt.Audience = auth.Audience;
        }
        else
        {
            var dev = auth.DevIssuer;
            jwt.TokenValidationParameters.ValidateIssuer = true;
            jwt.TokenValidationParameters.ValidIssuer = dev.Issuer;
            jwt.TokenValidationParameters.ValidateAudience = true;
            jwt.TokenValidationParameters.ValidAudience = dev.Audience;
            jwt.TokenValidationParameters.ValidateIssuerSigningKey = true;
            jwt.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(dev.SigningKey!));
            jwt.TokenValidationParameters.ValidateLifetime = true;
            jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
        }

        jwt.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                var detail = string.IsNullOrWhiteSpace(context.ErrorDescription)
                    ? "A valid bearer token is required."
                    : context.ErrorDescription;
                await ApiProblems.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, ErrorMapper.UnauthenticatedCode, detail);
            },
            OnForbidden = context => ApiProblems.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                ErrorMapper.ForbiddenRoleCode,
                "The caller's role is not allowed to perform this operation."),
        };
    }
}
