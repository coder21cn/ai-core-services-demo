using System.Threading.RateLimiting;
using AiCore.Api.Configuration;

namespace AiCore.Api.Execution;

/// <summary>Result of <see cref="ProviderConcurrencyGate.AcquireAsync"/>. <see cref="Lease"/> is null when rejected.</summary>
public sealed record GateAcquisition(RateLimitLease? Lease, bool QueueWaitTimedOut);

/// <summary>
/// Process-wide bound on simultaneous provider calls (FR-05), built on the BCL
/// <see cref="ConcurrencyLimiter"/>:
/// <list type="bullet">
/// <item>At most <c>MaxConcurrentProviderCalls</c> leases exist at any time.</item>
/// <item>At most <c>MaxQueuedRequests</c> callers wait (FIFO); further callers are rejected immediately,
/// so there is no unbounded work queue.</item>
/// <item>No caller waits longer than <c>MaxQueueWaitMs</c>; it is then removed from the queue and rejected.</item>
/// <item>A waiting caller whose token is cancelled (client disconnect) leaves the queue.</item>
/// </list>
/// Invariant: every acquired lease is disposed by the caller on every path (a <c>using</c> declaration), so
/// provider success, failure, exception, timeout or cancellation all return the permit. Provider calls are
/// themselves bounded in time by <see cref="ProviderInvoker"/>, so a permit can't be held indefinitely.
/// </summary>
public sealed class ProviderConcurrencyGate : IDisposable
{
    private readonly ConcurrencyLimiter _limiter;
    private readonly TimeSpan _maxQueueWait;
    private readonly TimeProvider _timeProvider;
    private long _queueFullRejections;
    private long _queueWaitTimeouts;

    public ProviderConcurrencyGate(ConcurrencyOptions options, TimeProvider timeProvider)
    {
        PermitLimit = options.MaxConcurrentProviderCalls;
        QueueLimit = options.MaxQueuedRequests;
        _maxQueueWait = TimeSpan.FromMilliseconds(options.MaxQueueWaitMs);
        _timeProvider = timeProvider;
        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = PermitLimit,
            QueueLimit = QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    public int PermitLimit { get; }

    public int QueueLimit { get; }

    /// <summary>
    /// Acquires a permit, waiting in the bounded queue for at most <c>MaxQueueWaitMs</c>. Returns a rejection
    /// (null lease) when the queue is full or the wait timed out. Throws <see cref="OperationCanceledException"/>
    /// only when <paramref name="cancellationToken"/> (the client) cancels. Always dispose a returned lease.
    /// </summary>
    public async ValueTask<GateAcquisition> AcquireAsync(CancellationToken cancellationToken)
    {
        // Fast path: a free permit is taken without allocating the queue-timeout timer. With OldestFirst
        // ordering, AttemptAcquire fails while anyone is queued, so this can't jump the queue.
        var immediate = _limiter.AttemptAcquire(permitCount: 1);
        if (immediate.IsAcquired)
        {
            return new GateAcquisition(immediate, QueueWaitTimedOut: false);
        }

        immediate.Dispose();

        using var queueTimeout = new CancellationTokenSource(_maxQueueWait, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, queueTimeout.Token);

        RateLimitLease lease;
        try
        {
            lease = await _limiter.AcquireAsync(permitCount: 1, linked.Token);
        }
        catch (OperationCanceledException) when (queueTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The limiter has already removed this waiter from the queue.
            Interlocked.Increment(ref _queueWaitTimeouts);
            return new GateAcquisition(null, QueueWaitTimedOut: true);
        }

        if (!lease.IsAcquired)
        {
            lease.Dispose();
            Interlocked.Increment(ref _queueFullRejections);
            return new GateAcquisition(null, QueueWaitTimedOut: false);
        }

        return new GateAcquisition(lease, QueueWaitTimedOut: false);
    }

    /// <summary>Requests rejected because every permit and queue slot was taken (503 capacity_exhausted).</summary>
    public long QueueFullRejections => Interlocked.Read(ref _queueFullRejections);

    /// <summary>Requests rejected after waiting <c>MaxQueueWaitMs</c> without a permit (503 capacity_exhausted).</summary>
    public long QueueWaitTimeouts => Interlocked.Read(ref _queueWaitTimeouts);

    /// <summary>
    /// Snapshot of available permits and queue depth, for diagnostics and tests. Count rejections with
    /// <see cref="QueueFullRejections"/> and <see cref="QueueWaitTimeouts"/>, not <c>TotalFailedLeases</c>: the
    /// limiter also counts a fast-path miss as a failed lease, even when that request then queues and succeeds.
    /// </summary>
    public RateLimiterStatistics GetStatistics() => _limiter.GetStatistics()!;

    public void Dispose() => _limiter.Dispose();
}
