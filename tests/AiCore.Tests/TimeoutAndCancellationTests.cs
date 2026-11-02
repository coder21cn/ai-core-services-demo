using AiCore.Api.Execution;
using System.Net;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests;

/// <summary>
/// T-05 / FR-06 / NFR-05: provider timeouts, queue-wait timeouts and cancellation release every permit and
/// tenant slot, map to documented errors, and never hang. Timeouts fire only when the test advances a
/// <see cref="FakeTimeProvider"/>; no test depends on wall-clock timing.
/// </summary>
public sealed class TimeoutAndCancellationTests
{
    private const string Prompt = "PROMPT-MARKER-5f3a";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(5);

    private static Dictionary<string, string?> Settings(int permits = 1, int queue = 0, int graceMs = 10_000, int queueWaitMs = 60_000) => new()
    {
        ["AiCore:Concurrency:MaxConcurrentProviderCalls"] = permits.ToString(),
        ["AiCore:Concurrency:MaxQueuedRequests"] = queue.ToString(),
        ["AiCore:Concurrency:MaxQueueWaitMs"] = queueWaitMs.ToString(),
        ["AiCore:Resilience:ProviderTimeoutMs"] = ((int)ProviderTimeout.TotalMilliseconds).ToString(),
        ["AiCore:Resilience:CancellationGraceMs"] = graceMs.ToString(),
        ["AiCore:Resilience:MaxAttempts"] = "3",
        ["AiCore:Resilience:RetryBackoffMs"] = "0",
    };

    [Fact]
    public async Task T05_ProviderTimeout_Returns504_IsNotRetried_AndReleasesCapacity()
    {
        var time = new FakeTimeProvider();
        var provider = new GatedProvider();
        using var api = new TestApi(Settings(), provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var pending = TestApi.PostGenerate(client, TestApi.TestAlias, Prompt);
        await TestApi.WaitUntil(() => provider.InFlight == 1, "provider call in flight");

        time.Advance(ProviderTimeout - TimeSpan.FromMilliseconds(1));
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        using var response = await pending.WaitAsync(Deadline);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After")); // the provider may have completed the work
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Prompt, raw);
        var body = await TestApi.ReadJson(response);
        Assert.Equal("provider_timeout", body.GetProperty("code").GetString());
        Assert.Equal(504, body.GetProperty("status").GetInt32());
        var correlationId = body.GetProperty("correlationId").GetString()!;

        Assert.Equal(1, provider.Calls); // a timeout is ambiguous, so it is never retried
        Assert.Equal(0, provider.InFlight); // the provider observed cancellation
        Assert.Equal(0, api.Invoker.AbandonedInFlight);
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 1);

        Assert.Contains(api.Logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Contains("timed out") && e.Contains(correlationId));
        Assert.DoesNotContain(api.Logs.Entries, e => e.Contains(Prompt));

        // The single permit is usable again.
        provider.Release();
        using var next = await TestApi.PostGenerate(client, TestApi.TestAlias, "next").WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task T05_AfterTimeout_PermitIsHeldUntilTheProviderActuallyStops()
    {
        var time = new FakeTimeProvider();
        var provider = new CancellationProbeProvider(honourCancellation: true);
        using var api = new TestApi(Settings(), provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var pending = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
        await TestApi.WaitUntil(() => provider.InFlight == 1, "provider call in flight");

        time.Advance(ProviderTimeout);
        await TestApi.WaitUntil(() => provider.CancellationObserved, "provider saw the cancellation");

        // The provider is still unwinding, so its permit still counts against the bound (FR-05): no other call
        // can start in its place yet.
        Assert.Equal(1, provider.InFlight);
        Assert.Equal(0, api.Gate.GetStatistics().CurrentAvailablePermits);
        Assert.False(pending.IsCompleted);

        provider.AllowStop();
        using var response = await pending.WaitAsync(Deadline);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(0, provider.InFlight);
        Assert.Equal(0, api.Invoker.AbandonedInFlight);
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 1);
    }

