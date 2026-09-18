using System.Net;
using ClaimsEngine.Api.Errors;
using ClaimsEngine.Api.Tests.Support;
using ClaimsEngine.Domain.Exceptions;
using ClaimsEngine.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsEngine.Api.Tests;

/// <summary>Rate limiting on writes: the third request in a 2-permit window is a ProblemDetails 429 with Retry-After.</summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitingTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["RateLimiting:PermitLimit"] = "2",
        ["RateLimiting:WindowSeconds"] = "60",
    };

    [Fact]
    public async Task RateLimit_ThirdWriteInWindow_Is429RateLimited_ReadsUnaffected()
    {
        var policy = await CreatePolicyAsync();
        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(ClaimantFor(policy), policy.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SubmitAsync(ClaimantFor(policy), policy.Id)).StatusCode);

        var limited = await SubmitAsync(ClaimantFor(policy), policy.Id);

        await limited.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(HttpStatusCode.OK, (await Manager.GetAsync("/claims")).StatusCode);
    }
}

/// <summary>Unexpected exceptions become a generic 500 with a correlation id and no internals, and are logged at error level with that id.</summary>
[Collection(ApiCollection.Name)]
public sealed class UnexpectedErrorTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    private readonly FailingCommandInterceptor _interceptor = new();
    private readonly CapturingLoggerProvider _logs = new();

    protected override Action<DbContextOptionsBuilder> ConfigureDatabase => options => options.AddInterceptors(_interceptor);

    protected override Action<IServiceCollection> ConfigureServices => services => services.AddLogging(logging => logging.AddProvider(_logs));

    [Fact]
    public async Task UnexpectedException_Is500InternalError_WithoutStackTrace_AndLoggedWithCorrelationId()
    {
        var policy = await CreatePolicyAsync();
        _interceptor.Armed = true;
        _interceptor.FailWhen = text => text.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && text.Contains("claims", StringComparison.OrdinalIgnoreCase);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/claims?policyId={policy.Id}");
        request.Headers.Add("X-Correlation-Id", "corr-500");
        var response = await Manager.SendAsync(request);

        var problem = await response.AssertProblemAsync(HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal("corr-500", problem.CorrelationId);
        Assert.DoesNotContain("Injected failure", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var entry = Assert.Single(_logs.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Unhandled InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains("correlationId=corr-500", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthReady_WhenDatabaseProbeFails_Is503_LiveStays200()
    {
        _interceptor.Armed = true;
        _interceptor.FailWhen = text => text.Contains("SELECT 1", StringComparison.Ordinal);

        var ready = await Anonymous.GetAsync("/health/ready");
        var live = await Anonymous.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("Unhealthy", await ready.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}

/// <summary>Startup validation: misconfiguration stops the process instead of surfacing during a claim.</summary>
[Collection(ApiCollection.Name)]
public sealed class StartupValidationTests(DatabaseFixture databases) : ApiTestBase(databases)
{
    [Theory]
    [InlineData("Rules:FilingWindowDays", "0")]
    [InlineData("Rules:HighValueShareOfLimit", "1.5")]
    [InlineData("Rules:SeniorAdjusterApprovalLimit", "10")]
    [InlineData("Idempotency:TtlHours", "0")]
    [InlineData("RateLimiting:PermitLimit", "0")]
    [InlineData("Auth:DevIssuer:SigningKey", "too-short")]
    [InlineData("Auth:DevIssuer:SigningKey", "")]
    [InlineData("Auth:Authority", "https://issuer.example")]
    public async Task InvalidConfiguration_FailsAtStartup(string key, string value)
    {
        await using var factory = new ClaimsApiFactory(Database, new FactoryOptions(new Dictionary<string, string?> { [key] = value }));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.True(ex is OptionsValidationException || ex.InnerException is OptionsValidationException, ex.ToString());
    }

    [Fact]
    public async Task MissingConnectionString_FailsFastWithClearMessage()
    {
        await using var factory = new ClaimsApiFactory(Database, new FactoryOptions(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "" }));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ConnectionStrings__Default", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalIssuerMode_ConfiguresWithoutDevIssuer()
    {
        await using var factory = new ClaimsApiFactory(Database, new FactoryOptions(new Dictionary<string, string?>
        {
            ["Auth:Authority"] = "https://issuer.example",
            ["Auth:Audience"] = "claims-engine",
            ["Auth:DevIssuer:Enabled"] = "false",
            ["Auth:DevIssuer:SigningKey"] = null,
        }));

        using var client = factory.CreateClient();
        await (await client.PostJsonAsync("/auth/token", new { subject = "a", role = "Manager" })).AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task DatabaseInitializer_RetriesWithBackoff_ThenGivesUp()
    {
        var options = new DbContextOptionsBuilder<ClaimsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1")
            .Options;
        await using var db = new ClaimsDbContext(options);
        var logs = new CapturingLoggerProvider();
        var logger = logs.CreateLogger("init");

        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseInitializer.InitializeAsync(db, logger, maxWait: TimeSpan.FromSeconds(2), initialDelay: TimeSpan.FromMilliseconds(200)));

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("retrying", StringComparison.Ordinal));
    }
}

/// <summary>The single exception-to-HTTP table, exercised directly.</summary>
public sealed class ErrorMapperTests
{
    public static TheoryData<Exception, int, string> Cases() => new()
    {
        { new ValidationException("v"), 400, "validation_error" },
        { new ValidationException("v", "note_required"), 400, "note_required" },
        { new UnauthenticatedException("u"), 401, "unauthenticated" },
        { new InsufficientAuthorityException("a"), 403, "insufficient_authority" },
        { new NotOwnerException("o"), 403, "not_owner" },
        { new NotFoundException("Claim", 1), 404, "not_found" },
        { new InvalidTransitionException("t"), 409, "invalid_transition" },
        { new InvalidTransitionException("t", "investigation_pending"), 409, "investigation_pending" },
        { new ConcurrencyException(), 409, "concurrency_conflict" },
        { new DuplicateKeyException("d"), 409, "duplicate_key" },
        { new RuleViolationException("policy_not_eligible", "r"), 422, "policy_not_eligible" },
        { new RuleViolationException("limit_exhausted", "r"), 422, "limit_exhausted" },
        { new BadHttpRequestException("bad json"), 400, "invalid_request" },
        { new BadHttpRequestException("too big", 413), 413, "payload_too_large" },
        { new InvalidOperationException("ef"), 500, "internal_error" },
        { new NullReferenceException(), 500, "internal_error" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Map_ProducesStatusAndCode(Exception exception, int status, string code)
    {
        var mapped = ErrorMapper.Map(exception);

        Assert.Equal(status, mapped.Status);
        Assert.Equal(code, mapped.Code);
        Assert.False(string.IsNullOrWhiteSpace(mapped.Detail));
        Assert.Equal(status == 500, mapped.IsUnexpected);
        if (status == 500)
        {
            Assert.DoesNotContain(exception.Message, mapped.Detail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(400, "invalid_request")]
    [InlineData(401, "unauthenticated")]
    [InlineData(403, "forbidden_role")]
    [InlineData(404, "not_found")]
    [InlineData(405, "method_not_allowed")]
    [InlineData(413, "payload_too_large")]
    [InlineData(415, "unsupported_media_type")]
    [InlineData(429, "rate_limited")]
    [InlineData(500, "internal_error")]
    [InlineData(503, "internal_error")]
    [InlineData(418, "http_418")]
    public void DefaultCodeFor_CoversFrameworkStatuses(int status, string code)
    {
        Assert.Equal(code, ErrorMapper.DefaultCodeFor(status));
    }

    [Fact]
    public void StatusFor_UnknownKind_Is500()
    {
        Assert.Equal(500, ErrorMapper.StatusFor((ErrorKind)99));
    }
}

/// <summary>Collects log entries so tests can assert on what was logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Category, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Entries)
            {
                owner.Entries.Add((logLevel, category, formatter(state, exception)));
            }
        }
    }
}
