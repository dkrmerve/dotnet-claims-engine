namespace ClaimsEngine.Domain.Shared;

/// <summary>Who is performing an action. Comes from the <c>role</c> claim of the bearer token.</summary>
public enum ActorRole
{
    /// <summary>The policyholder filing or withdrawing a claim.</summary>
    Claimant,
    Adjuster,
    SeniorAdjuster,
    Manager,
    /// <summary>Automatic decisions made by the engine itself (auto-rejections).</summary>
    System,
}

public static class ActorRoleExtensions
{
    /// <summary>Roles that work claims: review, reject, pay.</summary>
    public static bool IsAdjusterOrAbove(this ActorRole role) =>
        role is ActorRole.Adjuster or ActorRole.SeniorAdjuster or ActorRole.Manager;
}
