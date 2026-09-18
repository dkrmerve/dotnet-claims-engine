using System.Net;
using ClaimsEngine.Api.Auth;
using ClaimsEngine.Api.Contracts;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Api.Validation;
using ClaimsEngine.Domain.Shared;
using ClaimsEngine.Infrastructure;
using ClaimsEngine.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClaimsEngine.Api.Tests;

/// <summary>Small components exercised directly: validators at their edges, enum/date helpers, converters, clocks, the design-time factory.</summary>
public sealed class ComponentTests
{
    [Fact]
    public void CreatePolicyValidator_ReportsLongNumber_EmptyGuid_NegativeDeductible_MissingFrom()
    {
        var request = new CreatePolicyRequest(new string('p', 65), Guid.Empty, "Auto", 1000m, -1m, null, new DateOnly(2026, 1, 1));

        var errors = new CreatePolicyRequestValidator().Validate(request).ToDictionary();

        Assert.Contains("at most 64", errors["policyNumber"][0], StringComparison.Ordinal);
        Assert.Contains("non-empty GUID", errors["holderId"][0], StringComparison.Ordinal);
        Assert.Contains("negative", errors["deductible"][0], StringComparison.Ordinal);
        Assert.Contains("effectiveFrom", errors.Keys);
        Assert.DoesNotContain("effectiveTo", errors.Keys);
    }

