using System.Net;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AiCore.Tests;

/// <summary>
/// T-10: requests travel the full pipeline (auth -> endpoint -> gate -> provider) in the test host,
/// and each failure class maps to a distinct, safe status/code.
/// </summary>
public sealed class EndToEndErrorMappingTests
{
    private const string SensitivePrompt = "PROMPT-MARKER-c0ffee";

    [Theory]
    [InlineData("sim-transient-failure", HttpStatusCode.ServiceUnavailable, "provider_unavailable", true)]
    [InlineData("sim-permanent-failure", HttpStatusCode.BadGateway, "provider_error", false)]
    public async Task T10_MockProviderFailures_MapToDistinctSafeErrors(
        string alias, HttpStatusCode status, string code, bool hasRetryAfter)
    {
        using var api = new TestApi();
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, alias, SensitivePrompt);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(hasRetryAfter, response.Headers.Contains("Retry-After"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(SensitivePrompt, raw);
        Assert.DoesNotContain("simulated", raw, StringComparison.OrdinalIgnoreCase); // provider message not forwarded

        var body = await TestApi.ReadJson(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(
            Assert.Single(response.Headers.GetValues("X-Correlation-Id")),
            body.GetProperty("correlationId").GetString());
        Assert.DoesNotContain(api.Logs.Entries, e => e.Contains(SensitivePrompt));
    }

    [Fact]
    public async Task T10_UnexpectedProviderException_Returns500_WithoutLeakingExceptionDetailsToCallerOrLogs()
    {
        const string secret = "SECRET-connection-string-xyz";
        using var api = new TestApi(extraProvider: new ThrowingProvider(
            () => new InvalidOperationException($"{secret} while sending {SensitivePrompt}")));
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, SensitivePrompt);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, raw);
        Assert.DoesNotContain(SensitivePrompt, raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
        var body = await TestApi.ReadJson(response);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        var correlationId = body.GetProperty("correlationId").GetString()!;
        Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-Id")), correlationId);

        // The exception message (here carrying a secret and the prompt, as an SDK exception might) reaches
        // no log at all, while the failure stays diagnosable from its type and correlation ID.
        Assert.DoesNotContain(api.Logs.Entries, e => e.Contains(secret) || e.Contains(SensitivePrompt));
        Assert.Contains(api.Logs.Entries, e =>
            e.Level == LogLevel.Error && e.Contains(correlationId) && e.Contains(nameof(InvalidOperationException)));
    }

    [Fact]
    public async Task T10_AliasRoutedToUnregisteredProvider_IsAnInternalError_NotACallerError()
    {
        using var api = new TestApi(new()
        {
            ["AiCore:Models:misrouted:Provider"] = "does-not-exist",
            ["AiCore:Models:misrouted:ProviderModel"] = "x",
        });
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, "misrouted", "hi");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", (await TestApi.ReadJson(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task T10_ProviderSwap_SameEndpointServesASecondAdapter()
    {
        var provider = new RecordingProvider();
        using var api = new TestApi(extraProvider: provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var viaMock = await TestApi.ReadJson(await TestApi.PostGenerate(client, "chat-default", "hi"));
        var viaTest = await TestApi.ReadJson(await TestApi.PostGenerate(client, TestApi.TestAlias, "hi"));

        Assert.Equal("mock", viaMock.GetProperty("provider").GetString());
        Assert.Equal("test", viaTest.GetProperty("provider").GetString());
        Assert.Equal("test-model-v1", Assert.Single(provider.Requests).ProviderModel);
    }

    [Theory]
    [InlineData("GET", "/v1/generate", HttpStatusCode.MethodNotAllowed, "method_not_allowed")]
    [InlineData("GET", "/v1/does-not-exist", HttpStatusCode.NotFound, "not_found")]
    public async Task UnknownRoutesAndMethods_UseTheSameProblemContract(
        string method, string path, HttpStatusCode status, string code)
    {
        using var api = new TestApi();
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await TestApi.ReadJson(response)).GetProperty("code").GetString());
    }
}
