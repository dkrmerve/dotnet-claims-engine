using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application;

/// <summary>Claimants only see and touch their own policies; back-office roles see everything.</summary>
public static class Ownership
{
    public static void EnsureCanAccess(Actor actor, Policy policy)
    {
        if (actor.Role == ActorRole.Claimant && !actor.Owns(policy.HolderId))
        {
            throw new NotOwnerException($"Policy {policy.PolicyNumber} does not belong to the caller.");
        }
    }

    /// <summary>The holder filter a list query must apply for this actor; null means "no restriction".</summary>
    public static HolderId? VisibleHolder(Actor actor)
    {
        if (actor.Role != ActorRole.Claimant)
        {
            return null;
        }

        // A claimant whose subject is not a holder id owns nothing; an empty guid matches no policy.
        return new HolderId(Guid.TryParse(actor.Subject, out var holder) ? holder : Guid.Empty);
    }
}
