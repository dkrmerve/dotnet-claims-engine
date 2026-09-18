using System.Text;
using ClaimsEngine.Api.Composition;
using ClaimsEngine.Api.Contracts;
using ClaimsEngine.Api.Options;
using ClaimsEngine.Api.Validation;
using ClaimsEngine.Application.Ports;
using ClaimsEngine.Domain.Shared;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClaimsEngine.Api.Auth;

/// <summary>Mints HS256 tokens for the dev issuer. Shared by the /auth/token endpoint and the API tests.</summary>
public static class DevTokens
{
    public static string Create(
        DevIssuerOptions options,
        string subject,
        ActorRole role,
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        string? signingKey = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? options.SigningKey!));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + (lifetime ?? TimeSpan.FromMinutes(options.TokenLifetimeMinutes))).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [Authentication.SubjectClaim] = subject,
                [Authentication.RoleClaim] = role.ToString(),
            },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

public sealed record DevTokenResponse(string AccessToken, string TokenType, int ExpiresInSeconds, string Subject, string Role);

public static class DevTokenEndpoint
{
    /// <summary>Only mapped when Auth:DevIssuer:Enabled is true; otherwise the route does not exist (404).</summary>
    public static IEndpointRouteBuilder MapDevTokenEndpoint(this IEndpointRouteBuilder app, AuthOptions auth)
    {
        ArgumentNullException.ThrowIfNull(auth);

        if (!auth.DevIssuer.Enabled)
        {
            return app;
        }

        app.MapPost("/auth/token", Issue)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiting.WritesPolicy)
            .AddEndpointFilter(new ValidationFilter<DevTokenRequest>())
            .WithTags("Auth (dev issuer)")
            .WithName("IssueDevToken")
            .WithSummary("Development only: mint a bearer token for a subject and role. Disabled unless Auth:DevIssuer:Enabled=true.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static Ok<DevTokenResponse> Issue(DevTokenRequest request, IOptions<AuthOptions> options, IClock clock)
    {
        var dev = options.Value.DevIssuer;
        var role = Roles.TryParse(request.Role)!.Value;
        var subject = request.Subject!.Trim();
        var token = DevTokens.Create(dev, subject, role, clock.UtcNow);
        return TypedResults.Ok(new DevTokenResponse(token, "Bearer", dev.TokenLifetimeMinutes * 60, subject, role.ToString()));
    }
}
