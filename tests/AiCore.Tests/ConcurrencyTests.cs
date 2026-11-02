using System.Net;
using AiCore.Api.Providers;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace AiCore.Tests;

/// <summary>
/// T-04 / FR-05 / NFR-06: the in-flight bound holds, waiting is bounded, overflow is rejected, and
/// permits are always returned. Uses a gated provider plus limiter statistics, not timing guesses.
/// </summary>
public sealed class ConcurrencyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static Dictionary<string, string?> Limits(int permits, int queue) => new()
    {
        ["AiCore:Concurrency:MaxConcurrentProviderCalls"] = permits.ToString(),
        ["AiCore:Concurrency:MaxQueuedRequests"] = queue.ToString(),
    };

    [Fact]
    public async Task T04_ExcessRequests_WaitInBoundedQueue_ThenAreRejected_AndBoundIsNeverExceeded()
    {
        var provider = new GatedProvider();
        using var api = new TestApi(Limits(permits: 4, queue: 2), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        // Fill every permit.
        var running = Enumerable.Range(0, 4).Select(_ => TestApi.PostGenerate(client, TestApi.TestAlias, "p")).ToList();
        await TestApi.WaitUntil(() => provider.InFlight == 4, "4 provider calls in flight");

        // Fill the bounded queue.
        var queued = Enumerable.Range(0, 2).Select(_ => TestApi.PostGenerate(client, TestApi.TestAlias, "q")).ToList();
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentQueuedCount == 2, "2 requests queued");

        // Everything beyond permits + queue is rejected immediately, while the gate is still closed.
        var rejected = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => TestApi.PostGenerate(client, TestApi.TestAlias, "r"))).WaitAsync(Deadline);
        foreach (var response in rejected)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", Assert.Single(response.Headers.GetValues("Retry-After")));
            Assert.Equal("capacity_exhausted", (await TestApi.ReadJson(response)).GetProperty("code").GetString());
        }

        // Queued requests have not reached the provider.
        Assert.Equal(4, provider.Calls);
        Assert.All(queued, t => Assert.False(t.IsCompleted));

        provider.Release();
        var completed = await Task.WhenAll(running.Concat(queued)).WaitAsync(Deadline);

        Assert.All(completed, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(6, provider.Calls);
        Assert.Equal(10, api.Gate.QueueFullRejections);
        Assert.Equal(0, api.Gate.QueueWaitTimeouts);
        Assert.Equal(4, provider.MaxInFlight);
        AssertNoLeakedCapacity(api, permits: 4);
    }

    [Fact]
    public async Task T04_ZeroQueue_RejectsImmediatelyWhenFull()
    {
        var provider = new GatedProvider();
        using var api = new TestApi(Limits(permits: 1, queue: 0), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var first = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
        await TestApi.WaitUntil(() => provider.InFlight == 1, "1 provider call in flight");

        using var second = await TestApi.PostGenerate(client, TestApi.TestAlias, "p").WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);

        provider.Release();
        Assert.Equal(HttpStatusCode.OK, (await first.WaitAsync(Deadline)).StatusCode);
        Assert.Equal(1, provider.Calls);
        AssertNoLeakedCapacity(api, permits: 1);
    }

    [Fact]
    public async Task NFR06_Synthetic100ConcurrentRequests_NeverExceedConfiguredBound()
    {
        const int permits = 8;
        const int total = 100;
        var provider = new GatedProvider();
        using var api = new TestApi(Limits(permits, queue: total), provider);
        using var clientA = api.CreateClientFor(TestApi.KeyA);
        using var clientB = api.CreateClientFor(TestApi.KeyB);

        var requests = Enumerable.Range(0, total)
            .Select(i => TestApi.PostGenerate(i % 2 == 0 ? clientA : clientB, TestApi.TestAlias, $"p{i}"))
            .ToList();

        await TestApi.WaitUntil(() => provider.InFlight == permits, $"{permits} provider calls in flight");
        await TestApi.WaitUntil(
            () => api.Gate.GetStatistics().CurrentQueuedCount == total - permits, $"{total - permits} requests queued");
        Assert.Equal(permits, provider.Calls);

        provider.Release();
        var responses = await Task.WhenAll(requests).WaitAsync(Deadline);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        // 92 requests queued and all succeeded: no rejections, although the limiter's TotalFailedLeases counts
        // each of their fast-path misses.
        Assert.Equal(0, api.Gate.QueueFullRejections);
        Assert.Equal(0, api.Gate.QueueWaitTimeouts);
        Assert.Equal(total, provider.Calls);
        Assert.Equal(permits, provider.MaxInFlight);
        AssertNoLeakedCapacity(api, permits);
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("transient")]
    [InlineData("unexpected")]
    public async Task NFR05_FailingProvider_ReleasesItsPermit(string failure)
    {
        Func<Exception> exception = failure switch
        {
            "permanent" => () => new ProviderPermanentException("permanent"),
            "transient" => () => new ProviderTransientException("transient"),
            _ => () => new InvalidOperationException("boom"),
        };
        using var api = new TestApi(Limits(permits: 1, queue: 0), new ThrowingProvider(exception));
        using var client = api.CreateClientFor(TestApi.KeyA);

        // With a single permit and no queue, any leak would turn the next call into 503 capacity_exhausted.
        for (var i = 0; i < 3; i++)
        {
            using var failed = await TestApi.PostGenerate(client, TestApi.TestAlias, "p");
            Assert.NotEqual("capacity_exhausted", (await TestApi.ReadJson(failed)).GetProperty("code").GetString());
        }

        using var ok = await TestApi.PostGenerate(client, "chat-default", "p");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        AssertNoLeakedCapacity(api, permits: 1);
    }

    [Fact]
    public async Task ClientCancellationWhileQueued_LeavesTheQueue_AndNeverReachesTheProvider()
    {
        var provider = new GatedProvider();
        using var api = new TestApi(Limits(permits: 1, queue: 1), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var running = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
        await TestApi.WaitUntil(() => provider.InFlight == 1, "1 provider call in flight");

        using var cts = new CancellationTokenSource();
        var queued = TestApi.PostGenerate(client, TestApi.TestAlias, "q", cts.Token);
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentQueuedCount == 1, "1 request queued");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentQueuedCount == 0, "queue slot freed");

        // The freed slot is usable again while the permit is still held.
        var requeued = TestApi.PostGenerate(client, TestApi.TestAlias, "r");
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentQueuedCount == 1, "slot reused");

        provider.Release();
        Assert.All(await Task.WhenAll(running, requeued).WaitAsync(Deadline), r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, provider.Calls);
        AssertNoLeakedCapacity(api, permits: 1);
    }

    [Fact]
    public async Task ClientCancellationDuringProviderCall_ReturnsThePermit()
    {
        var provider = new GatedProvider();
        using var api = new TestApi(Limits(permits: 1, queue: 0), provider);
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var cts = new CancellationTokenSource();
        var cancelled = TestApi.PostGenerate(client, TestApi.TestAlias, "p", cts.Token);
        await TestApi.WaitUntil(() => provider.InFlight == 1, "1 provider call in flight");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Deadline));
        await TestApi.WaitUntil(() => provider.InFlight == 0, "provider observed cancellation");
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentAvailablePermits == 1, "permit returned");

        // A client abort is not a service failure: it must not be logged as an unexpected provider error.
        Assert.DoesNotContain(api.Logs.Entries, e => e.Level >= LogLevel.Error);

        // With one permit and no queue, a leak would make this 503 capacity_exhausted.
        provider.Release();
        using var next = await TestApi.PostGenerate(client, TestApi.TestAlias, "q").WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        AssertNoLeakedCapacity(api, permits: 1);
    }

    internal static void AssertNoLeakedCapacity(TestApi api, int permits)
    {
        var stats = api.Gate.GetStatistics();
        Assert.Equal(permits, stats.CurrentAvailablePermits);
        Assert.Equal(0, stats.CurrentQueuedCount);
        Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantA).ActiveRequests);
        Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantB).ActiveRequests);
    }
}
