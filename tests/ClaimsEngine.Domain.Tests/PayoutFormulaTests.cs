using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 3: eligiblePayout = min(claimed - deductible, limit - alreadyPaidInPolicyYear); non-positive results auto-reject.</summary>
public sealed class PayoutFormulaTests
{
    [Fact]
    public void Payout_ClaimedMinusDeductible_WhenUnderRemainingLimit()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 20_000m, deductible: 500m);

        var claim = Fixtures.Submitted(policy, claimed: 3_000m);

        Assert.Equal(Money.Euro(2_500m), claim.EligiblePayout);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
    }

    [Fact]
    public void Payout_CappedByRemainingAnnualLimit()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 20_000m, deductible: 500m);
        var context = new ClaimSubmissionContext(Money.Euro(18_000m), 0);

        var claim = Fixtures.Submitted(policy, claimed: 3_000m, context: context);

        Assert.Equal(Money.Euro(2_000m), claim.EligiblePayout);
    }

    [Fact]
    public void Payout_ClaimedMinusDeductibleExactlyEqualsRemainingLimit_PaysFullRemainder()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 20_000m, deductible: 500m);
        var context = new ClaimSubmissionContext(Money.Euro(17_500m), 0);

        var claim = Fixtures.Submitted(policy, claimed: 3_000m, context: context);

        Assert.Equal(Money.Euro(2_500m), claim.EligiblePayout);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
    }

    [Fact]
    public void Payout_ClaimedEqualsDeductible_IsRejectedBelowDeductible()
    {
        var policy = Fixtures.ActivePolicy(deductible: 500m);

        var claim = Fixtures.Submitted(policy, claimed: 500m);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal(RejectionReason.BelowDeductible, claim.RejectionReason);
        Assert.Equal(Money.Zero, claim.EligiblePayout);
    }

    [Fact]
    public void Payout_ClaimedBelowDeductible_IsRejectedBelowDeductible()
    {
        var claim = Fixtures.Submitted(Fixtures.ActivePolicy(deductible: 500m), claimed: 499.99m);
        Assert.Equal(RejectionReason.BelowDeductible, claim.RejectionReason);
    }

    [Fact]
    public void Payout_RemainingLimitZero_IsRejectedLimitExhausted()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 10_000m, deductible: 0m);
        var context = new ClaimSubmissionContext(Money.Euro(10_000m), 0);

        var claim = Fixtures.Submitted(policy, claimed: 100m, context: context);

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal(RejectionReason.LimitExhausted, claim.RejectionReason);
    }

    [Fact]
    public void Payout_ZeroDeductible_PaysWholeClaimedAmount()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 10_000m, deductible: 0m);

        var claim = Fixtures.Submitted(policy, claimed: 1_234.56m);

        Assert.Equal(Money.Euro(1_234.56m), claim.EligiblePayout);
    }

    [Fact]
    public void Payout_ZeroClaimedAmount_IsValidationError()
    {
        var ex = Assert.Throws<ValidationException>(() => Fixtures.Submitted(claimed: 0m));
        Assert.Equal("validation_error", ex.Code);
        Assert.Contains("claimedAmount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Payout_NegativeClaimedAmount_IsRejectedByMoney()
    {
        Assert.Throws<ValidationException>(() => Fixtures.Submitted(claimed: -1m));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Submit_BlankDescription_IsValidationError(string? description)
    {
        var ex = Assert.Throws<ValidationException>(() => Fixtures.Submitted(description: description!));
        Assert.Contains("description", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Submit_DescriptionOver2000Chars_IsValidationError()
    {
        var ex = Assert.Throws<ValidationException>(() => Fixtures.Submitted(description: new string('x', 2_001)));
        Assert.Contains("2000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Submit_DescriptionOfExactly2000Chars_IsAccepted_AndTrimmed()
    {
        var claim = Fixtures.Submitted(description: " " + new string('x', 1_998) + " ");
        Assert.Equal(1_998, claim.Description.Length);
    }
}
