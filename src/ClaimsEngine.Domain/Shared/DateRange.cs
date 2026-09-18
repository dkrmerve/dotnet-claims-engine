namespace ClaimsEngine.Domain.Shared;

/// <summary>An inclusive range of calendar days, e.g. one policy year.</summary>
public readonly record struct DateRange(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}
