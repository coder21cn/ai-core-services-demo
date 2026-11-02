using System.Net;
using AiCore.Api.Providers;
using AiCore.Tests.Infrastructure;

namespace AiCore.Tests;

/// <summary>
/// T-06 / T-07 / FR-10: the platform retries only failures the provider guarantees were not processed, a
/// bounded number of times. Ambiguous transient failures and permanent failures get exactly one attempt,
/// because a second call could produce a second, separately billed completion.
/// </summary>
public sealed class RetryPolicyTests
{
    private const string Prompt = "PROMPT-MARKER-77e1";

    private static Dictionary<string, string?> Attempts(int maxAttempts) => new()
    {
        ["AiCore:Resilience:MaxAttempts"] = maxAttempts.ToString(),
        ["AiCore:Resilience:RetryBackoffMs"] = "0",
    };

    [Fact]
    public async Task T06_PermanentFailure_IsNotRetried_AndReturnsASafe502()
    {
        var provider = new ScriptedProvider(_ => throw new ProviderPermanentException($"content rejected: {Prompt}"));
        using var api = new TestApi(Attempts(3), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, Prompt);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Prompt, raw);
        Assert.DoesNotContain("content rejected", raw);
        Assert.Equal("provider_error", (await TestApi.ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal(1, provider.Calls);
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 8);
    }

    [Fact]
    public async Task T06_RepeatedPermanentFailures_NeverAccumulateRetries()
    {
        var provider = new ScriptedProvider(_ => throw new ProviderPermanentException("bad model"));
        using var api = new TestApi(Attempts(5), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        for (var i = 0; i < 4; i++)
        {
            using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }

        Assert.Equal(4, provider.Calls); // one call per request, never more
        Assert.All(provider.Requests, r => Assert.Equal(1, r.Attempt));
    }

    [Fact]
    public async Task T07_NotProcessedFailure_IsRetried_AndTheRetryCanSucceed()
    {
        var provider = new ScriptedProvider(r => r.Attempt == 1 ? ScriptedProvider.Throttled(r) : ScriptedProvider.Ok(r));
        using var api = new TestApi(Attempts(3), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = provider.Requests.ToArray();
        Assert.Equal([1, 2], requests.Select(r => r.Attempt));
        Assert.Single(requests.Select(r => r.CorrelationId).Distinct()); // same logical request
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 8);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task T07_NotProcessedFailures_AreRetriedExactlyUpToMaxAttempts(int maxAttempts)
    {
        var provider = new ScriptedProvider(ScriptedProvider.Throttled);
        using var api = new TestApi(Attempts(maxAttempts), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("provider_unavailable", (await TestApi.ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal(Enumerable.Range(1, maxAttempts), provider.Requests.Select(r => r.Attempt));
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 8);
    }

    [Fact]
    public async Task T07_AmbiguousTransientFailure_IsNotRetried()
    {
        // Default RequestNotProcessed = false: e.g. a connection reset after the request was sent. The provider
        // may have generated (and billed) a completion, so the platform does not try again.
        var provider = new ScriptedProvider(_ => throw new ProviderTransientException("connection reset"));
        using var api = new TestApi(Attempts(3), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("provider_unavailable", (await TestApi.ReadJson(response)).GetProperty("code").GetString());
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task T07_UnexpectedException_IsNotRetried()
    {
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException("sdk bug"));
        using var api = new TestApi(Attempts(3), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task T07_RetriesConsumeOneQuotaTokenPerRequest_NotPerAttempt()
    {
        var settings = Attempts(3);
        settings["AiCore:Quotas:Default:RequestsPerMinute"] = "1";
        settings["AiCore:Quotas:Default:BurstRequests"] = "1";
        var provider = new ScriptedProvider(r => r.Attempt < 3 ? ScriptedProvider.Throttled(r) : ScriptedProvider.Ok(r));
        using var api = new TestApi(settings, provider, new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, provider.Calls);
        Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantA).AvailableRequestTokens, precision: 6);
    }

    [Fact]
    public async Task T07_NotProcessedFailure_StillChargesTheRequestToken()
    {
        // Deliberate (docs/m2-design-notes.md §4): the rate quota limits provider calls made on a tenant's
        // behalf. Throttled attempts still hit the provider's admission and its account-wide rate limit, and
        // charging them stops a client that retries faster than its quota from amplifying an upstream outage.
        // A client that honours Retry-After at or below its sustained rate never drains its bucket this way.
        var settings = Attempts(2);
        settings["AiCore:Quotas:Default:RequestsPerMinute"] = "1";
        settings["AiCore:Quotas:Default:BurstRequests"] = "2";
        var provider = new ScriptedProvider(ScriptedProvider.Throttled);
        using var api = new TestApi(settings, provider, new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, api.Quotas.GetSnapshot(TestApi.TenantA).AvailableRequestTokens, precision: 6);
    }

    [Theory]
    [InlineData("sim-throttled-once", HttpStatusCode.OK)]
    [InlineData("sim-throttled", HttpStatusCode.ServiceUnavailable)]
    public async Task T07_MockThrottlingAliases_ExerciseTheRetryPolicyThroughThePublicApi(string alias, HttpStatusCode expected)
    {
        // Default configuration from appsettings.json: MaxAttempts 2, RetryBackoffMs 250.
        using var api = new TestApi(new() { ["AiCore:Resilience:CancellationGraceMs"] = "1000" });
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var response = await TestApi.PostGenerate(client, alias, "p");

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains(api.Logs.Entries, e => e.Contains("retrying") && e.Contains("attempt 1 of 2"));
    }
}
