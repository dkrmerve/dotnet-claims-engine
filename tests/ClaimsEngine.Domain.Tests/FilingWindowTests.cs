using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Domain.Tests.Support;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Rule 2: claims filed more than 30 days after the incident (00:00 UTC) are created but auto-rejected as LateFiling.</summary>
public sealed class FilingWindowTests
{
    private static readonly DateOnly Incident = new(2026, 2, 13);
    private static readonly DateTimeOffset ThirtyDaysLater = new(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FilingWindow_Exactly30Days_IsAccepted()
    {
        var claim = Fixtures.Submitted(incidentDate: Incident, filedAt: ThirtyDaysLater);

        Assert.Equal(ClaimStatus.Submitted, claim.Status);
        Assert.Null(claim.RejectionReason);
    }

    [Fact]
    public void FilingWindow_30DaysPlusOneSecond_IsLateFiling()
    {
        var claim = Fixtures.Submitted(incidentDate: Incident, filedAt: ThirtyDaysLater.AddSeconds(1));

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal(RejectionReason.LateFiling, claim.RejectionReason);
        Assert.True(claim.Status.IsTerminal());
    }

    [Fact]
    public void FilingWindow_LateClaim_KeepsAuditTrailWithSystemActor()
    {
        var claim = Fixtures.Submitted(incidentDate: Incident, filedAt: ThirtyDaysLater.AddDays(10));

        Assert.Equal(2, claim.History.Count);
        Assert.Equal(ClaimStatus.Submitted, claim.History[0].ToStatus);
        Assert.Equal(ActorRole.System, claim.History[1].ActorRole);
        Assert.Equal(ClaimStatus.Rejected, claim.History[1].ToStatus);
        Assert.Contains("filing window is 30 days", claim.History[1].Note, StringComparison.Ordinal);
        Assert.Equal(Money.Zero, claim.EligiblePayout);
    }

    [Fact]
    public void FilingWindow_LateHighValueClaim_StillCarriesInvestigationFlag()
    {
        var policy = Fixtures.ActivePolicy(coverageLimit: 10_000m);

        var claim = Fixtures.Submitted(policy, claimed: 9_000m, incidentDate: Incident, filedAt: ThirtyDaysLater.AddDays(1));

        Assert.Equal(ClaimStatus.Rejected, claim.Status);
        Assert.Equal(ClaimFlag.RequiresInvestigation, claim.Flag);
    }

    [Fact]
    public void FilingWindow_IncidentDateInTheFuture_IsValidationError()
    {
        var ex = Assert.Throws<ValidationException>(() => Fixtures.Submitted(incidentDate: FakeClock.Today.AddDays(1)));

        Assert.Equal("validation_error", ex.Code);
        Assert.Contains("future", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilingWindow_IncidentToday_IsAccepted()
    {
        var claim = Fixtures.Submitted(incidentDate: FakeClock.Today);
        Assert.Equal(ClaimStatus.Submitted, claim.Status);
    }

    [Fact]
    public void FilingWindow_IsConfigurableThroughRules()
    {
        var rules = new ClaimRules(7, 3, 365, 0.8m, 14, Money.Euro(5_000m), Money.Euro(50_000m));

        var claim = Fixtures.Submitted(incidentDate: FakeClock.Today.AddDays(-8), rules: rules);

        Assert.Equal(RejectionReason.LateFiling, claim.RejectionReason);
    }
}
