using ClaimsEngine.Api.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimsEngine.Api.Tests;

/// <summary>Secure by construction: every routed endpoint must state an authorization policy or AllowAnonymous. A forgotten endpoint fails this test.</summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointSecurityTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Fact]
    public void EveryEndpoint_DeclaresAuthorizationOrAllowAnonymous()
    {
        var endpoints = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(endpoints);

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null && e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        Assert.Empty(undeclared);
    }

    [Fact]
    public void AnonymousEndpoints_AreOnlyTheOperationalOnes()
    {
        var anonymous = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .Order()
            .ToList();

        Assert.All(anonymous, route => Assert.True(
            route.StartsWith("/health", StringComparison.Ordinal)
            || route.StartsWith("/openapi", StringComparison.Ordinal)
            || route.StartsWith("/docs", StringComparison.Ordinal)
            || route.StartsWith("/scalar", StringComparison.Ordinal)
            || route == "/metrics"
            || route == "/auth/token",
            $"Unexpected anonymous endpoint: {route}"));
    }
}
