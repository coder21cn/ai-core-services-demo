using System.Net;
using System.Net.Http.Json;
using AiCore.Api.Providers;
using AiCore.Tests.Infrastructure;

namespace AiCore.Tests;

public sealed class NumericBoundaryTests
{
    [Theory]
    [InlineData(int.MaxValue / 4)]
    [InlineData(int.MaxValue / 4 + 1)]
    [InlineData(1_073_741_824)]
    [InlineData(int.MaxValue)]
    public async Task LargeOutputBudget_DoesNotOverflowOrTruncateTheMockResult(int budget)
    {
        var provider = new MockModelProvider(new MockProviderOptions
        {
            Models = { ["m"] = new MockModelOptions() },
        }, TimeProvider.System);
        var request = new ProviderRequest("correlation", "tenant", "m", "hello", 256);
        var expected = await provider.GenerateAsync(request, CancellationToken.None);
        var actual = await provider.GenerateAsync(request with { MaxOutputTokens = budget }, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.True(actual.Output.Length <= 4L * budget);
    }

    [Theory]
    [InlineData(536_870_912, false)]
    [InlineData(536_870_912, true)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public async Task LargeConfiguredOutputBudget_SucceedsThroughTheEndpoint(int budget, bool omitBudget)
    {
        using var api = new TestApi(new()
        {
            ["AiCore:Limits:MaxOutputTokens"] = budget.ToString(),
            ["AiCore:Limits:DefaultOutputTokens"] = budget.ToString(),
        });
        using var client = api.CreateClientFor(TestApi.KeyA);
        using var response = omitBudget
            ? await TestApi.PostGenerate(client, "chat-default", "hello")
            : await client.PostAsJsonAsync("/v1/generate", new { model = "chat-default", prompt = "hello", maxOutputTokens = budget });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestApi.ReadJson(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("output").GetString()));
        Assert.True(body.GetProperty("usage").GetProperty("outputTokens").GetInt32() > 0);
        Assert.Equal(api.Gate.PermitLimit, api.Gate.GetStatistics().CurrentAvailablePermits);
    }

    [Theory]
    [InlineData(int.MaxValue - 3, 536_870_911)]
    [InlineData(int.MaxValue - 2, 536_870_912)]
    [InlineData(int.MaxValue, 536_870_912)]
    public void TokenEstimate_DoesNotOverflowItsRoundingAddition(int characters, int expected) =>
        Assert.Equal(expected, MockModelProvider.EstimateTokens(characters));

    [Fact]
    public void SharedCapacityAboveIntMax_DoesNotProduceAnIncorrectTenantWarning()
    {
        using var api = new TestApi(new()
        {
            ["AiCore:Concurrency:MaxConcurrentProviderCalls"] = int.MaxValue.ToString(),
            ["AiCore:Concurrency:MaxQueuedRequests"] = "1",
            ["AiCore:Quotas:Default:MaxConcurrentRequests"] = "4",
        });
        using var client = api.CreateClient();
        Assert.DoesNotContain(api.Logs.Entries, entry => entry.Contains("shared capacity"));
    }
}
