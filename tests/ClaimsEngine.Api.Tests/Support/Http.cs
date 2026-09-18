using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimsEngine.Api.Auth;
using ClaimsEngine.Api.Options;
using ClaimsEngine.Domain.Shared;

namespace ClaimsEngine.Api.Tests.Support;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>The RFC 7807 body as the API writes it, including the project's extensions.</summary>
internal sealed record ProblemResponse(
    int? Status,
    string? Title,
    string? Detail,
    string? Type,
    string? Instance,
    string? Code,
    string? CorrelationId,
    string? TraceId,
    Dictionary<string, string[]>? Errors);

internal static class Tokens
{
    public static readonly DevIssuerOptions Issuer = new() { Enabled = true, SigningKey = ClaimsApiFactory.SigningKey };

    /// <summary>Tokens are validated against wall-clock time by the JWT middleware, so they are minted with the real "now".</summary>
    public static string For(ActorRole role, string subject, TimeSpan? lifetime = null, string? signingKey = null, DateTimeOffset? issuedAt = null) =>
        DevTokens.Create(Issuer, subject, role, issuedAt ?? DateTimeOffset.UtcNow, lifetime, signingKey);
}

internal static class HttpExtensions
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static HttpClient WithBearer(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object? body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: Json.Options),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PostRawAsync(this HttpClient client, string path, string body, string mediaType = "application/json") =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) });

    public static Task<HttpResponseMessage> PostEmptyAsync(this HttpClient client, string path) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path));

    public static async Task<T> ReadAsAsync<T>(this HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(body, Json.Options) ?? throw new InvalidOperationException($"Empty body for {response.RequestMessage?.RequestUri}: {response.StatusCode}");
    }

    public static async Task<T> ReadOkAsync<T>(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        if (response.StatusCode != expected)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected {(int)expected} but got {(int)response.StatusCode} for {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {body}");
        }

        return await response.ReadAsAsync<T>();
    }

    /// <summary>Asserts status, stable code, non-empty detail, catalog type URL and the correlation id extension.</summary>
    public static async Task<ProblemResponse> AssertProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status} {code} but got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = JsonSerializer.Deserialize<ProblemResponse>(body, Json.Options)!;
        Assert.Equal((int)status, problem.Status);
        Assert.Equal(code, problem.Code);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail), "detail must not be empty");
        Assert.Equal("https://github.com/dkrmerve/dotnet-claims-engine/docs/errors#" + code, problem.Type);
        Assert.False(string.IsNullOrWhiteSpace(problem.CorrelationId), "correlationId must be present");
        Assert.True(response.Headers.Contains("X-Correlation-Id"), "X-Correlation-Id header must be echoed");
        return problem;
    }
}
