using System.Net;
using AiCore.Tests.Infrastructure;

namespace AiCore.Tests;

/// <summary>T-01: successful request through the real mock provider.</summary>
public sealed class GenerateSuccessTests : IDisposable
{
    private readonly TestApi _api = new();

    [Fact]
    public async Task T01_ValidRequest_ReturnsStableContractWithCorrelationId()
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, "chat-default", "Hello");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await TestApi.ReadJson(response);
        var correlationId = body.GetProperty("correlationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(correlationId));
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues("X-Correlation-Id")));

        Assert.Equal("chat-default", body.GetProperty("model").GetString());
        Assert.Equal("mock", body.GetProperty("provider").GetString());
        Assert.Equal("mock-echo-v1", body.GetProperty("providerModel").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("output").GetString()));
        Assert.True(body.GetProperty("latencyMs").GetInt64() >= 0);

        var usage = body.GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("inputTokens").GetInt32()); // ceil(5 chars / 4)
        var outputTokens = usage.GetProperty("outputTokens").GetInt32();
        Assert.True(outputTokens > 0);
        Assert.Equal(2 + outputTokens, usage.GetProperty("totalTokens").GetInt32());
        Assert.True(usage.GetProperty("simulated").GetBoolean());

        // Exactly the documented top-level fields: the contract does not leak extra data (e.g. tenant).
        Assert.Equal(
            ["correlationId", "latencyMs", "model", "output", "provider", "providerModel", "usage"],
            body.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task T01_SameInput_IsDeterministic_WithDistinctCorrelationIds()
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);

        var first = await TestApi.ReadJson(await TestApi.PostGenerate(client, "chat-default", "Determinism check"));
        var second = await TestApi.ReadJson(await TestApi.PostGenerate(client, "CHAT-DEFAULT", "Determinism check"));

        Assert.Equal(first.GetProperty("output").GetString(), second.GetProperty("output").GetString());
        Assert.Equal(first.GetProperty("usage").ToString(), second.GetProperty("usage").ToString());
        Assert.NotEqual(first.GetProperty("correlationId").GetString(), second.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task ClientSuppliedCorrelationId_IsNotTrusted()
    {
        using var client = _api.CreateClientFor(TestApi.KeyA);
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "client-chosen-id");

        using var response = await TestApi.PostGenerate(client, "chat-default", "Hello");

        var body = await TestApi.ReadJson(response);
        Assert.NotEqual("client-chosen-id", body.GetProperty("correlationId").GetString());
    }

    public void Dispose() => _api.Dispose();
}
