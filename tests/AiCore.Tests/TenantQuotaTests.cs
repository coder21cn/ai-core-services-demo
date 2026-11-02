using System.Net;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests;

/// <summary>
/// T-03 / FR-04: per-tenant quotas through the real pipeline. One tenant exhausting its quota gets 429 and
/// never reaches the provider, while another tenant's capacity is untouched. Time is a
/// <see cref="FakeTimeProvider"/>, so token refill happens only when a test advances it.
/// </summary>
public sealed class TenantQuotaTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static Dictionary<string, string?> Quota(int rpm, int burst, int concurrent, int permits = 8, int queue = 16) => new()
    {
        ["AiCore:Quotas:Default:RequestsPerMinute"] = rpm.ToString(),
        ["AiCore:Quotas:Default:BurstRequests"] = burst.ToString(),
        ["AiCore:Quotas:Default:MaxConcurrentRequests"] = concurrent.ToString(),
        ["AiCore:Concurrency:MaxConcurrentProviderCalls"] = permits.ToString(),
        ["AiCore:Concurrency:MaxQueuedRequests"] = queue.ToString(),
    };

    [Fact]
    public async Task T03_TenantAtRateQuota_Gets429WithRetryAfter_WhileOtherTenantProceeds()
    {
        var time = new FakeTimeProvider();
        var provider = new RecordingProvider();
        // 6 requests/minute = one token every 10 s; bursts of up to 3.
        using var api = new TestApi(Quota(rpm: 6, burst: 3, concurrent: 10), provider, time);
        using var clientA = api.CreateClientFor(TestApi.KeyA);
        using var clientB = api.CreateClientFor(TestApi.KeyB);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using (var rejected = await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a-over"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
            Assert.Equal("10", Assert.Single(rejected.Headers.GetValues("Retry-After")));
            var body = await TestApi.ReadJson(rejected);
            Assert.Equal("rate_quota_exceeded", body.GetProperty("code").GetString());
            Assert.Equal(429, body.GetProperty("status").GetInt32());
        }

        // Tenant B has its own full bucket.
        for (var i = 0; i < 3; i++)
        {
            using var ok = await TestApi.PostGenerate(clientB, TestApi.TestAlias, "b");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        // The rejected request never reached the provider.
        Assert.Equal(3, provider.Requests.Count(r => r.TenantId == TestApi.TenantA));
        Assert.Equal(3, provider.Requests.Count(r => r.TenantId == TestApi.TenantB));
        Assert.DoesNotContain(provider.Requests, r => r.Prompt == "a-over");

        // One token refills after 10 s, not before.
        time.Advance(TimeSpan.FromSeconds(9));
        using (var stillLimited = await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, stillLimited.StatusCode);
            Assert.Equal("1", Assert.Single(stillLimited.Headers.GetValues("Retry-After")));
        }

        time.Advance(TimeSpan.FromSeconds(1));
        using (var refilled = await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a"))
        {
            Assert.Equal(HttpStatusCode.OK, refilled.StatusCode);
        }

        using var emptyAgain = await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a");
        Assert.Equal(HttpStatusCode.TooManyRequests, emptyAgain.StatusCode);
    }

    [Fact]
    public async Task T03_TenantAtConcurrencyQuota_Gets429_WhileOtherTenantStillReachesTheProvider()
    {
        var provider = new GatedProvider();
        // 4 shared permits, no queue. Without the per-tenant limit, tenant A could hold all 4 permits and
        // tenant B would get 503 capacity_exhausted.
        using var api = new TestApi(Quota(rpm: 1000, burst: 1000, concurrent: 2, permits: 4, queue: 0), provider);
        using var clientA = api.CreateClientFor(TestApi.KeyA);
        using var clientB = api.CreateClientFor(TestApi.KeyB);

        var runningA = Enumerable.Range(0, 2).Select(_ => TestApi.PostGenerate(clientA, TestApi.TestAlias, "a")).ToList();
        await TestApi.WaitUntil(() => provider.InFlight == 2, "tenant A has 2 calls in flight");

        using (var rejected = await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a").WaitAsync(Deadline))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal("1", Assert.Single(rejected.Headers.GetValues("Retry-After")));
            Assert.Equal("concurrency_quota_exceeded", (await TestApi.ReadJson(rejected)).GetProperty("code").GetString());
        }

        Assert.Equal(2, provider.Calls);
        Assert.Equal(2, api.Gate.GetStatistics().CurrentAvailablePermits);

        // Tenant B uses the capacity tenant A is not allowed to take.
        var runningB = Enumerable.Range(0, 2).Select(_ => TestApi.PostGenerate(clientB, TestApi.TestAlias, "b")).ToList();
        await TestApi.WaitUntil(() => provider.InFlight == 4, "tenant B's calls reached the provider");
        Assert.Equal(2, api.Quotas.GetSnapshot(TestApi.TenantA).ActiveRequests);
        Assert.Equal(2, api.Quotas.GetSnapshot(TestApi.TenantB).ActiveRequests);

        provider.Release();
        var responses = await Task.WhenAll(runningA.Concat(runningB)).WaitAsync(Deadline);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(4, provider.MaxInFlight);
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 4);
    }

    [Fact]
    public async Task T03_PerTenantOverride_AppliesOnlyToThatTenant()
    {
        var time = new FakeTimeProvider();
        var settings = Quota(rpm: 1, burst: 1, concurrent: 10);
        settings[$"AiCore:Quotas:Tenants:{TestApi.TenantB}:BurstRequests"] = "3";
        using var api = new TestApi(settings, new RecordingProvider(), time);
        using var clientA = api.CreateClientFor(TestApi.KeyA);
        using var clientB = api.CreateClientFor(TestApi.KeyB);

        var statusesA = new List<HttpStatusCode>();
        var statusesB = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statusesA.Add((await TestApi.PostGenerate(clientA, TestApi.TestAlias, "a")).StatusCode);
            statusesB.Add((await TestApi.PostGenerate(clientB, TestApi.TestAlias, "b")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statusesA);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statusesB);
        Assert.Equal(1, api.Quotas.LimitsFor(TestApi.TenantB).RequestsPerMinute); // unset fields inherit the default
    }

    [Fact]
    public async Task T03_RequestRejectedForGlobalCapacity_GetsItsQuotaTokenBack()
    {
        var time = new FakeTimeProvider();
        var provider = new GatedProvider();
        // Tenant A: 2 tokens, effectively no refill. One shared permit, no queue.
        using var api = new TestApi(Quota(rpm: 1, burst: 2, concurrent: 10, permits: 1, queue: 0), provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var first = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
        await TestApi.WaitUntil(() => provider.InFlight == 1, "first call holds the only permit");

        // Admitted by the tenant quota (second token), then rejected by the full global gate: platform overload,
        // not the tenant's usage, so the token is refunded.
        using (var overloaded = await TestApi.PostGenerate(client, TestApi.TestAlias, "p").WaitAsync(Deadline))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, overloaded.StatusCode);
            Assert.Equal("capacity_exhausted", (await TestApi.ReadJson(overloaded)).GetProperty("code").GetString());
        }

        Assert.Equal(1, api.Quotas.GetSnapshot(TestApi.TenantA).AvailableRequestTokens, precision: 6);

        provider.Release();
        Assert.Equal(HttpStatusCode.OK, (await first.WaitAsync(Deadline)).StatusCode);

        // The refunded token admits one more request; then the bucket is empty.
        using var third = await TestApi.PostGenerate(client, TestApi.TestAlias, "p").WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        using var fourth = await TestApi.PostGenerate(client, TestApi.TestAlias, "p").WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.Equal("rate_quota_exceeded", (await TestApi.ReadJson(fourth)).GetProperty("code").GetString());
        Assert.Equal(2, provider.Calls);
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 1);
    }

    [Fact]
    public async Task T03_InvalidRequests_DoNotConsumeQuota()
    {
        var time = new FakeTimeProvider();
        using var api = new TestApi(Quota(rpm: 1, burst: 1, concurrent: 10), new RecordingProvider(), time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await TestApi.PostGenerate(client, "no-such-alias", "p")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await TestApi.PostRaw(client, """{"model":"test-model"}""")).StatusCode);
        }

        using var ok = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
