using System.Threading.RateLimiting;
using ClaimsEngine.Api.Errors;
using ClaimsEngine.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ClaimsEngine.Api.Composition;

/// <summary>
/// Fixed-window limiter for write endpoints, partitioned by the authenticated subject (falling
/// back to the client address for anonymous calls such as /auth/token). Limits come from
/// RateLimiting:PermitLimit / RateLimiting:WindowSeconds. Rejections are ProblemDetails 429.
/// Behind a proxy, enable Proxy:TrustForwardedHeaders so the address is the real client.
/// </summary>
public static class RateLimiting
{
    public const string WritesPolicy = "writes";

    public static IServiceCollection AddClaimsEngineRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                }

                await ApiProblems.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    ErrorMapper.RateLimitedCode,
                    "Too many write requests from this client; retry after the window resets.");
            };

            limiter.AddPolicy(WritesPolicy, httpContext =>
            {
                var options = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                var client = PartitionKey(httpContext);
                return RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });
        });

        return services;
    }

    public static string PartitionKey(HttpContext httpContext)
    {
        var subject = httpContext.User.FindFirst(Auth.Authentication.SubjectClaim)?.Value;
        return string.IsNullOrWhiteSpace(subject)
            ? "ip:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : "sub:" + subject;
    }
}
