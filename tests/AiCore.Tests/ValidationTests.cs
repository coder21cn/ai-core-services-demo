using System.Net;
using AiCore.Tests.Infrastructure;

namespace AiCore.Tests;

/// <summary>FR-01 / FR-07: invalid input yields a structured 4xx and never reaches the provider.</summary>
public sealed class ValidationTests : IDisposable
{
    private readonly RecordingProvider _provider = new();
    private readonly TestApi _api;

    public ValidationTests() => _api = new TestApi(
        new() { ["AiCore:Limits:MaxPromptChars"] = "100", ["AiCore:Limits:MaxOutputTokens"] = "50" },
        _provider);

    public static TheoryData<string, string, string?> InvalidBodies => new()
    {
        // body, expected code, expected field in "errors" (null if not a field error)
        { """{"prompt":"hi"}""", "invalid_request", "model" },
        { """{"model":"test-model","prompt":"   "}""", "invalid_request", "prompt" },
        { """{"model":"test-model"}""", "invalid_request", "prompt" },
        { $$"""{"model":"test-model","prompt":"{{new string('x', 101)}}"}""", "invalid_request", "prompt" },
        { """{"model":"test-model","prompt":"hi","maxOutputTokens":0}""", "invalid_request", "maxOutputTokens" },
        { """{"model":"test-model","prompt":"hi","maxOutputTokens":51}""", "invalid_request", "maxOutputTokens" },
        { """{"model":"test-model","promt":"typo"}""", "invalid_request", null },
        { """{"MODEL":"test-model","PROMPT":"hi"}""", "invalid_request", null },
        { """{"model":"test-model","prompt":"hi","prompt":"second"}""", "invalid_request", null },
        { """{"model":"test-model","prompt":"hi","maxOutputTokens":"5"}""", "invalid_request", null },
        { """{"model":"test-model","prompt":42}""", "invalid_request", null },
        { """{ not json""", "invalid_request", null },
        { "null", "invalid_request", null },
        { """{"model":"no-such-alias","prompt":"hi"}""", "unknown_model", null },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task InvalidRequest_Returns400ProblemDetails_WithoutProviderCall(string json, string code, string? field)
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostRaw(client, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await TestApi.ReadJson(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        if (field is not null)
        {
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), $"Expected an error for '{field}'");
        }

        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task PromptAtExactLimit_IsAccepted()
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, new string('x', 100));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task OmittedMaxOutputTokens_DefaultIsCappedAtConfiguredMaximum()
    {
        // This fixture sets MaxOutputTokens = 50 but leaves DefaultOutputTokens at its default of 256.
        using var client = _api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "hi");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, Assert.Single(_provider.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task NonJsonContentType_Returns415()
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostRaw(client, "model=x&prompt=y", "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_media_type", (await TestApi.ReadJson(response)).GetProperty("code").GetString());
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task InvalidValuesAreNotEchoedInErrors()
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);
        const string marker = "SENSITIVE-MARKER-1234";

        using var response = await TestApi.PostRaw(client, $$$"""{"model":"test-model","prompt":{"nested":"{{{marker}}}"}}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(marker, await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _api.Dispose();
}
