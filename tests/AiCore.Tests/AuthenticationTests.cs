using System.Net;
using AiCore.Tests.Infrastructure;

namespace AiCore.Tests;

/// <summary>T-02 and FR-03: credentials are required, and the tenant comes only from the credential.</summary>
public sealed class AuthenticationTests : IDisposable
{
    private readonly RecordingProvider _provider = new();
    private readonly TestApi _api;

    public AuthenticationTests() => _api = new TestApi(extraProvider: _provider);

    [Theory]
    [InlineData(null, "must be supplied")]
    [InlineData("", "must be supplied")]
    [InlineData("not-a-real-key", "not valid")]
    [InlineData("TEST-ONLY-KEY-A", "not valid")] // keys are case-sensitive
    public async Task T02_MissingOrInvalidCredentials_Return401_AndNeverInvokeProvider(string? apiKey, string expectedDetail)
    {
        using var client = _api.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
        }

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "hello");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("ApiKey", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        var body = await TestApi.ReadJson(response);
        Assert.Equal("unauthenticated", body.GetProperty("code").GetString());
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Contains(expectedDetail, body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task T02_Unauthenticated_IsRejectedBeforeBodyIsParsed()
    {
        using var client = _api.CreateClient();

        using var response = await TestApi.PostRaw(client, "{ this is not json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task FR03_TenantIsDerivedFromCredential_NotFromClientHeaders()
    {
        using var clientA = _api.CreateClientFor(TestApi.KeyA);
        clientA.DefaultRequestHeaders.Add("X-Tenant-Id", TestApi.TenantB); // spoofing attempt
        using var clientB = _api.CreateClientFor(TestApi.KeyB);

        Assert.Equal(HttpStatusCode.OK, (await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestApi.PostGenerate(clientB, TestApi.TestAlias, "b")).StatusCode);

        var requests = _provider.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(TestApi.TenantA, requests.Single(r => r.Prompt == "a").TenantId);
        Assert.Equal(TestApi.TenantB, requests.Single(r => r.Prompt == "b").TenantId);
    }

    [Fact]
    public async Task FR03_TenantFieldInBody_IsRejected_NotTrusted()
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostRaw(
            client, $$"""{"model":"{{TestApi.TestAlias}}","prompt":"x","tenantId":"{{TestApi.TenantB}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await TestApi.ReadJson(response);
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
        Assert.Equal("The request body is not valid JSON for this endpoint.", body.GetProperty("detail").GetString());
        Assert.Empty(_provider.Requests);
    }

    [Fact]
    public async Task HealthLive_IsAnonymous_AndIndependentOfProviders()
    {
        using var client = _api.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
        Assert.Empty(_provider.Requests);
    }

    public void Dispose() => _api.Dispose();
}
