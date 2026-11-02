using AiCore.Api.Providers;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests;

/// <summary>FR-02: the mock is deterministic, offline and supports latency, failures and cancellation.</summary>
public sealed class MockModelProviderTests
{
    private readonly FakeTimeProvider _time = new();

    private MockModelProvider Create(MockBehavior behavior = MockBehavior.Success, int latencyMs = 0, bool honourCancellation = true) =>
        new(new MockProviderOptions
        {
            Models =
            {
                ["m"] = new MockModelOptions { Behavior = behavior, LatencyMs = latencyMs, HonourCancellation = honourCancellation },
            },
        }, _time);

    private static ProviderRequest Request(string prompt = "hello world", int maxOutputTokens = 256, string model = "m", int attempt = 1) =>
        new("corr", "tenant-a", model, prompt, maxOutputTokens, attempt);

    [Fact]
    public async Task Success_IsDeterministic_AndDependsOnInput()
    {
        var provider = Create();

        var a1 = await provider.GenerateAsync(Request("alpha"), CancellationToken.None);
        var a2 = await provider.GenerateAsync(Request("alpha"), CancellationToken.None);
        var b = await provider.GenerateAsync(Request("bravo"), CancellationToken.None);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1.Output, b.Output);
        Assert.Equal(2, a1.InputTokens); // ceil(5 / 4)
        Assert.DoesNotContain("alpha", a1.Output); // prompt is not reflected
    }

    [Fact]
    public async Task Output_IsBoundedByMaxOutputTokens()
    {
        var result = await Create().GenerateAsync(Request(maxOutputTokens: 5), CancellationToken.None);

        Assert.True(result.Output.Length <= 20, $"Output length {result.Output.Length} exceeds 5 tokens * 4 chars");
        Assert.True(result.OutputTokens <= 5);
    }

    [Fact]
    public async Task Latency_IsSimulatedWithInjectedClock()
    {
        var task = Create(latencyMs: 500).GenerateAsync(Request(), CancellationToken.None);

        _time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(task.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(result.Output);
    }

    [Fact]
    public async Task Cancellation_DuringLatency_StopsTheCall()
    {
        using var cts = new CancellationTokenSource();
        var task = Create(latencyMs: 10_000).GenerateAsync(Request(), cts.Token);
        Assert.False(task.IsCompleted);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AlreadyCancelledToken_IsHonouredWithoutLatency()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create().GenerateAsync(Request(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task TransientFailure_ThrowsTransientException()
    {
        await Assert.ThrowsAsync<ProviderTransientException>(
            () => Create(MockBehavior.TransientFailure).GenerateAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task PermanentFailure_ThrowsPermanentException()
    {
        await Assert.ThrowsAsync<ProviderPermanentException>(
            () => Create(MockBehavior.PermanentFailure).GenerateAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownProviderModel_IsPermanent()
    {
        await Assert.ThrowsAsync<ProviderPermanentException>(
            () => Create().GenerateAsync(Request(model: "nope"), CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Throttled_IsTransient_AndMarkedNotProcessed_OnEveryAttempt(int attempt)
    {
        var ex = await Assert.ThrowsAsync<ProviderTransientException>(
            () => Create(MockBehavior.Throttled).GenerateAsync(Request(attempt: attempt), CancellationToken.None));

        Assert.True(ex.RequestNotProcessed);
    }

    [Fact]
    public async Task ThrottledOnFirstAttempt_SucceedsOnTheRetry()
    {
        var provider = Create(MockBehavior.ThrottledOnFirstAttempt);

        var first = await Assert.ThrowsAsync<ProviderTransientException>(
            () => provider.GenerateAsync(Request(attempt: 1), CancellationToken.None));
        var second = await provider.GenerateAsync(Request(attempt: 2), CancellationToken.None);

        Assert.True(first.RequestNotProcessed);
        Assert.NotEmpty(second.Output);
    }

    [Fact]
    public async Task TransientFailure_IsAmbiguous_SoNotMarkedNotProcessed()
    {
        var ex = await Assert.ThrowsAsync<ProviderTransientException>(
            () => Create(MockBehavior.TransientFailure).GenerateAsync(Request(), CancellationToken.None));

        Assert.False(ex.RequestNotProcessed);
    }

    [Fact]
    public async Task HonourCancellationFalse_IgnoresTheToken_UntilLatencyElapses()
    {
        using var cts = new CancellationTokenSource();
        var task = Create(latencyMs: 1_000, honourCancellation: false).GenerateAsync(Request(), cts.Token);

        await cts.CancelAsync();
        await Task.Delay(50); // give an (incorrect) cancellation a chance to complete the task
        Assert.False(task.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1_000));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(result.Output);
    }
}