    [Fact]
    public async Task T05_ProviderIgnoringCancellation_IsAbandonedAfterGrace_SoItsPermitIsNotHeldIndefinitely()
    {
        var time = new FakeTimeProvider();
        var provider = new CancellationProbeProvider(honourCancellation: false);
        using var api = new TestApi(Settings(graceMs: 2_000), provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var pending = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
        await TestApi.WaitUntil(() => provider.InFlight == 1, "provider call in flight");

        time.Advance(ProviderTimeout);
        await TestApi.WaitUntil(() => provider.CancellationObserved, "provider was asked to stop");

        // The grace timer starts when the invoker begins waiting for the provider to stop, a moment this test
        // cannot observe, so time is advanced in steps until the abandoned call's response arrives.
        await TestApi.AdvanceUntil(time, TimeSpan.FromMilliseconds(500), () => pending.IsCompleted, "abandoned after grace");
        using var response = await pending;

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, provider.InFlight); // still running: the adapter ignored cancellation
        Assert.Equal(1, api.Invoker.AbandonedInFlight);
        // ...but it holds neither the shared permit nor its tenant's slot: it is bounded per route instead.
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 1);
        Assert.Contains(api.Logs.Entries, e => e.Level == LogLevel.Error && e.Contains("abandoned"));

