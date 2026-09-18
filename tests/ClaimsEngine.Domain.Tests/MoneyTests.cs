using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Money value object: non-negative, two decimals, single currency, half-even rounding.</summary>
public sealed class MoneyTests
{
    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1)]
    [InlineData(-1000000)]
    public void Money_NegativeAmount_IsRejected(decimal amount)
    {
        var ex = Assert.Throws<ValidationException>(() => new Money(amount));
        Assert.Equal("validation_error", ex.Code);
        Assert.Equal(ErrorKind.Validation, ex.Kind);
    }

    [Theory]
    [InlineData(1.005)]
    [InlineData(0.001)]
    [InlineData(19.999)]
    public void Money_MoreThanTwoDecimals_IsRejected(decimal amount)
    {
        var ex = Assert.Throws<ValidationException>(() => new Money(amount));
        Assert.Contains("two decimals", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.01)]
    [InlineData(1.10)]
    [InlineData(5000)]
    [InlineData(999999999.99)]
    public void Money_ValidAmount_IsAccepted(decimal amount)
    {
        var money = new Money(amount);
        Assert.Equal(amount, money.Amount);
        Assert.Equal("EUR", money.Currency);
    }

    [Theory]
    [InlineData(1.005, 1.00)]
    [InlineData(1.015, 1.02)]
    [InlineData(1.025, 1.02)]
    [InlineData(2.675, 2.68)]
    [InlineData(0.004, 0.00)]
    public void Money_Round_RoundsHalfToEvenToTwoDecimals(decimal input, decimal expected)
    {
        Assert.Equal(expected, Money.Round(input).Amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    [InlineData(null)]
    public void Money_InvalidCurrencyCode_IsRejected(string? currency)
    {
        Assert.Throws<ValidationException>(() => new Money(1m, currency!));
    }

    [Fact]
    public void Money_CurrencyCode_IsUpperCased()
    {
        Assert.Equal("USD", new Money(1m, "usd").Currency);
    }

    [Fact]
    public void Money_MixedCurrencies_CannotBeCombinedOrCompared()
    {
        var eur = Money.Euro(10m);
        var usd = new Money(10m, "USD");

        Assert.Throws<ValidationException>(() => eur + usd);
        Assert.Throws<ValidationException>(() => eur - usd);
        Assert.Throws<ValidationException>(() => eur < usd);
        Assert.Throws<ValidationException>(() => eur.CompareTo(usd));
    }

    [Fact]
    public void Money_SubtractionBelowZero_IsRejected()
    {
        Assert.Throws<ValidationException>(() => Money.Euro(1m) - Money.Euro(2m));
    }

    [Theory]
    [InlineData(10, 2.5, 12.5)]
    [InlineData(0, 0, 0)]
    [InlineData(0.01, 0.02, 0.03)]
    public void Money_Addition_AddsAmounts(decimal left, decimal right, decimal expected)
    {
        Assert.Equal(Money.Euro(expected), Money.Euro(left) + Money.Euro(right));
    }

    [Theory]
    [InlineData(10, 2.5, 7.5)]
    [InlineData(1, 1, 0)]
    public void Money_Subtraction_SubtractsAmounts(decimal left, decimal right, decimal expected)
    {
        Assert.Equal(Money.Euro(expected), Money.Euro(left) - Money.Euro(right));
    }

    [Theory]
    [InlineData(20000, 0.80, 16000)]
    [InlineData(333.33, 0.5, 166.66)]
    [InlineData(0.01, 0.5, 0.00)]
    public void Money_MultiplyByFactor_RoundsHalfEven(decimal amount, decimal factor, decimal expected)
    {
        Assert.Equal(expected, (Money.Euro(amount) * factor).Amount);
    }

    [Fact]
    public void Money_ComparisonOperators_FollowAmount()
    {
        var small = Money.Euro(1m);
        var big = Money.Euro(2m);

        Assert.True(small < big);
        Assert.True(small <= big);
        Assert.True(big > small);
        Assert.True(big >= small);
        Assert.True(small <= Money.Euro(1m));
        Assert.True(small >= Money.Euro(1m));
        Assert.False(small > big);
        Assert.False(big < small);
        Assert.Equal(Money.Euro(1m), Money.Min(small, big));
        Assert.Equal(Money.Euro(1m), Money.Min(big, small));
    }

    [Fact]
    public void Money_Zero_And_IsZero()
    {
        Assert.True(Money.Zero.IsZero);
        Assert.False(Money.Euro(0.01m).IsZero);
        Assert.Equal(0m, Money.Zero.Amount);
    }

    [Fact]
    public void Money_DefaultStruct_HasEurCurrency()
    {
        Money money = default;
        Assert.Equal("EUR", money.Currency);
        Assert.Equal(0m, money.Amount);
    }

    [Fact]
    public void Money_ToString_IsInvariantWithTwoDecimals()
    {
        Assert.Equal("1234.50 EUR", Money.Euro(1234.5m).ToString());
        Assert.Equal("0.00 EUR", Money.Zero.ToString());
    }

    [Fact]
    public void Money_ValueEquality_ComparesAmountAndCurrency()
    {
        Assert.Equal(Money.Euro(5m), Money.Euro(5m));
        Assert.NotEqual(Money.Euro(5m), new Money(5m, "USD"));
        Assert.NotEqual(Money.Euro(5m), Money.Euro(5.01m));
    }
}
