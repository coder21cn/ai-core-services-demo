using AiCore.Api.Configuration;
using AiCore.Api.Execution;
using AiCore.Api.Providers;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests;

/// <summary>Unit tests of deadline, retry and cancellation edge cases in the provider phase (FR-06, FR-10).</summary>
public sealed class ProviderInvokerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private readonly FakeTimeProvider _time = new();

    private ProviderInvoker Create(int timeoutMs = 1_000, int maxAttempts = 3, int backoffMs = 0, int graceMs = 1_000) =>
        new(new ResilienceOptions
        {
            ProviderTimeoutMs = timeoutMs,
            MaxAttempts = maxAttempts,
            RetryBackoffMs = backoffMs,
            CancellationGraceMs = graceMs,
        }, _time, NullLogger<ProviderInvoker>.Instance);

    private static ProviderRequest Request() => new("corr", "tenant", "m", "prompt", 10);

    [Fact]
    public async Task SynchronouslyThrowingAdapter_IsHandledLikeAnAsyncFailure()
    {
        var result = await Create().InvokeAsync(
            new ThrowingProvider(() => new ProviderPermanentException("bad")), Request(), CancellationToken.None);

        Assert.Equal(ProviderCallStatus.PermanentFailure, result.Status);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task UnexpectedException_KeepsOnlyItsType()
    {
        var result = await Create().InvokeAsync(
            new ThrowingProvider(() => new InvalidOperationException("secret detail")), Request(), CancellationToken.None);

        Assert.Equal(ProviderCallStatus.UnexpectedFailure, result.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.ExceptionType);
        Assert.Null(result.SafeMessage);
    }

    [Fact]
    public async Task AdapterOwnCancellation_IsUnexpected_NotATimeoutOrClientAbort()
    {
        // e.g. an HTTP client's internal timeout surfacing as TaskCanceledException while our token is live.
        var result = await Create().InvokeAsync(
            new ThrowingProvider(() => new TaskCanceledException("http client timeout")), Request(), CancellationToken.None);

        Assert.Equal(ProviderCallStatus.UnexpectedFailure, result.Status);
    }

    [Fact]
    public async Task Backoff_UsesTheInjectedClock_BeforeTheNextAttempt()
    {
        var provider = new ScriptedProvider(r => r.Attempt == 1 ? ScriptedProvider.Throttled(r) : ScriptedProvider.Ok(r));
        var start = _time.GetUtcNow();
        var invocation = Create(timeoutMs: 10_000, backoffMs: 400).InvokeAsync(provider, Request(), CancellationToken.None);

        await TestApi.WaitUntil(() => provider.Calls == 1, "first attempt made");
        Assert.False(invocation.IsCompleted); // waiting for the backoff; fake time has not moved

        await TestApi.AdvanceUntil(_time, TimeSpan.FromMilliseconds(100), () => invocation.IsCompleted, "retry after backoff");
        var result = await invocation;

        Assert.Equal(ProviderCallStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.True(_time.GetUtcNow() - start >= TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task DeadlineDuringBackoff_IsReportedAsNotProcessedTransientFailure_NotAsTimeout()
    {
        // Every attempt so far was rejected before processing, so nothing can have been billed: a 504
        // "may have been processed" would be misleading.
        // Attempt 1 at t=0, attempt 2 at t=600, second backoff due at t=1200 but the deadline is t=1000.
        var provider = new ScriptedProvider(ScriptedProvider.Throttled);
        var invocation = Create(timeoutMs: 1_000, backoffMs: 600).InvokeAsync(provider, Request(), CancellationToken.None);

        await TestApi.WaitUntil(() => provider.Calls == 1, "first attempt made");
        await TestApi.AdvanceUntil(_time, TimeSpan.FromMilliseconds(250), () => invocation.IsCompleted, "deadline reached");
        var result = await invocation;

        Assert.Equal(ProviderCallStatus.TransientFailure, result.Status);
        Assert.True(result.RequestNotProcessed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, provider.Calls); // the third attempt (MaxAttempts = 3) never started
    }

    [Fact]
    public async Task ClientCancellation_PropagatesAsOperationCanceled_AfterTheProviderStops()
    {
        var provider = new CancellationProbeProvider(honourCancellation: true);
        using var client = new CancellationTokenSource();
        var invocation = Create().InvokeAsync(provider, Request(), client.Token);
        await TestApi.WaitUntil(() => provider.InFlight == 1, "call in flight");

        await client.CancelAsync();
        await TestApi.WaitUntil(() => provider.CancellationObserved, "provider asked to stop");
        Assert.False(invocation.IsCompleted); // still waiting for the provider to stop

        provider.AllowStop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(Deadline));
        Assert.Equal(0, provider.InFlight);
    }

    [Fact]
    public async Task ZeroGrace_AbandonsANonStoppingCallImmediately()
    {
        var provider = new CancellationProbeProvider(honourCancellation: false);
        var invoker = Create(timeoutMs: 1_000, graceMs: 0);
        var invocation = invoker.InvokeAsync(provider, Request(), CancellationToken.None);
        await TestApi.WaitUntil(() => provider.InFlight == 1, "call in flight");

        _time.Advance(TimeSpan.FromSeconds(1));
        var result = await invocation.WaitAsync(Deadline);

        Assert.Equal(ProviderCallStatus.TimedOut, result.Status);
        Assert.Equal(1, invoker.AbandonedInFlight);
        provider.Release();
        await TestApi.WaitUntil(() => invoker.AbandonedInFlight == 0, "abandoned call completed");
    }

    [Fact]
    public async Task AdapterThatBlocksBeforeReturningATask_CannotHoldTheCallerPastDeadlineAndGrace()
    {
        using var unblock = new ManualResetEventSlim();
        var provider = new BlockingProvider(unblock);
        var invoker = Create(timeoutMs: 1_000, graceMs: 500);
        // Task.Run only so that a regression blocks a pool thread rather than this test's thread.
        var invocation = Task.Run(() => invoker.InvokeAsync(provider, Request(), CancellationToken.None));
        try
        {
            await TestApi.WaitUntil(() => provider.Entered, "adapter is blocking");
            _time.Advance(TimeSpan.FromSeconds(1));
            await TestApi.AdvanceUntil(_time, TimeSpan.FromMilliseconds(250), () => invocation.IsCompleted, "deadline enforced");

            var result = await invocation;
            Assert.Equal(ProviderCallStatus.TimedOut, result.Status);
            Assert.Equal(1, invoker.AbandonedInFlight);
        }
        finally
        {
            unblock.Set();
        }

        await TestApi.WaitUntil(() => invoker.AbandonedInFlight == 0, "blocked call finished");
    }

    [Fact]
    public async Task PermanentFailureDuringGrace_IsReportedAsPermanent_NotAsTimeout()
    {
        var provider = new CancellationProbeProvider(
            honourCancellation: true, failOnStop: () => new ProviderPermanentException("content rejected"));
        var invocation = Create(timeoutMs: 1_000).InvokeAsync(provider, Request(), CancellationToken.None);
        await TestApi.WaitUntil(() => provider.InFlight == 1, "call in flight");

        _time.Advance(TimeSpan.FromSeconds(1));
        await TestApi.WaitUntil(() => provider.CancellationObserved, "provider asked to stop");
        provider.AllowStop(); // within the grace period, the adapter reports a permanent failure

        var result = await invocation.WaitAsync(Deadline);
        Assert.Equal(ProviderCallStatus.PermanentFailure, result.Status);
        Assert.Equal("content rejected", result.SafeMessage);
    }

    [Fact]
    public async Task NotProcessedFailureDuringGrace_StaysASafeTransientFailure_NotATimeout()
    {
        var provider = new CancellationProbeProvider(
            honourCancellation: true,
            failOnStop: () => new ProviderTransientException("throttled before processing", requestNotProcessed: true));
        var invocation = Create(timeoutMs: 1_000, maxAttempts: 1).InvokeAsync(provider, Request(), CancellationToken.None);
        await TestApi.WaitUntil(() => provider.InFlight == 1, "call in flight");

        _time.Advance(TimeSpan.FromSeconds(1));
        await TestApi.WaitUntil(() => provider.CancellationObserved, "provider asked to stop");
        provider.AllowStop();

        var result = await invocation.WaitAsync(Deadline);
        Assert.Equal(ProviderCallStatus.TransientFailure, result.Status);
        Assert.True(result.RequestNotProcessed); // 503 with "nothing billed", not an ambiguous 504
    }

    [Fact]
    public async Task Retry_IsNotSentToARouteThatReachedItsAbandonedCallLimit()
    {
        var invoker = new ProviderInvoker(new ResilienceOptions
        {
            ProviderTimeoutMs = 1_000, MaxAttempts = 3, RetryBackoffMs = 0, CancellationGraceMs = 0, MaxAbandonedCallsPerRoute = 1,
        }, _time, NullLogger<ProviderInvoker>.Instance);

        // One call to route test/m is abandoned: the route is now at its limit.
        var hanging = new CancellationProbeProvider(honourCancellation: false);
        var first = invoker.InvokeAsync(hanging, Request(), CancellationToken.None);
        await TestApi.WaitUntil(() => hanging.InFlight == 1, "call in flight");
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ProviderCallStatus.TimedOut, (await first.WaitAsync(Deadline)).Status);

        // A request already past admission gets a "not processed" failure on attempt 1. Its retry must not be sent to
        // the refused route; nothing was processed, so the result is a safe transient failure.
        var throttledOnce = new ScriptedProvider(r => r.Attempt == 1 ? ScriptedProvider.Throttled(r) : ScriptedProvider.Ok(r));
        var result = await invoker.InvokeAsync(throttledOnce, Request(), CancellationToken.None).WaitAsync(Deadline);

        Assert.Equal(ProviderCallStatus.TransientFailure, result.Status);
        Assert.True(result.RequestNotProcessed);
        Assert.Equal(1, throttledOnce.Calls);

        hanging.Release();
        await TestApi.WaitUntil(() => invoker.AbandonedInFlight == 0, "abandoned call ended");
    }
}

