using ClaimsEngine.Domain.Claims;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Domain.Tests;

/// <summary>Every exception type carries a stable code and kind; ClaimRules rejects nonsensical configuration at construction.</summary>
public sealed class ExceptionsAndRulesTests
{
    public static TheoryData<DomainException, string, ErrorKind> Exceptions() => new()
    {
        { new ValidationException("v"), "validation_error", ErrorKind.Validation },
        { new ValidationException("v", "note_required"), "note_required", ErrorKind.Validation },
        { new NotFoundException("Claim", Guid.Empty), "not_found", ErrorKind.NotFound },
        { new InvalidTransitionException("t"), "invalid_transition", ErrorKind.Conflict },
        { new InvalidTransitionException("t", "investigation_pending"), "investigation_pending", ErrorKind.Conflict },
        { new ConcurrencyException(), "concurrency_conflict", ErrorKind.Conflict },
        { new ConcurrencyException(new InvalidOperationException("inner")), "concurrency_conflict", ErrorKind.Conflict },
        { new DuplicateKeyException("dup"), "duplicate_key", ErrorKind.Conflict },
        { new RuleViolationException("policy_not_eligible", "r"), "policy_not_eligible", ErrorKind.RuleViolation },
        { new InsufficientAuthorityException("a"), "insufficient_authority", ErrorKind.Forbidden },
        { new NotOwnerException("o"), "not_owner", ErrorKind.Forbidden },
        { new UnauthenticatedException("u"), "unauthenticated", ErrorKind.Unauthenticated },
    };

    [Theory]
    [MemberData(nameof(Exceptions))]
    public void Exception_CarriesStableCodeAndKind(DomainException exception, string code, ErrorKind kind)
    {
        Assert.Equal(code, exception.Code);
        Assert.Equal(kind, exception.Kind);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        Assert.Matches("^[a-z_]+$", exception.Code);
    }

    [Fact]
    public void Exception_NotFound_MentionsResourceAndId()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"Claim '{id}' was not found.", new NotFoundException("Claim", id).Message);
    }

    [Fact]
    public void Exception_InnerExceptionIsPreserved()
    {
        var inner = new InvalidOperationException("db");
        Assert.Same(inner, new ConcurrencyException(inner).InnerException);
        Assert.Same(inner, new DuplicateKeyException("dup", inner).InnerException);
    }

    [Fact]
    public void ClaimRules_Default_MatchesTheBusinessBrief()
    {
        var rules = ClaimRules.Default;

        Assert.Equal(30, rules.FilingWindowDays);
        Assert.Equal(3, rules.FrequentClaimantThreshold);
        Assert.Equal(365, rules.FrequentClaimantWindowDays);
        Assert.Equal(0.80m, rules.HighValueShareOfLimit);
        Assert.Equal(14, rules.ReviewSlaDays);
        Assert.Equal(Money.Euro(5_000m), rules.AdjusterApprovalLimit);
        Assert.Equal(Money.Euro(50_000m), rules.SeniorAdjusterApprovalLimit);
        Assert.Equal(TimeSpan.FromDays(30), rules.FilingWindow);
        Assert.Equal(TimeSpan.FromDays(365), rules.FrequentClaimantWindow);
    }

    [Theory]
    [InlineData(0, 3, 365, 0.8, 14, 5000, 50000)]
    [InlineData(30, 0, 365, 0.8, 14, 5000, 50000)]
    [InlineData(30, 3, 0, 0.8, 14, 5000, 50000)]
    [InlineData(30, 3, 365, 0, 14, 5000, 50000)]
    [InlineData(30, 3, 365, 1.01, 14, 5000, 50000)]
    [InlineData(30, 3, 365, 0.8, 0, 5000, 50000)]
    [InlineData(30, 3, 365, 0.8, 14, 50000, 5000)]
    public void ClaimRules_InvalidConfiguration_IsRejected(int window, int threshold, int windowDays, decimal share, int sla, decimal adjuster, decimal senior)
    {
        Assert.Throws<ValidationException>(() => new ClaimRules(window, threshold, windowDays, share, sla, Money.Euro(adjuster), Money.Euro(senior)));
    }

    [Fact]
    public void ClaimRules_ShareOfExactlyOne_IsAllowed()
    {
        var rules = new ClaimRules(30, 3, 365, 1m, 14, Money.Euro(5_000m), Money.Euro(5_000m));
        Assert.Equal(1m, rules.HighValueShareOfLimit);
    }

    [Fact]
    public void SubmissionContext_Clean_HasNothingPaidAndNoOtherClaims()
    {
        Assert.Equal(Money.Zero, ClaimSubmissionContext.Clean.AlreadyPaidInPolicyYear);
        Assert.Equal(0, ClaimSubmissionContext.Clean.OtherClaimsByHolderInWindow);
    }
}
