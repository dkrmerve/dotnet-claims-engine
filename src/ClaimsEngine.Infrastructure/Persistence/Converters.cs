using ClaimsEngine.Domain.Shared;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ClaimsEngine.Infrastructure.Persistence;

public sealed class PolicyIdConverter() : ValueConverter<PolicyId, Guid>(id => id.Value, value => new PolicyId(value));

public sealed class ClaimIdConverter() : ValueConverter<ClaimId, Guid>(id => id.Value, value => new ClaimId(value));

public sealed class HolderIdConverter() : ValueConverter<HolderId, Guid>(id => id.Value, value => new HolderId(value));

/// <summary>Money is single-currency (EUR) in this service, so only the amount is stored.</summary>
public sealed class MoneyConverter() : ValueConverter<Money, decimal>(money => money.Amount, amount => new Money(amount, Money.DefaultCurrency));

/// <summary>UTC ticks in a 64-bit column: exact, and comparable/orderable on providers without a native offset type.</summary>
public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    value => value.UtcTicks,
    ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
