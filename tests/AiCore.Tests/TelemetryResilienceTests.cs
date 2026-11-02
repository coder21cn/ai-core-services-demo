using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using AiCore.Api.Observability;
using AiCore.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCore.Tests;

public sealed class TelemetryResilienceTests
{
    [Theory]
    [InlineData("aicore.queue.duration")]
    [InlineData("aicore.provider.duration")]
    [InlineData("aicore.provider.attempts")]
    [InlineData("aicore.requests")]
    public async Task ThrowingMeasurementListener_DoesNotFailRequestsOrLeakCapacity(string name)
    {
        var provider = new RecordingProvider();
        using var api = new TestApi(extraProvider: provider);
        var telemetry = api.Services.GetRequiredService<AiCoreTelemetry>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, telemetry.Meter) && instrument.Name == name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("broken exporter"));
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("broken exporter"));
        listener.Start();
        using var client = api.CreateClientFor(TestApi.KeyA);
        for (var i = 0; i < 2; i++)
        {
            using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "hello");
            Assert.Equal(api.Gate.PermitLimit, api.Gate.GetStatistics().CurrentAvailablePermits);
            Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantA).ActiveRequests);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(2, provider.Requests.Count);
    }

    [Theory]
    [InlineData("aicore.request", "sample")]
    [InlineData("aicore.request", "start")]
    [InlineData("aicore.request", "stop")]
    [InlineData("aicore.queue", "sample")]
    [InlineData("aicore.queue", "start")]
    [InlineData("aicore.queue", "stop")]
    [InlineData("aicore.provider", "sample")]
    [InlineData("aicore.provider", "start")]
    [InlineData("aicore.provider", "stop")]
    public async Task ThrowingActivityListener_DoesNotFailRequestsOrLeakCapacity(string name, string callback)
    {
        var provider = new RecordingProvider();
        using var api = new TestApi(extraProvider: provider);
        var telemetry = api.Services.GetRequiredService<AiCoreTelemetry>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name == name && callback == "sample" ? throw new InvalidOperationException("broken sampler") : ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => { if (activity.OperationName == name && callback == "start") throw new InvalidOperationException("broken start"); },
            ActivityStopped = activity => { if (activity.OperationName == name && callback == "stop") throw new InvalidOperationException("broken stop"); },
        };
        ActivitySource.AddActivityListener(listener);
        using var client = api.CreateClientFor(TestApi.KeyA);
        for (var i = 0; i < 2; i++)
        {
            using var response = await TestApi.PostGenerate(client, TestApi.TestAlias, "hello");
            Assert.Equal(api.Gate.PermitLimit, api.Gate.GetStatistics().CurrentAvailablePermits);
            Assert.Equal(0, api.Quotas.GetSnapshot(TestApi.TenantA).ActiveRequests);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task ThrowingCompletionLogger_DoesNotTurnSuccessIntoFailure()
    {
        using var telemetry = new AiCoreTelemetry(new Dictionary<string, AiCore.Api.Configuration.ModelRouteOptions>());
        var context = new DefaultHttpContext();
        var invoked = false;
        var middleware = new RequestTelemetryMiddleware(_ => { invoked = true; return Task.CompletedTask; }, new ThrowingLogger());
        await middleware.InvokeAsync(context, telemetry);
        Assert.True(invoked);
    }

    [Fact]
    public async Task ExceptionEscapingThePipeline_IsNeverRecordedAsSuccess()
    {
        using var telemetry = new AiCoreTelemetry(new Dictionary<string, AiCore.Api.Configuration.ModelRouteOptions>());
        using var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var context = new DefaultHttpContext();
        var middleware = new RequestTelemetryMiddleware(_ => throw new InvalidOperationException("private failure"),
            factory.CreateLogger<RequestTelemetryMiddleware>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context, telemetry));
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("internal_error", entry.Fields["Result"]);
        Assert.Equal(500, entry.Fields["StatusCode"]);
        Assert.False(entry.Contains("private failure"));
    }

    [Fact]
    public async Task DisconnectedResponseWrite_IsCancellation_NotAnErrorSpan()
    {
        using var telemetry = new AiCoreTelemetry(new Dictionary<string, AiCore.Api.Configuration.ModelRouteOptions>());
        Activity? completed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => completed = a,
        };
        ActivitySource.AddActivityListener(listener);
        using var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var context = new DefaultHttpContext { RequestAborted = new CancellationToken(canceled: true) };
        var middleware = new RequestTelemetryMiddleware(_ => throw new IOException("disconnected"),
            factory.CreateLogger<RequestTelemetryMiddleware>());
        await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(context, telemetry));
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("cancelled", entry.Fields["Result"]);
        Assert.Equal(499, entry.Fields["StatusCode"]);
        Assert.NotNull(completed);
        Assert.NotEqual(ActivityStatusCode.Error, completed.Status);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    public void ThrowingActivityCallbacks_RestoreTheAmbientParent(string callback)
    {
        using var parent = new Activity("parent").Start();
        using var telemetry = new AiCoreTelemetry(new Dictionary<string, AiCore.Api.Configuration.ModelRouteOptions>());
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => { if (callback == "start") throw new InvalidOperationException("broken start"); },
            ActivityStopped = _ => { if (callback == "stop") throw new InvalidOperationException("broken stop"); },
        };
        ActivitySource.AddActivityListener(listener);
        using (telemetry.StartActivity("probe")) { }
        Assert.Same(parent, Activity.Current);
        Assert.True(telemetry.EmissionFailures > 0);
    }

    [Fact]
    public async Task FailureAfterHeaders_PreservesWireStatusButMarksTheSpanAsError()
    {
        using var telemetry = new AiCoreTelemetry(new Dictionary<string, AiCore.Api.Configuration.ModelRouteOptions>());
        Activity? completed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Activities),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => completed = a,
        };
        ActivitySource.AddActivityListener(listener);
        using var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        var middleware = new RequestTelemetryMiddleware(_ => throw new IOException("private transfer failure"),
            factory.CreateLogger<RequestTelemetryMiddleware>());
        await Assert.ThrowsAsync<IOException>(() => middleware.InvokeAsync(context, telemetry));
        var entry = Assert.Single(logs.Entries);
        Assert.Equal("internal_error", entry.Fields["Result"]);
        Assert.Equal(200, entry.Fields["StatusCode"]);
        Assert.NotNull(completed);
        Assert.Equal(ActivityStatusCode.Error, completed.Status);
        Assert.False(entry.Contains("private transfer failure"));
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private sealed class ThrowingLogger : ILogger<RequestTelemetryMiddleware>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("broken sink");
    }
}
