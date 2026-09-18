using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Application.Dtos;

public sealed record MoneyDto(decimal Amount, string Currency)
{
    public static MoneyDto From(Money money) => new(money.Amount, money.Currency);

    public static MoneyDto? From(Money? money) => money is { } m ? From(m) : null;
}

public sealed record PolicyDto(
    Guid Id,
    string PolicyNumber,
    Guid HolderId,
    CoverageType CoverageType,
    MoneyDto CoverageLimit,
    MoneyDto Deductible,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    PolicyStatus Status,
    MoneyDto LifetimePaid,
    long Version)
{
    public static PolicyDto From(Policy p) => new(
        p.Id.Value,
        p.PolicyNumber,
        p.HolderId.Value,
        p.CoverageType,
        MoneyDto.From(p.CoverageLimit),
        MoneyDto.From(p.Deductible),
        p.EffectiveFrom,
        p.EffectiveTo,
        p.Status,
        MoneyDto.From(p.LifetimePaid),
        p.Version);
}

public sealed record ClaimHistoryEntryDto(
    DateTimeOffset At,
    ActorRole ActorRole,
    ClaimStatus? FromStatus,
    ClaimStatus ToStatus,
    string? Note)
{
    public static ClaimHistoryEntryDto From(ClaimHistoryEntry e) => new(e.At, e.ActorRole, e.FromStatus, e.ToStatus, e.Note);
}

public sealed record ClaimDto(
    Guid Id,
    Guid PolicyId,
    DateOnly IncidentDate,
    DateTimeOffset FiledAt,
    MoneyDto ClaimedAmount,
    string Description,
    ClaimStatus Status,
    MoneyDto EligiblePayout,
    MoneyDto? ApprovedPayout,
    ClaimFlag Flag,
    RejectionReason? RejectionReason,
    DateTimeOffset? ReviewStartedAt,
    DateTimeOffset? PaidAt,
    long Version)
{
    public static ClaimDto From(Claim c) => new(
        c.Id.Value,
        c.PolicyId.Value,
        c.IncidentDate,
        c.FiledAt,
        MoneyDto.From(c.ClaimedAmount),
        c.Description,
        c.Status,
        MoneyDto.From(c.EligiblePayout),
        MoneyDto.From(c.ApprovedPayout),
        c.Flag,
        c.RejectionReason,
        c.ReviewStartedAt,
        c.PaidAt,
        c.Version);
}

/// <summary>One page of a list. <see cref="Page"/> is 1-based.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
