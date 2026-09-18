using ClaimsEngine.Application.Claims;
using ClaimsEngine.Application.Policies;

namespace ClaimsEngine.Api.Composition;

/// <summary>Registers every use-case handler. Kept in the API so the Application project stays framework-free.</summary>
public static class UseCaseRegistration
{
    public static IServiceCollection AddClaimsEngineUseCases(this IServiceCollection services)
    {
        services.AddScoped<CreatePolicyHandler>();
        services.AddScoped<GetPolicyHandler>();

        services.AddScoped<SubmitClaimHandler>();
        services.AddScoped<GetClaimHandler>();
        services.AddScoped<GetClaimHistoryHandler>();
        services.AddScoped<ListClaimsHandler>();
        services.AddScoped<ListOverdueClaimsHandler>();
        services.AddScoped<StartReviewHandler>();
        services.AddScoped<ApproveClaimHandler>();
        services.AddScoped<RejectClaimHandler>();
        services.AddScoped<PayClaimHandler>();
        services.AddScoped<WithdrawClaimHandler>();
        services.AddScoped<ClearClaimFlagHandler>();
        return services;
    }
}
