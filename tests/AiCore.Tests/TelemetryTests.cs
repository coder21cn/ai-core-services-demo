using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using AiCore.Api.Observability;
using AiCore.Api.Providers;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests;

public sealed class TelemetryTests
{
    private const string Marker = "PRIVATE-input-output-key-182947";

    [Fact]
    public async Task T08_Success_RecordsCorrelatedStructuredTelemetry_WithoutContent()
    {
        using var api = new TestApi(extraProvider: new ScriptedProvider(_ => new(Marker, 7, 3)));
        using var capture = new Capture(api);
        using var client = api.CreateClientFor(TestApi.KeyA);
        client.DefaultRequestHeaders.Add("X-Correlation-Id", Marker);
        client.DefaultRequestHeaders.Add("baggage", $"secret={Marker}");
        client.DefaultRequestHeaders.Add("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias.ToUpperInvariant(), Marker);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await TestApi.ReadJson(response);
        var id = body.GetProperty("correlationId").GetString();
        var log = Completion(api, "success");
        Assert.Equal(id, log.Fields["CorrelationId"]);
        Assert.Equal(TestApi.TestAlias, log.Fields["ModelAlias"]);
        Assert.Equal(7, log.Fields["InputTokens"]);
        Assert.Equal(3, log.Fields["OutputTokens"]);
        Assert.Equal(true, log.Fields["UsageSimulated"]);
        Assert.NotEqual(TestApi.TenantA, log.Fields["TenantRef"]);
        Assert.Equal(64, Assert.IsType<string>(log.Fields["TenantRef"]).Length);
        var span = Assert.Single(capture.Spans, a => a.OperationName == "aicore.request");
        Assert.Equal("0123456789abcdef0123456789abcdef", span.TraceId.ToString());
        Assert.Equal(span.TraceId.ToString(), log.Fields["TraceId"]);
        Assert.Equal(id, span.GetTagItem("correlation.id"));
        Assert.Equal(log.Fields["TenantRef"], span.GetTagItem("tenant.ref"));
        Assert.Equal(2, capture.Spans.Count(a => a.ParentSpanId == span.SpanId));
        Assert.Single(capture.Measurements, m => m.Name == "aicore.requests" && m.Value == 1);
        Assert.Equal(10, capture.Measurements.Where(m => m.Name == "aicore.usage.tokens").Sum(m => m.Value));
        Assert.Single(capture.Measurements, m => m.Name == "aicore.provider.attempts");
        Assert.All(capture.Measurements.Where(m => m.Name.EndsWith(".duration")), m => Assert.True(m.Value >= 0));
        AssertPrivate(api, capture);
    }

    [Theory]
    [InlineData("permanent", "provider_error", 502)]
    [InlineData("transient", "provider_unavailable", 503)]
    [InlineData("retry", "provider_unavailable", 503)]
    [InlineData("unexpected", "internal_error", 500)]
    public async Task T08_FailureTelemetry_IsSafeAndDistinguishesOutcomes(string kind, string result, int status)
    {
        using var api = new TestApi(new() { ["AiCore:Resilience:RetryBackoffMs"] = "0" },
            new ThrowingProvider(() => kind switch
            {
                "permanent" => new ProviderPermanentException(Marker),
                "unexpected" => new InvalidOperationException(Marker),
                _ => new ProviderTransientException(Marker, kind == "retry"),
            }));
        using var capture = new Capture(api);
        using var client = api.CreateClientFor(TestApi.KeyA);
        using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, Marker);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain(Marker, await response.Content.ReadAsStringAsync());
        Assert.Equal(status, Completion(api, result).Fields["StatusCode"]);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(capture.Spans, s => s.OperationName == "aicore.request").Status);
        Assert.Equal(kind == "retry" ? 2 : 1, capture.Measurements.Count(m => m.Name == "aicore.provider.attempts"));
        Assert.Equal(kind == "retry" ? 1 : 0, capture.Measurements.Count(m => m.Name == "aicore.provider.retries"));
        Assert.DoesNotContain(capture.Measurements, m => m.Name == "aicore.usage.tokens");
        AssertPrivate(api, capture);
    }

    [Fact]
    public async Task T08_Rejections_UseBoundedLabelsAndAnonymousIdentity()
    {
        using var api = new TestApi(new() { ["AiCore:Quotas:Default:BurstRequests"] = "1", ["AiCore:Quotas:Default:RequestsPerMinute"] = "1" },
            new RecordingProvider());
        using var capture = new Capture(api);
        using var client = api.CreateClientFor(TestApi.KeyA);
        using var badClient = api.CreateClientFor(Marker);
        using var denied = await TestApi.PostGenerate(badClient, TestApi.TestAlias, Marker);
        using var invalid = await TestApi.PostGenerate(client, Marker, Marker);
        using var malformed = await TestApi.PostRaw(client, $$"""{"{{Marker}}":true}""");
        using var success = await TestApi.PostGenerate(client, TestApi.TestAlias, Marker);
        using var limited = await TestApi.PostGenerate(client, TestApi.TestAlias, Marker);
        Assert.Equal("anonymous", Completion(api, "unauthenticated").Fields["TenantRef"]);
        Assert.Equal("none", Completion(api, "unknown_model").Fields["ModelAlias"]);
        Assert.Equal(429, Completion(api, "rate_quota_exceeded").Fields["StatusCode"]);
        Assert.Equal(5, capture.Measurements.Count(m => m.Name == "aicore.requests"));
        AssertPrivate(api, capture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndClientAbort_AreDistinct_AndCapacityGaugesRecover(bool abort)
    {
        var time = new FakeTimeProvider();
        var provider = new GatedProvider();
        using var api = new TestApi(new()
        {
            ["AiCore:Resilience:ProviderTimeoutMs"] = "1000",
            ["AiCore:Concurrency:MaxConcurrentProviderCalls"] = "1",
            ["AiCore:Concurrency:MaxQueuedRequests"] = "0",
        }, provider, time);
        using var capture = new Capture(api);
        using var client = api.CreateClientFor(TestApi.KeyA);
        using var cts = new CancellationTokenSource();
        var pending = TestApi.PostGenerate(client, TestApi.TestAlias, Marker, cts.Token);
        try
        {
            await TestApi.WaitUntil(() => provider.InFlight == 1, "provider running");
            capture.Listener.RecordObservableInstruments();
            Assert.Contains(capture.Measurements, m => m.Name == "aicore.provider.permits_in_use" && m.Value == 1);
            using var overloaded = await TestApi.PostGenerate(client, TestApi.TestAlias, Marker);
            Assert.Equal(503, Completion(api, "capacity_exhausted").Fields["StatusCode"]);
            if (abort)
            {
                await cts.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(1));
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            }
            var log = Completion(api, abort ? "cancelled" : "provider_timeout");
            Assert.Equal(abort ? 499 : 504, log.Fields["StatusCode"]);
            capture.Listener.RecordObservableInstruments();
            Assert.Equal(0, capture.Measurements.Last(m => m.Name == "aicore.provider.permits_in_use").Value);
            AssertPrivate(api, capture);
        }
        finally { provider.Release(); }
    }

    [Fact]
    public async Task AbandonedGauge_TracksWorkAfterItsPermitIsReleased_AndThenRecovers()
    {
        var time = new FakeTimeProvider();
        var provider = new CancellationProbeProvider(honourCancellation: false);
        using var api = new TestApi(new()
        {
            ["AiCore:Resilience:ProviderTimeoutMs"] = "1000",
            ["AiCore:Resilience:CancellationGraceMs"] = "0",
            ["AiCore:Concurrency:MaxConcurrentProviderCalls"] = "1",
        }, provider, time);
        using var capture = new Capture(api);
        using var client = api.CreateClientFor(TestApi.KeyA);
        try
        {
            var pending = TestApi.PostGenerate(client, TestApi.TestAlias, Marker);
            await TestApi.WaitUntil(() => provider.InFlight == 1, "provider running");
            time.Advance(TimeSpan.FromSeconds(1));
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
            capture.Listener.RecordObservableInstruments();
            Assert.Equal(0, capture.Measurements.Last(m => m.Name == "aicore.provider.permits_in_use").Value);
            Assert.Equal(1, capture.Measurements.Last(m => m.Name == "aicore.provider.abandoned").Value);
            provider.Release();
            await TestApi.WaitUntil(() => api.Invoker.AbandonedInFlight == 0, "orphan finished");
            capture.Listener.RecordObservableInstruments();
            Assert.Equal(0, capture.Measurements.Last(m => m.Name == "aicore.provider.abandoned").Value);
            AssertPrivate(api, capture);
        }
        finally { provider.Release(); }
    }

    [Fact]
    public async Task TenantReferences_AreStableWithinHost_AndDistinctAcrossTenantsAndHosts()
    {
        using var api = new TestApi(extraProvider: new RecordingProvider());
        using var a = api.CreateClientFor(TestApi.KeyA);
        using var b = api.CreateClientFor(TestApi.KeyB);
        using var r1 = await TestApi.PostGenerate(a, TestApi.TestAlias, "p");
        using var r2 = await TestApi.PostGenerate(a, TestApi.TestAlias, "p");
        using var r3 = await TestApi.PostGenerate(b, TestApi.TestAlias, "p");
        var refs = api.Logs.Entries.Where(e => e.Fields.ContainsKey("TenantRef")).Select(e => e.Fields["TenantRef"]).ToArray();
        Assert.Equal(refs[0], refs[1]);
        Assert.NotEqual(refs[0], refs[2]);
        using var other = new TestApi();
        Assert.NotEqual(refs[0], other.Services.GetRequiredService<AiCoreTelemetry>().TenantReference(TestApi.TenantA));
    }

    private static CapturingLoggerProvider.LogEntry Completion(TestApi api, string result) =>
        Assert.Single(api.Logs.Entries, e => e.Fields.GetValueOrDefault("Result") as string == result);

    private static void AssertPrivate(TestApi api, Capture capture)
    {
        foreach (var secret in new[] { Marker, TestApi.KeyA, TestApi.KeyB, TestApi.TenantA, TestApi.TenantB })
        {
            Assert.DoesNotContain(api.Logs.Entries, e => e.Contains(secret));
            Assert.DoesNotContain(capture.Spans, a => a.TagObjects.Any(t => $"{t.Key}={t.Value}".Contains(secret)) ||
                a.Events.Any(e => $"{e.Name} {string.Join(' ', e.Tags)}".Contains(secret)));
            Assert.DoesNotContain(capture.Measurements, m => m.Tags.Any(t => $"{t.Key}={t.Value}".Contains(secret)));
        }
        Assert.All(capture.Measurements, m => Assert.DoesNotContain(m.Tags.Keys,
            k => k.Contains("tenant") || k.Contains("correlation") || k.Contains("trace")));
    }

    // Filter by source instance so parallel test hosts cannot contaminate measurements or trace assertions.
    private sealed class Capture : IDisposable
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();
        public ConcurrentQueue<Measurement> Measurements { get; } = new();
        public MeterListener Listener { get; } = new();
        private readonly ActivityListener _activities;
        public Capture(TestApi api)
        {
            var telemetry = api.Services.GetRequiredService<AiCoreTelemetry>();
            _activities = new ActivityListener
            {
                ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => Spans.Enqueue(a),
            };
            ActivitySource.AddActivityListener(_activities);
            Listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, telemetry.Meter)) listener.EnableMeasurementEvents(instrument);
            };
            Listener.SetMeasurementEventCallback<long>((i, v, t, _) => Save(i, v, t));
            Listener.SetMeasurementEventCallback<int>((i, v, t, _) => Save(i, v, t));
            Listener.SetMeasurementEventCallback<double>((i, v, t, _) => Save(i, v, t));
            Listener.Start();
        }
        private void Save(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            Measurements.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
        public void Dispose() { _activities.Dispose(); Listener.Dispose(); }
    }

    private sealed record Measurement(string Name, double Value, Dictionary<string, object?> Tags);
}
