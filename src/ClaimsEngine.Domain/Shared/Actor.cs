namespace ClaimsEngine.Domain.Shared;

/// <summary>
/// The authenticated caller: the JWT <c>sub</c> claim and the single business role it carries.
/// Claimants use their holder id as subject, which is what ownership checks compare against.
/// </summary>
public sealed record Actor(string Subject, ActorRole Role)
{
    public bool Owns(HolderId holderId) => string.Equals(Subject, holderId.Value.ToString(), StringComparison.OrdinalIgnoreCase);
}
