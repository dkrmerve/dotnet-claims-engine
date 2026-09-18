using ClaimsEngine.Api.Auth;
using ClaimsEngine.Api.Composition;
using ClaimsEngine.Api.Contracts;
using ClaimsEngine.Api.Validation;
using ClaimsEngine.Application.Dtos;
using ClaimsEngine.Application.Policies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ClaimsEngine.Api.Endpoints;

public static class PolicyEndpoints
{
    public static IEndpointRouteBuilder MapPolicies(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/policies").WithTags("Policies");

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(AuthorizationPolicies.Manager)
            .RequireRateLimiting(RateLimiting.WritesPolicy)
            .AddEndpointFilter(new ValidationFilter<CreatePolicyRequest>())
            .WithName("CreatePolicy")
            .WithSummary("Create a policy (Manager).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(AuthorizationPolicies.AnyRole)
            .WithName("GetPolicy")
            .WithSummary("Fetch a policy. Claimants only see their own.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Created<PolicyDto>> CreateAsync(CreatePolicyRequest request, CreatePolicyHandler handler, CancellationToken cancellationToken)
    {
        var policy = await handler.HandleAsync(request.ToCommand(), cancellationToken);
        return TypedResults.Created($"/policies/{policy.Id}", policy);
    }

    private static async Task<Ok<PolicyDto>> GetAsync(Guid id, HttpContext http, GetPolicyHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new GetPolicyQuery(id, http.Actor()), cancellationToken));
}
