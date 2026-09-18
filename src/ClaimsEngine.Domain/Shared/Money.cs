using System.Globalization;
using ClaimsEngine.Domain.Exceptions;

namespace ClaimsEngine.Domain.Shared;

/// <summary>
/// A non-negative amount in one currency (EUR by default) with at most two decimals.
/// The constructor is strict (more than two decimals is rejected); results of arithmetic that
/// can produce fractions of a cent, such as percentages, go through <see cref="Round"/>, which
/// rounds half-to-even. Subtraction below zero throws; use <see cref="Amount"/> for intermediate
/// maths that may legitimately go negative.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public const string DefaultCurrency = "EUR";

    /// <summary>Largest representable amount; matches the numeric(18,2) columns amounts are stored in.</summary>
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    private readonly string? _currency;

    public Money(decimal amount, string currency = DefaultCurrency)
    {
        if (amount < 0)
        {
            throw new ValidationException($"Money cannot be negative (was {amount.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (amount > MaxAmount)
        {
            throw new ValidationException($"Money cannot exceed {MaxAmount.ToString(CultureInfo.InvariantCulture)} (was {amount.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (decimal.Round(amount, 2) != amount)
        {
            throw new ValidationException($"Money supports at most two decimals (was {amount.ToString(CultureInfo.InvariantCulture)}).");
        }

        if (!IsIsoCurrencyCode(currency))
        {
            throw new ValidationException($"Currency must be a three-letter ISO code (was '{currency}').");
        }

        Amount = amount;
        _currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }

    public string Currency => _currency ?? DefaultCurrency;

    public static Money Zero => new(0m);

    public bool IsZero => Amount == 0m;

    public static Money Euro(decimal amount) => new(amount);

    /// <summary>Rounds half-to-even to cents before constructing; for derived amounts such as percentages.</summary>
    public static Money Round(decimal amount, string currency = DefaultCurrency) =>
        new(decimal.Round(amount, 2, MidpointRounding.ToEven), currency);

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount + right.Amount, left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(left.Amount - right.Amount, left.Currency);
    }

    public static Money operator *(Money left, decimal factor) => Round(left.Amount * factor, left.Currency);

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static Money Min(Money left, Money right) => left <= right ? left : right;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Amount:0.00} {Currency}");

    private static bool IsIsoCurrencyCode(string? currency) =>
        currency is { Length: 3 } && currency.All(char.IsAsciiLetter);

    private static void EnsureSameCurrency(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new ValidationException($"Cannot combine {left.Currency} with {right.Currency}.");
        }
    }
}
