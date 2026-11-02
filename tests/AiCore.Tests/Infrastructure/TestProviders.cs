using System.Collections.Concurrent;
using AiCore.Api.Providers;

namespace AiCore.Tests.Infrastructure;

/// <summary>Records every request it receives and returns a fixed result.</summary>
public sealed class RecordingProvider : IModelProvider
{
    public ConcurrentQueue<ProviderRequest> Requests { get; } = new();

    public string Name => "test";

    public Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new ProviderResult("recorded", 1, 1));
    }
}

/// <summary>Throws the supplied exception on every call.</summary>
public sealed class ThrowingProvider(Func<Exception> exception) : IModelProvider
{
    public string Name => "test";

    public Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken) =>
        throw exception();
}

/// <summary>
/// Blocks every call on a gate the test controls, and tracks the number of calls in flight, so the
/// concurrency bound can be asserted deterministically rather than by timing.
/// </summary>
public sealed class GatedProvider : IModelProvider
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private int _inFlight;
    private int _maxInFlight;
    private int _calls;

    public string Name => "test";

    public int InFlight { get { lock (_lock) { return _inFlight; } } }

    public int MaxInFlight { get { lock (_lock) { return _maxInFlight; } } }

    public int Calls { get { lock (_lock) { return _calls; } } }

    public void Release() => _gate.TrySetResult();

    public async Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls++;
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
        }

        try
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return new ProviderResult("gated", 1, 1);
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
            }
        }
    }
}

/// <summary>
/// Runs a test-supplied function for every call (which may return or throw) and records each request,
/// including its attempt number, so retry behaviour can be asserted exactly.
/// </summary>
public sealed class ScriptedProvider(Func<ProviderRequest, ProviderResult> behaviour) : IModelProvider
{
    public ConcurrentQueue<ProviderRequest> Requests { get; } = new();

    public string Name => "test";

    public int Calls => Requests.Count;

    public Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(behaviour(request));
    }

    public static ProviderResult Ok(ProviderRequest _) => new("scripted", 1, 1);

    public static ProviderResult Throttled(ProviderRequest _) =>
        throw new ProviderTransientException("upstream throttled; not processed", requestNotProcessed: true);
}

/// <summary>
/// A provider whose reaction to cancellation is controlled by the test:
/// <list type="bullet">
/// <item><c>honourCancellation: true</c>: on cancellation it waits for <see cref="AllowStop"/> before throwing,
/// imitating an adapter that needs time to unwind (e.g. closing a stream).</item>
/// <item><c>honourCancellation: false</c>: it ignores the token and finishes only on <see cref="Release"/>,
/// imitating a defective adapter.</item>
/// </list>
/// </summary>
public sealed class CancellationProbeProvider(bool honourCancellation, Func<Exception>? failOnStop = null) : IModelProvider
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;
    private int _calls;
    private volatile bool _cancellationObserved;

    public string Name => "test";

    public int InFlight => Volatile.Read(ref _inFlight);

    public int Calls => Volatile.Read(ref _calls);

    public bool CancellationObserved => _cancellationObserved;

    public void Release() => _release.TrySetResult();

    public void AllowStop() => _stop.TrySetResult();

    public async Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Increment(ref _inFlight);
        try
        {
            await using var registration = cancellationToken.Register(() => _cancellationObserved = true);
            if (!honourCancellation)
            {
                await _release.Task;
                return new ProviderResult("late", 1, 1);
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return new ProviderResult("probe", 1, 1);
            }
            catch (OperationCanceledException)
            {
                await _stop.Task;
                if (failOnStop is null)
                {
                    throw;
                }

                // An adapter that turns a late cancellation into its own failure (e.g. "content rejected").
                throw failOnStop();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>
/// Blocks the calling thread <i>before</i> returning a task, as sync-over-async inside a vendor SDK or a
/// blocking DNS/TLS handshake would. Ignores its cancellation token.
/// </summary>
public sealed class BlockingProvider(ManualResetEventSlim unblock) : IModelProvider
{
    private volatile bool _entered;

    public string Name => "test";

    public bool Entered => _entered;

    public Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        _entered = true;
        unblock.Wait();
        return Task.FromResult(new ProviderResult("blocked", 1, 1));
    }
}
