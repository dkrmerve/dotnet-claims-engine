using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Policies;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Policy aggregate: creation invariants, policy-year windows, status changes, payout bookkeeping.</summary>
public sealed class PolicyTests
{
    [Fact]
    public void Policy_Create_SetsActiveStatusAndVersionOne()
    {
        var policy = Fixtures.ActivePolicy();

        Assert.Equal(PolicyStatus.Active, policy.Status);
        Assert.Equal(1, policy.Version);
        Assert.Equal(Money.Zero, policy.LifetimePaid);
        Assert.Equal(new DateRange(Fixtures.PolicyStart, Fixtures.PolicyEnd), policy.CoveragePeriod);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Policy_Create_BlankPolicyNumber_IsRejected(string number)
    {
        Assert.Throws<ValidationException>(() => Policy.Create(
            PolicyId.New(), number, HolderId.New(), CoverageType.Home, Money.Euro(1000m), Money.Euro(0m), Fixtures.PolicyStart, Fixtures.PolicyEnd));
    }

    [Fact]
    public void Policy_Create_ZeroCoverageLimit_IsRejected()
    {
        Assert.Throws<ValidationException>(() => Fixtures.ActivePolicy(coverageLimit: 0m, deductible: 0m));
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1000, 1000.01)]
    public void Policy_Create_DeductibleAtOrAboveCoverageLimit_IsRejected(decimal limit, decimal deductible)
    {
        var ex = Assert.Throws<ValidationException>(() => Fixtures.ActivePolicy(coverageLimit: limit, deductible: deductible));
        Assert.Equal("validation_error", ex.Code);
    }

    [Fact]
    public void Policy_Create_ZeroDeductible_IsAllowed()
    {
        Assert.Equal(Money.Zero, Fixtures.ActivePolicy(deductible: 0m).Deductible);
    }

    [Theory]
    [InlineData("2026-01-01", "2026-01-01")]
    [InlineData("2026-01-02", "2026-01-01")]
    public void Policy_Create_EffectiveToNotAfterFrom_IsRejected(string from, string to)
    {
        Assert.Throws<ValidationException>(() => Fixtures.ActivePolicy(from: DateOnly.Parse(from), to: DateOnly.Parse(to)));
    }

    [Fact]
    public void Policy_Create_TrimsPolicyNumber()
    {
        var policy = Policy.Create(PolicyId.New(), "  POL-1  ", HolderId.New(), CoverageType.Health, Money.Euro(10m), Money.Euro(1m), Fixtures.PolicyStart, Fixtures.PolicyEnd);
        Assert.Equal("POL-1", policy.PolicyNumber);
    }

    [Theory]
    [InlineData("2026-03-01", "2028-02-29", "2026-06-01", "2026-03-01", "2027-02-28")]
    [InlineData("2026-03-01", "2028-02-29", "2027-03-01", "2027-03-01", "2028-02-29")]
    [InlineData("2026-03-01", "2028-02-29", "2027-02-28", "2026-03-01", "2027-02-28")]
    [InlineData("2026-01-01", "2026-06-30", "2026-02-01", "2026-01-01", "2026-06-30")]
    [InlineData("2024-02-29", "2027-02-27", "2025-02-27", "2024-02-29", "2025-02-27")]
    [InlineData("2024-02-29", "2027-02-27", "2025-02-28", "2025-02-28", "2026-02-27")]
    public void PolicyYear_IsAnchoredAtEffectiveFrom_NotCalendarYear(string from, string to, string date, string expectedStart, string expectedEnd)
    {
        var policy = Fixtures.ActivePolicy(from: DateOnly.Parse(from), to: DateOnly.Parse(to));

        var year = policy.PolicyYearContaining(DateOnly.Parse(date));

        Assert.Equal(new DateRange(DateOnly.Parse(expectedStart), DateOnly.Parse(expectedEnd)), year);
    }

    [Fact]
    public void PolicyYear_DateOutsideCoverage_IsRejected()
    {
        var policy = Fixtures.ActivePolicy();
        Assert.Throws<ValidationException>(() => policy.PolicyYearContaining(Fixtures.PolicyEnd.AddDays(1)));
    }

    [Fact]
    public void Policy_Lapse_MovesActiveToLapsed_AndBumpsVersion()
    {
        var policy = Fixtures.ActivePolicy();
        policy.Lapse();

        Assert.Equal(PolicyStatus.Lapsed, policy.Status);
        Assert.Equal(2, policy.Version);
        Assert.Throws<InvalidTransitionException>(policy.Lapse);
    }

    [Fact]
    public void Policy_Cancel_FromAnyNonCancelledStatus_ThenRejectsSecondCancel()
    {
        var policy = Fixtures.ActivePolicy();
        policy.Lapse();
        policy.Cancel();

        Assert.Equal(PolicyStatus.Cancelled, policy.Status);
        var ex = Assert.Throws<InvalidTransitionException>(policy.Cancel);
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public void Policy_RecordPayout_AccumulatesLifetimePaid_AndBumpsVersion()
    {
        var policy = Fixtures.ActivePolicy();

        policy.RecordPayout(Money.Euro(100m));
        policy.RecordPayout(Money.Euro(50.5m));

        Assert.Equal(Money.Euro(150.5m), policy.LifetimePaid);
        Assert.Equal(3, policy.Version);
    }

    [Fact]
    public void DateRange_Contains_IsInclusive()
    {
        var range = new DateRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.True(range.Contains(range.Start));
        Assert.True(range.Contains(range.End));
        Assert.False(range.Contains(range.Start.AddDays(-1)));
        Assert.False(range.Contains(range.End.AddDays(1)));
    }

    [Fact]
    public void StronglyTypedIds_NewAndToString()
    {
        var policyId = PolicyId.New();
        var claimId = ClaimId.New();
        var holderId = HolderId.New();

        Assert.NotEqual(Guid.Empty, policyId.Value);
        Assert.Equal(policyId.Value.ToString(), policyId.ToString());
        Assert.Equal(claimId.Value.ToString(), claimId.ToString());
        Assert.Equal(holderId.Value.ToString(), holderId.ToString());
        Assert.NotEqual(PolicyId.New(), PolicyId.New());
    }

    [Fact]
    public void Actor_Owns_ComparesSubjectWithHolderIdCaseInsensitively()
    {
        var holder = HolderId.New();

        Assert.True(new Actor(holder.Value.ToString().ToUpperInvariant(), ActorRole.Claimant).Owns(holder));
        Assert.False(new Actor("someone-else", ActorRole.Claimant).Owns(holder));
    }

    [Theory]
    [InlineData(ActorRole.Adjuster, true)]
    [InlineData(ActorRole.SeniorAdjuster, true)]
    [InlineData(ActorRole.Manager, true)]
    [InlineData(ActorRole.Claimant, false)]
    [InlineData(ActorRole.System, false)]
    public void ActorRole_IsAdjusterOrAbove(ActorRole role, bool expected)
    {
        Assert.Equal(expected, role.IsAdjusterOrAbove());
    }
}
