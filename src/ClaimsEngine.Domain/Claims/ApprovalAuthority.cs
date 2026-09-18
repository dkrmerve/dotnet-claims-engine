using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Claims;

/// <summary>Rule 5: who may approve a payout of a given size. Limits are inclusive.</summary>
public static class ApprovalAuthority
{
    public static bool CanApprove(ActorRole role, Money payout, ClaimRules rules)
    {
        return role switch
        {
            ActorRole.Manager => true,
            ActorRole.SeniorAdjuster => payout <= rules.SeniorAdjusterApprovalLimit,
            ActorRole.Adjuster => payout <= rules.AdjusterApprovalLimit,
            _ => false,
        };
    }

    public static string DescribeRequiredRoles(Money payout, ClaimRules rules)
    {
        if (payout <= rules.AdjusterApprovalLimit)
        {
            return "Adjuster, SeniorAdjuster or Manager";
        }

        return payout <= rules.SeniorAdjusterApprovalLimit ? "SeniorAdjuster or Manager" : "Manager";
    }
}