        // When the defective call finally finishes, its result is discarded and the abandoned count drops.
        provider.Release();
        await TestApi.WaitUntil(() => api.Invoker.AbandonedInFlight == 0, "abandoned call completed");
        Assert.Equal(0, provider.InFlight);
    }

    [Fact]
    public async Task T05_QueueWaitTimeout_Returns503_FreesTheQueueSlot_AndRefundsTheQuotaToken()
    {
        var time = new FakeTimeProvider();
        var provider = new GatedProvider();
        var settings = Settings(permits: 1, queue: 1, queueWaitMs: 1_000);
        settings["AiCore:Quotas:Default:RequestsPerMinute"] = "1";
        settings["AiCore:Quotas:Default:BurstRequests"] = "2";
        settings["AiCore:Resilience:ProviderTimeoutMs"] = "60000"; // keep the running call alive
        using var api = new TestApi(settings, provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        var running = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
        await TestApi.WaitUntil(() => provider.InFlight == 1, "permit held");
        var queued = TestApi.PostGenerate(client, TestApi.TestAlias, "q");
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentQueuedCount == 1, "request queued");

        time.Advance(TimeSpan.FromMilliseconds(1_000));
        using var timedOut = await queued.WaitAsync(Deadline);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.StatusCode);
        Assert.Equal("1", Assert.Single(timedOut.Headers.GetValues("Retry-After")));
        var body = await TestApi.ReadJson(timedOut);
        Assert.Equal("capacity_exhausted", body.GetProperty("code").GetString());
        Assert.Contains("queue wait", body.GetProperty("detail").GetString());
        Assert.Equal(0, api.Gate.GetStatistics().CurrentQueuedCount);
        Assert.Equal(1, api.Gate.QueueWaitTimeouts);
        Assert.Equal(0, api.Gate.QueueFullRejections);
        Assert.Equal(1, provider.Calls); // the timed-out request never reached the provider

        // The queued request's token was refunded: 2 - 1 (running) - 1 (queued) + 1 (refund) + 1/60 (refill).
        Assert.True(api.Quotas.GetSnapshot(TestApi.TenantA).AvailableRequestTokens >= 1);

        provider.Release();
        Assert.Equal(HttpStatusCode.OK, (await running.WaitAsync(Deadline)).StatusCode);
        ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 1);
    }

    [Fact]
    public async Task T05_ClientCancellationDuringProviderCall_StopsTheProvider_AndIsNotATimeout()
    {
        var time = new FakeTimeProvider();
        var provider = new GatedProvider();
        using var api = new TestApi(Settings(), provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        using var cts = new CancellationTokenSource();
        var cancelled = TestApi.PostGenerate(client, TestApi.TestAlias, "p", cts.Token);
        await TestApi.WaitUntil(() => provider.InFlight == 1, "provider call in flight");

        await cts.CancelAsync(); // before the timeout: fake time has not moved
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Deadline));
        await TestApi.WaitUntil(() => provider.InFlight == 0, "provider observed client cancellation");
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentAvailablePermits == 1, "permit returned");
        await TestApi.WaitUntil(() => api.Quotas.GetSnapshot(TestApi.TenantA).ActiveRequests == 0, "tenant slot returned");

        Assert.Equal(1, provider.Calls);
        Assert.DoesNotContain(api.Logs.Entries, e => e.Contains("timed out"));
        Assert.DoesNotContain(api.Logs.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task T05_TooManyAbandonedCalls_RejectNewCallsToThatRoute_UntilTheyEnd()
    {
        var time = new FakeTimeProvider();
        var provider = new CancellationProbeProvider(honourCancellation: false);
        var settings = Settings(permits: 4, graceMs: 2_000);
        settings["AiCore:Resilience:MaxAbandonedCallsPerRoute"] = "1";
        using var api = new TestApi(settings, provider, time);
        using var clientA = api.CreateClientFor(TestApi.KeyA);
        using var clientB = api.CreateClientFor(TestApi.KeyB);
        try
        {
            var first = TestApi.PostGenerate(clientA, TestApi.TestAlias, "p");
            await TestApi.WaitUntil(() => provider.InFlight == 1, "provider call in flight");
            time.Advance(ProviderTimeout);
            await TestApi.WaitUntil(() => provider.CancellationObserved, "provider was asked to stop");
            await TestApi.AdvanceUntil(time, TimeSpan.FromMilliseconds(500), () => first.IsCompleted, "abandoned after grace");
            Assert.Equal(1, api.Invoker.AbandonedInFlight);

            // The route is now at its abandoned-call limit: new calls fail fast instead of piling up orphans,
            // for every tenant, without being charged quota.
            using var rejected = await TestApi.PostGenerate(clientB, TestApi.TestAlias, "p").WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal("1", Assert.Single(rejected.Headers.GetValues("Retry-After")));
            var rejectedBody = await TestApi.ReadJson(rejected);
            Assert.Equal("provider_unavailable", rejectedBody.GetProperty("code").GetString());
            Assert.Contains("no request was sent", rejectedBody.GetProperty("detail").GetString()); // not "the provider failed"
            Assert.Equal(1, provider.Calls);
            Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantB).ActiveRequests);

            // Other providers are unaffected (the mock's 25 ms latency runs on the fake clock).
            var viaMock = TestApi.PostGenerate(clientB, "chat-default", "p");
            await TestApi.AdvanceUntil(time, TimeSpan.FromMilliseconds(25), () => viaMock.IsCompleted, "mock call completes");
            Assert.Equal(HttpStatusCode.OK, (await viaMock).StatusCode);

            provider.Release();
            await TestApi.WaitUntil(() => api.Invoker.AbandonedInFlight == 0, "abandoned call ended");
            using var recovered = await TestApi.PostGenerate(clientB, TestApi.TestAlias, "p").WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }
        finally
        {
            provider.Release();
        }
    }

    [Fact]
    public async Task T05_CompletionDuringGracePeriod_IsReturned_NotReportedAsATimeout()
    {
        var time = new FakeTimeProvider();
        var provider = new CancellationProbeProvider(honourCancellation: false);
        using var api = new TestApi(Settings(), provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);
        try
        {
            var pending = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
            await TestApi.WaitUntil(() => provider.InFlight == 1, "provider call in flight");
            time.Advance(ProviderTimeout);
            await TestApi.WaitUntil(() => provider.CancellationObserved, "provider was asked to stop");

            // The adapter finishes after the deadline but within the grace period. The completion exists (and was
            // billed), so returning 504 would invite the client to pay for a second one.
            provider.Release();
            using var response = await pending.WaitAsync(Deadline);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("late", (await TestApi.ReadJson(response)).GetProperty("output").GetString());
            Assert.Equal(1, provider.Calls);
            Assert.Equal(0, api.Invoker.AbandonedInFlight);
            ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 1);
            Assert.Contains(api.Logs.Entries, e => e.Level == LogLevel.Warning && e.Contains("after the deadline"));
        }
        finally
        {
            provider.Release();
        }
    }

    /// <summary>
    /// A second model on the mock provider that ignores cancellation. Its 60 s latency runs on the fake clock,
    /// so an abandoned call to it ends only when the test advances time by a minute.
    /// </summary>
    private static Dictionary<string, string?> HangingRouteSettings(int maxAbandoned, int tenantConcurrency = 1_000_000)
    {
        var settings = Settings(permits: 4, graceMs: 2_000);
        settings["AiCore:Resilience:MaxAbandonedCallsPerRoute"] = maxAbandoned.ToString();
        settings["AiCore:Quotas:Default:MaxConcurrentRequests"] = tenantConcurrency.ToString();
        settings["AiCore:Models:hang:Provider"] = "mock";
        settings["AiCore:Models:hang:ProviderModel"] = "mock-hang-test";
        settings["AiCore:MockProvider:Models:mock-hang-test:LatencyMs"] = "60000";
        settings["AiCore:MockProvider:Models:mock-hang-test:HonourCancellation"] = "false";
        return settings;
    }

    private static async Task AbandonOneCallAsync(TestApi api, HttpClient client, FakeTimeProvider time, string alias)
    {
        var before = api.Invoker.AbandonedInFlight;
        var pending = TestApi.PostGenerate(client, alias, "p");
        await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentAvailablePermits < api.Gate.PermitLimit, "call holds a permit");
        await TestApi.AdvanceUntil(time, TimeSpan.FromMilliseconds(500), () => pending.IsCompleted, "call abandoned after deadline and grace");
        using var response = await pending;
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(before + 1, api.Invoker.AbandonedInFlight);
    }

    private static async Task<HttpResponseMessage> PostOnFakeClock(HttpClient client, string alias, FakeTimeProvider time)
    {
        // The mock's 25 ms latency also runs on the fake clock.
        var pending = TestApi.PostGenerate(client, alias, "p");
        await TestApi.AdvanceUntil(time, TimeSpan.FromMilliseconds(25), () => pending.IsCompleted, $"{alias} completes");
        return await pending;
    }

    [Fact]
    public async Task T05_AbandonedCallLimit_IsPerRoute_OtherModelsOfTheProviderStayAvailable()
    {
        var time = new FakeTimeProvider();
        using var api = new TestApi(HangingRouteSettings(maxAbandoned: 1), timeProvider: time);
        using var clientA = api.CreateClientFor(TestApi.KeyA);
        using var clientB = api.CreateClientFor(TestApi.KeyB);

        await AbandonOneCallAsync(api, clientA, time, "hang");

        // The hanging route (mock/mock-hang-test) is refused for every tenant...
        using (var refused = await TestApi.PostGenerate(clientB, "hang", "p").WaitAsync(Deadline))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("provider_unavailable", (await TestApi.ReadJson(refused)).GetProperty("code").GetString());
        }

        // ...but another model of the same provider is not.
        using var otherModel = await PostOnFakeClock(clientB, "chat-default", time);
        Assert.Equal(HttpStatusCode.OK, otherModel.StatusCode);

        time.Advance(TimeSpan.FromMinutes(1));
        await TestApi.WaitUntil(() => api.Invoker.AbandonedInFlight == 0, "abandoned call ended");
    }

    [Fact]
    public async Task T05_AbandonedCall_DoesNotHoldItsTenantSlot_SoTheTenantIsNeitherBlamedNorLockedOut()
    {
        var time = new FakeTimeProvider();
        using var api = new TestApi(HangingRouteSettings(maxAbandoned: 1, tenantConcurrency: 1), timeProvider: time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        await AbandonOneCallAsync(api, client, time, "hang");

        // The abandoned call is a provider defect: it counts against the route's limit, not the tenant's quota.
        Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantA).ActiveRequests);

        // The hanging route answers 503 (provider condition), not 429 (tenant usage)...
        using (var refused = await TestApi.PostGenerate(client, "hang", "p").WaitAsync(Deadline))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("provider_unavailable", (await TestApi.ReadJson(refused)).GetProperty("code").GetString());
        }

        // ...and healthy routes stay available to the same tenant.
        using var healthy = await PostOnFakeClock(client, "chat-default", time);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        time.Advance(TimeSpan.FromMinutes(1));
        await TestApi.WaitUntil(() => api.Invoker.AbandonedInFlight == 0, "abandoned call ended");
    }

    [Fact]
    public async Task T05_QueuedRequests_RecheckTheAbandonedCallLimit_AfterGettingAPermit()
    {
        var time = new FakeTimeProvider();
        var provider = new CancellationProbeProvider(honourCancellation: false);
        var settings = Settings(permits: 2, queue: 1, graceMs: 2_000);
        settings["AiCore:Resilience:MaxAbandonedCallsPerRoute"] = "1";
        using var api = new TestApi(settings, provider, time);
        using var client = api.CreateClientFor(TestApi.KeyA);
        try
        {
            var first = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
            var second = TestApi.PostGenerate(client, TestApi.TestAlias, "p");
            await TestApi.WaitUntil(() => provider.InFlight == 2, "both permits in use");
            var queued = TestApi.PostGenerate(client, TestApi.TestAlias, "p"); // passes the check: nothing abandoned yet
            await TestApi.WaitUntil(() => api.Gate.GetStatistics().CurrentQueuedCount == 1, "third request queued");

            await TestApi.AdvanceUntil(
                time, TimeSpan.FromMilliseconds(500),
                () => first.IsCompleted && second.IsCompleted && queued.IsCompleted, "all three requests answered");

            Assert.Equal(HttpStatusCode.GatewayTimeout, (await first).StatusCode);
            Assert.Equal(HttpStatusCode.GatewayTimeout, (await second).StatusCode);
            using var refused = await queued;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("provider_unavailable", (await TestApi.ReadJson(refused)).GetProperty("code").GetString());
            Assert.Equal(2, provider.Calls); // the queued request never reached the hanging route
            Assert.Equal(2, api.Invoker.AbandonedInFlight); // within MaxAbandoned + MaxConcurrentProviderCalls - 1
            ConcurrencyTests.AssertNoLeakedCapacity(api, permits: 2);
        }
        finally
        {
            provider.Release();
        }
    }

    [Fact]
    public async Task T05_RefusalsAtTheAbandonedCallLimit_LogTheTransitions_NotEveryRequest()
    {
        var time = new FakeTimeProvider();
        using var api = new TestApi(HangingRouteSettings(maxAbandoned: 1), timeProvider: time);
        using var client = api.CreateClientFor(TestApi.KeyA);

        await AbandonOneCallAsync(api, client, time, "hang");
        Assert.Single(api.Logs.Entries, e => e.Level == LogLevel.Warning && e.Contains("reached its limit"));
        var warningsBefore = api.Logs.Entries.Count(e => e.Level >= LogLevel.Warning);

        for (var i = 0; i < 5; i++)
        {
            using var refused = await TestApi.PostGenerate(client, "hang", "p").WaitAsync(Deadline);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        }

        // Refused requests add nothing at Warning level or above, whatever the message text.
        Assert.Equal(warningsBefore, api.Logs.Entries.Count(e => e.Level >= LogLevel.Warning));

        time.Advance(TimeSpan.FromMinutes(1));
        await TestApi.WaitUntil(() => api.Invoker.AbandonedInFlight == 0, "abandoned call ended");
        await TestApi.WaitUntil(
            () => api.Logs.Entries.Any(e => e.Level == LogLevel.Information && e.Contains("accepting new calls")),
            "recovery logged");
    }

    /// <summary>Starts calls with one client token, waits until they hold permits, disconnects, and waits until they are abandoned.</summary>
    private static async Task AbortCallsAsync(TestApi api, HttpClient client, FakeTimeProvider time, string alias, int count)
    {
        var before = api.Invoker.AbandonedInFlight;
        using var cts = new CancellationTokenSource();
        var pending = Enumerable.Range(0, count).Select(_ => TestApi.PostGenerate(client, alias, "p", cts.Token)).ToList();
        await TestApi.WaitUntil(
            () => api.Gate.GetStatistics().CurrentAvailablePermits == api.Gate.PermitLimit - count, $"{count} calls hold permits");

        // The in-memory test server completes a cancelled client call only once the server side has finished,
        // which here is after the grace period on the fake clock.
        await cts.CancelAsync();
        await TestApi.AdvanceUntil(
            time, TimeSpan.FromMilliseconds(500), () => api.Invoker.AbandonedInFlight == before + count, "aborted calls abandoned after grace");
        foreach (var call in pending)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Deadline));
        }
    }

    [Fact]
    public async Task T05_OneTenantAbortingItsOwnCalls_CannotUseUpARoutesAbandonedCallBudget()
    {
        var time = new FakeTimeProvider();
        using var api = new TestApi(HangingRouteSettings(maxAbandoned: 3, tenantConcurrency: 2), timeProvider: time);
        using var clientA = api.CreateClientFor(TestApi.KeyA);
        using var clientB = api.CreateClientFor(TestApi.KeyB);

        // Tenant A starts its 2 allowed calls on the route and disconnects; the adapter ignores the cancellation.
        await AbortCallsAsync(api, clientA, time, "hang", 2);
        Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantA).ActiveRequests); // slots free: no 429 lockout

        // A can't add more abandoned calls on that route: it is refused there with 503, before reaching the provider...
        var more = Enumerable.Range(0, 2).Select(_ => TestApi.PostGenerate(clientA, "hang", "p")).ToList();
        await TestApi.AdvanceUntil(time, TimeSpan.FromMilliseconds(500), () => more.All(t => t.IsCompleted), "A's further calls answered");
        foreach (var call in more)
        {
            using var refused = await call;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            var refusedBody = await TestApi.ReadJson(refused);
            Assert.Equal("provider_unavailable", refusedBody.GetProperty("code").GetString());
            Assert.Contains("no request was sent", refusedBody.GetProperty("detail").GetString());
        }

        // Operators can see that a tenant is being refused on this route, once, without a log line per request.
        Assert.Single(api.Logs.Entries, e => e.Level == LogLevel.Warning && e.Contains("refused on this route"));

        Assert.Equal(2, api.Invoker.AbandonedInFlightFor(ProviderRoutes.Key("mock", "mock-hang-test")));

        // ...so the route stays open for other tenants...
        Assert.False(api.Invoker.HasTooManyAbandonedCalls(ProviderRoutes.Key("mock", "mock-hang-test")));
        using var ctsB = new CancellationTokenSource();
        var callB = TestApi.PostGenerate(clientB, "hang", "p", ctsB.Token);
        await TestApi.WaitUntil(
            () => api.Gate.GetStatistics().CurrentAvailablePermits == api.Gate.PermitLimit - 1, "tenant B's call reached the provider");
        await ctsB.CancelAsync();

        // ...and A is not locked out of other routes.
        using var healthy = await PostOnFakeClock(clientA, "chat-default", time);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);

        await TestApi.AdvanceUntil(
            time, TimeSpan.FromSeconds(10), () => callB.IsCompleted && api.Invoker.AbandonedInFlight == 0, "abandoned calls ended");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callB);
    }
}