    [Fact]
    public void CreatePolicyValidator_ValidRequest_HasNoErrors_AndMapsToCommand()
    {
        var request = new CreatePolicyRequest("POL-1", Guid.NewGuid(), "health", 1000m, 10m, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.False(new CreatePolicyRequestValidator().Validate(request).Any);
        Assert.Equal(Domain.Policies.CoverageType.Health, request.ToCommand().CoverageType);
    }

    [Fact]
    public void SubmitValidator_EmptyGuidPolicy_IsReported()
    {
        var errors = new SubmitClaimRequestValidator().Validate(new SubmitClaimRequest(Guid.Empty, "2026-03-01", 10m, "x")).ToDictionary();
        Assert.Equal(["policyId"], errors.Keys);
    }

    [Fact]
    public void DevTokenValidator_SubjectOver128Chars_IsReported()
    {
        var errors = new DevTokenRequestValidator().Validate(new DevTokenRequest(new string('s', 129), "Manager")).ToDictionary();
        Assert.Contains("at most 128", errors["subject"][0], StringComparison.Ordinal);
    }

    [Fact]
    public void ListClaimsValidator_ValidQuery_HasNoErrors_AndAppliesDefaults()
    {
        var request = new ListClaimsRequest(null, "paid", null, null);
        Assert.False(new ListClaimsRequestValidator().Validate(request).Any);

        var query = request.ToQuery(new Actor("m", ActorRole.Manager));
        Assert.Equal(1, query.Page);
        Assert.Equal(20, query.PageSize);
        Assert.Equal(Domain.Claims.ClaimStatus.Paid, query.Status);
    }

    [Fact]
    public void ValidationErrors_AccumulatesMultipleMessagesPerField()
    {
        var errors = new ValidationErrors().Add("f", "one").Add("f", "two").Add("g", "three");

        var dictionary = errors.ToDictionary();
        Assert.Equal(["one", "two"], dictionary["f"]);
        Assert.Equal(["three"], dictionary["g"]);
        Assert.True(errors.Any);
        Assert.False(new ValidationErrors().Any);
    }

    [Theory]
    [InlineData("Auto", true)]
    [InlineData("auto", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EnumNames_MatchesNamesOnly(string? value, bool expected)
    {
        Assert.Equal(expected, EnumNames.IsValid<Domain.Policies.CoverageType>(value));
        Assert.Equal("Auto, Home, Health", EnumNames.Allowed<Domain.Policies.CoverageType>());
    }

    [Theory]
    [InlineData("2026-03-10", "2026-03-10")]
    [InlineData(" 2026-03-10 ", "2026-03-10")]
    [InlineData("2026-03-11T01:30:00+03:00", "2026-03-10")]
    [InlineData("2026-03-10T23:59:59.999Z", "2026-03-10")]
    [InlineData("2026-03-10T23:30-02:00", "2026-03-11")]
    [InlineData("2026-03-10T12:00:00", "2026-03-10")]
    public void IncidentDates_ParsesIsoFormsToUtcDay(string text, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), IncidentDates.Parse(text));
    }

    [Theory]
    [InlineData("10/03/2026")]
    [InlineData("March 10, 2026")]
    [InlineData("2026-3-10")]
    [InlineData("")]
    [InlineData(null)]
    public void IncidentDates_RejectsNonIsoForms(string? text)
    {
        Assert.False(IncidentDates.TryParse(text, out _));
        Assert.Throws<FormatException>(() => IncidentDates.Parse(text!));
    }

    [Theory]
    [InlineData("Manager", ActorRole.Manager)]
    [InlineData(" senioradjuster ", ActorRole.SeniorAdjuster)]
    [InlineData("System", null)]
    [InlineData("Admin", null)]
    [InlineData(null, null)]
    public void Roles_TryParse_AcceptsBusinessRolesOnly(string? value, ActorRole? expected)
    {
        Assert.Equal(expected, Roles.TryParse(value));
    }

    [Fact]
    public void Authentication_Resolve_RequiresSubAndKnownRole()
    {
        var noSub = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("role", "Manager")], "test"));
        var noRole = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("sub", "alice"), new("role", "Admin")], "test"));
        var ok = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new("sub", "alice"), new("role", "Admin"), new("role", "Adjuster")], "test"));

        Assert.Throws<Domain.Exceptions.UnauthenticatedException>(() => Authentication.Resolve(noSub));
        Assert.Throws<Domain.Exceptions.UnauthenticatedException>(() => Authentication.Resolve(noRole));
        Assert.Equal(new Actor("alice", ActorRole.Adjuster), Authentication.Resolve(ok));
    }

    [Fact]
    public async Task DevTokens_ProduceValidatableHs256Tokens()
    {
        var now = DateTimeOffset.UtcNow;
        var token = DevTokens.Create(Tokens.Issuer, "holder-1", ActorRole.Claimant, now, TimeSpan.FromMinutes(10));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Tokens.Issuer.Issuer,
            ValidAudience = Tokens.Issuer.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(ClaimsApiFactory.SigningKey)),
        });

        Assert.True(result.IsValid);
        Assert.Equal("holder-1", result.ClaimsIdentity.FindFirst("sub")!.Value);
        Assert.Equal("Claimant", result.ClaimsIdentity.FindFirst("role")!.Value);
    }

    [Fact]
    public void UtcTicksConverter_RoundTripsInstantsAsUtc()
    {
        var converter = new UtcTicksConverter();
        var instant = new DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.FromHours(3));

        var ticks = (long)converter.ConvertToProvider(instant)!;
        var back = (DateTimeOffset)converter.ConvertFromProvider(ticks)!;

        Assert.Equal(instant.UtcTicks, ticks);
        Assert.Equal(instant, back);
        Assert.Equal(TimeSpan.Zero, back.Offset);
    }

    [Fact]
    public void SystemClock_ReturnsUtcNow()
    {
        var now = new SystemClock().UtcNow;
        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.InRange(now, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void DesignTimeFactory_BuildsAnNpgsqlContextWithoutConnecting()
    {
        using var db = new ClaimsDbContextDesignTimeFactory().CreateDbContext([]);
        Assert.True(db.Database.IsNpgsql());
    }
}

/// <summary>The Development environment profile: human-readable logging and appsettings.Development.json with the dev issuer on.</summary>
[Collection(ApiCollection.Name)]
public sealed class DevelopmentEnvironmentTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    protected override string Environment => "Development";

    [Fact]
    public async Task DevelopmentProfile_BootsAndIssuesTokens()
    {
        Clock.UtcNow = DateTimeOffset.UtcNow;
        var response = await Anonymous.PostJsonAsync("/auth/token", new { subject = "dev", role = "Manager" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
