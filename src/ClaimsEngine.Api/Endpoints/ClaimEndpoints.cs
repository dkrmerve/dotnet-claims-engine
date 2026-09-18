using ClaimsEngine.Api.Auth;
using ClaimsEngine.Api.Composition;
using ClaimsEngine.Api.Contracts;
using ClaimsEngine.Api.Validation;
using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Dtos;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsEngine.Api.Endpoints;

public static class ClaimEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    public static IEndpointRouteBuilder MapClaims(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/claims")
            .WithTags("Claims")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListAsync)
            .RequireAuthorization(AuthorizationPolicies.AnyRole)
            .AddEndpointFilter(new ValidationFilter<ListClaimsRequest>())
            .WithName("ListClaims")
            .WithSummary("List claims newest first, filtered by policyId and/or status, paged (page >= 1, pageSize 1..100, default 20). Claimants only see their own.")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/overdue", ListOverdueAsync)
            .RequireAuthorization(AuthorizationPolicies.AdjusterOrAbove)
            .WithName("ListOverdueClaims")
            .WithSummary("Claims UnderReview for longer than the review SLA (Adjuster+).");

        group.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(AuthorizationPolicies.AnyRole)
            .WithName("GetClaim")
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/history", GetHistoryAsync)
            .RequireAuthorization(AuthorizationPolicies.AnyRole)
            .WithName("GetClaimHistory")
            .WithSummary("Full audit trail of the claim.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        var writes = group.MapGroup("")
            .RequireRateLimiting(RateLimiting.WritesPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        writes.MapPost("/", SubmitAsync)
            .RequireAuthorization(AuthorizationPolicies.AnyRole)
            .AddEndpointFilter(new ValidationFilter<SubmitClaimRequest>())
            .WithName("SubmitClaim")
            .WithSummary("File a claim. Claimants file on their own policies; back-office roles on any. Send an Idempotency-Key header to make retries safe.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        writes.MapPost("/{id:guid}/review", ReviewAsync)
            .RequireAuthorization(AuthorizationPolicies.AdjusterOrAbove)
            .AddEndpointFilter(new ValidationFilter<NoteRequest>(bodyRequired: false))
            .WithName("StartReview")
            .WithSummary("Submitted -> UnderReview (Adjuster+).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        writes.MapPost("/{id:guid}/approve", ApproveAsync)
            .RequireAuthorization(AuthorizationPolicies.AdjusterOrAbove)
            .AddEndpointFilter(new ValidationFilter<NoteRequest>(bodyRequired: false))
            .WithName("ApproveClaim")
            .WithSummary("UnderReview -> Approved. Authority depends on payout size; flagged claims cannot be approved.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        writes.MapPost("/{id:guid}/reject", RejectAsync)
            .RequireAuthorization(AuthorizationPolicies.AdjusterOrAbove)
            .AddEndpointFilter(new ValidationFilter<RejectClaimRequest>())
            .WithName("RejectClaim")
            .WithSummary("Submitted|UnderReview -> Rejected with a mandatory reason (Adjuster+).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        writes.MapPost("/{id:guid}/pay", PayAsync)
            .RequireAuthorization(AuthorizationPolicies.Manager)
            .WithName("PayClaim")
            .WithSummary("Approved -> Paid (Manager). Re-checks the annual limit inside the transaction.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        writes.MapPost("/{id:guid}/withdraw", WithdrawAsync)
            .RequireAuthorization(AuthorizationPolicies.Claimant)
            .AddEndpointFilter(new ValidationFilter<NoteRequest>(bodyRequired: false))
            .WithName("WithdrawClaim")
            .WithSummary("Submitted|UnderReview -> Withdrawn (the owning Claimant).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        writes.MapPost("/{id:guid}/clear-flag", ClearFlagAsync)
            .RequireAuthorization(AuthorizationPolicies.Manager)
            .AddEndpointFilter(new ValidationFilter<ClearFlagRequest>())
            .WithName("ClearInvestigationFlag")
            .WithSummary("Manager clears the RequiresInvestigation flag with a mandatory note.")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Created<ClaimDto>> SubmitAsync(
        SubmitClaimRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        HttpContext http,
        SubmitClaimHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(request.ToCommand(idempotencyKey, http.Actor()), cancellationToken);
        if (result.Replayed)
        {
            http.Response.Headers[ReplayedHeader] = "true";
        }

        return TypedResults.Created($"/claims/{result.Claim.Id}", result.Claim);
    }

    private static async Task<Ok<ClaimDto>> GetAsync(Guid id, HttpContext http, GetClaimHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new GetClaimQuery(id, http.Actor()), cancellationToken));

    private static async Task<Ok<IReadOnlyList<ClaimHistoryEntryDto>>> GetHistoryAsync(Guid id, HttpContext http, GetClaimHistoryHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new GetClaimQuery(id, http.Actor()), cancellationToken));

    private static async Task<Ok<PagedResponse<ClaimDto>>> ListAsync([AsParameters] ListClaimsRequest request, HttpContext http, ListClaimsHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(request.ToQuery(http.Actor()), cancellationToken));

    private static async Task<Ok<IReadOnlyList<ClaimDto>>> ListOverdueAsync(ListOverdueClaimsHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(cancellationToken));

    private static async Task<Ok<ClaimDto>> ReviewAsync(Guid id, NoteRequest? body, HttpContext http, StartReviewHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new StartReviewCommand(id, http.Actor(), body?.Note), cancellationToken));

    private static async Task<Ok<ClaimDto>> ApproveAsync(Guid id, NoteRequest? body, HttpContext http, ApproveClaimHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new ApproveClaimCommand(id, http.Actor(), body?.Note), cancellationToken));

    private static async Task<Ok<ClaimDto>> RejectAsync(Guid id, RejectClaimRequest body, HttpContext http, RejectClaimHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new RejectClaimCommand(id, http.Actor(), body.ParsedReason, body.Note), cancellationToken));

    private static async Task<Ok<ClaimDto>> PayAsync(Guid id, HttpContext http, PayClaimHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new PayClaimCommand(id, http.Actor()), cancellationToken));

    private static async Task<Ok<ClaimDto>> WithdrawAsync(Guid id, NoteRequest? body, HttpContext http, WithdrawClaimHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new WithdrawClaimCommand(id, http.Actor(), body?.Note), cancellationToken));

    private static async Task<Ok<ClaimDto>> ClearFlagAsync(Guid id, ClearFlagRequest body, HttpContext http, ClearClaimFlagHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new ClearClaimFlagCommand(id, http.Actor(), body.Note), cancellationToken));
}
