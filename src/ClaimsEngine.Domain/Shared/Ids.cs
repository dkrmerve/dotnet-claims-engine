namespace ClaimsEngine.Domain.Shared;

/// <summary>Strongly typed identifier for a <see cref="Policies.Policy"/>.</summary>
public readonly record struct PolicyId(Guid Value)
{
    public static PolicyId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly typed identifier for a <see cref="Claims.Claim"/>.</summary>
public readonly record struct ClaimId(Guid Value)
{
    public static ClaimId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

/// <summary>Strongly typed identifier for the person who holds a policy.</summary>
public readonly record struct HolderId(Guid Value)
{
    public static HolderId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
