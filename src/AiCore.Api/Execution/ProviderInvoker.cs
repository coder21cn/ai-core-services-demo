using AiCore.Api.Configuration;
using AiCore.Api.Providers;
using AiCore.Api.Observability;

namespace AiCore.Api.Execution;

public enum ProviderCallStatus
{
    Succeeded,
    TimedOut,
    TransientFailure,
    PermanentFailure,
    UnexpectedFailure,
}

/// <summary>Outcome of the provider phase. Never carries an unexpected exception's message (it may hold request data).</summary>
/// <param name="Attempts">Provider calls made for this request, including the first.</param>
/// <param name="SafeMessage">Adapter-authored message of a <see cref="ProviderException"/> (safe by contract).</param>
/// <param name="ExceptionType">Type name of an unexpected exception; its message is deliberately dropped.</param>
/// <param name="RequestNotProcessed">For transient failures: the provider guaranteed no work was done.</param>
public sealed record ProviderCallResult(
    ProviderCallStatus Status,
    int Attempts,
    ProviderResult? Result = null,
    string? SafeMessage = null,
    string? ExceptionType = null,
    bool RequestNotProcessed = false);

/// <summary>
/// Executes the provider phase of a request: deadline, retry policy and cancellation (FR-06, FR-10).
/// <list type="bullet">
/// <item><b>Deadline.</b> <c>ProviderTimeoutMs</c> bounds all attempts and backoff together. When it expires the
/// provider's token is cancelled and the result is <see cref="ProviderCallStatus.TimedOut"/> (never retried: the
/// provider may have completed, and billed, the work).</item>
/// <item><b>Retry.</b> Only a <see cref="ProviderTransientException"/> with <c>RequestNotProcessed</c> is retried,
/// up to <c>MaxAttempts</c> in total, after a fixed backoff. Ambiguous transient failures, permanent failures,
/// timeouts and unexpected exceptions are returned after one attempt.</item>
/// <item><b>Cancellation.</b> Client cancellation propagates as <see cref="OperationCanceledException"/>.</item>
/// <item><b>Stopping.</b> After cancelling a call (timeout or client abort) the invoker waits up to
/// <c>CancellationGraceMs</c> for the adapter to stop, so the caller's permit keeps counting the call until it
/// has really ended and the in-flight bound stays exact. If the call completes within the grace period its real
/// outcome is used: a success is returned (it exists and was billed; a 504 would invite a paid duplicate), a
/// "not processed" failure stays a safe 503, and a permanent failure stays a 502. Anything else is a timeout.</item>
/// <item><b>Abandoning.</b> An adapter that ignores cancellation past the grace period is <i>abandoned</i>: logged
/// as an error, counted against its <b>route</b> (provider + provider model) and, through <c>onAbandoned</c>, against
/// its tenant on that route only, and its global permit and tenant slot released when this method returns. While a route has <c>MaxAbandonedCallsPerRoute</c> abandoned calls running,
/// <see cref="HasTooManyAbandonedCalls"/> is true and the caller refuses new work for that route with 503 (checked
/// both before queueing and after a permit is acquired), so orphans per route stay below
/// <c>MaxAbandonedCallsPerRoute + MaxConcurrentProviderCalls</c>. Reaching and leaving the limit are each logged
/// once. That trade (liveness over an exact global bound for a defective adapter) keeps a hung provider from
/// holding permits indefinitely (NFR-05) while capping the orphans, without blaming tenants or blocking other
/// models.</item>
/// <item><b>Isolation from blocking adapters.</b> Each attempt starts on the thread pool, so an adapter that blocks
/// before returning its task (sync-over-async in an SDK, a blocking DNS or TLS handshake) cannot stop the deadline
/// from being enforced. It still occupies a pool thread while blocked.</item>
/// </list>
/// Worst case, this method returns within <c>ProviderTimeoutMs + CancellationGraceMs</c>.
/// </summary>
public sealed class ProviderInvoker(ResilienceOptions options, TimeProvider timeProvider, ILogger<ProviderInvoker> logger,
    AiCoreTelemetry? telemetry = null)
{
    private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(options.ProviderTimeoutMs);
    private readonly TimeSpan _grace = TimeSpan.FromMilliseconds(options.CancellationGraceMs);
    private readonly TimeSpan _backoff = TimeSpan.FromMilliseconds(options.RetryBackoffMs);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _abandonedByRoute =
        new(StringComparer.OrdinalIgnoreCase);
    private int _abandonedInFlight;

    /// <summary>Provider calls that ignored cancellation past the grace period and have not completed yet.</summary>
    public int AbandonedInFlight => Volatile.Read(ref _abandonedInFlight);

    /// <summary>Abandoned calls of one route (<see cref="ProviderRoutes.Key"/>) that are still running.</summary>
    public int AbandonedInFlightFor(string route) =>
        _abandonedByRoute.TryGetValue(route, out var count) ? count : 0;

    /// <summary>
    /// True while the route has at least <c>MaxAbandonedCallsPerRoute</c> abandoned calls still running. New calls
    /// to it, including retries, are then refused, so a model that hangs can't accumulate unbounded orphans. Other
    /// models of the same provider are unaffected.
    /// </summary>
    public bool HasTooManyAbandonedCalls(string route) =>
        AbandonedInFlightFor(route) >= options.MaxAbandonedCallsPerRoute;


    public TimeSpan Timeout => _timeout;

    /// <param name="onAbandoned">
    /// Called synchronously, before this method returns or throws, with the still-running task of an abandoned
    /// call. The caller uses it to keep counting the call against its tenant on this route until it ends.
    /// </param>
    public async Task<ProviderCallResult> InvokeAsync(
        IModelProvider provider, ProviderRequest request, CancellationToken cancellationToken, Action<Task>? onAbandoned = null)
    {
        // A client that is already gone gets no provider call: the attempt could be billed with nobody to receive it.
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = new CancellationTokenSource(_timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        var route = ProviderRoutes.Key(provider.Name, request.ProviderModel);
        string? notProcessedReason = null;

        for (var attempt = 1; ; attempt++)
        {
            // Before a retry: no attempt starts after the deadline or a client abort (backoff can finish in the same
            // instant), and none goes to a route that reached its abandoned-call limit meanwhile. Every earlier
            // attempt was "not processed", so the outcome is a safe transient failure, not an ambiguous timeout.
            if (attempt > 1 && (token.IsCancellationRequested || HasTooManyAbandonedCalls(route)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ProviderCallResult(
                    ProviderCallStatus.TransientFailure, attempt - 1, SafeMessage: notProcessedReason, RequestNotProcessed: true);
            }

            var attemptRequest = request with { Attempt = attempt };
            telemetry?.RecordAttempt(provider.Name, attempt);
            var call = Start(provider, attemptRequest, token);
            try
            {
                // WaitAsync returns on cancellation even if the adapter ignores its token.
                var result = await call.WaitAsync(token);
                return new ProviderCallResult(ProviderCallStatus.Succeeded, attempt, result);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                var stopped = await LetCallStopAsync(call, provider, attemptRequest, onAbandoned);
                cancellationToken.ThrowIfCancellationRequested(); // client abort: not a service failure

                if (stopped && call.IsCompletedSuccessfully)
                {
                    logger.LogWarning(
                        "Provider {Provider} completed attempt {Attempt} after the deadline, within the cancellation grace period; returning its result (correlation {CorrelationId})",
                        provider.Name, attempt, request.CorrelationId);
                    return new ProviderCallResult(ProviderCallStatus.Succeeded, attempt, call.Result);
                }

                if (stopped && call.Exception?.InnerException is ProviderTransientException { RequestNotProcessed: true } notProcessed)
                {
                    // Known not processed, so nothing was billed: a safe 503 is more accurate than an ambiguous 504.
                    return new ProviderCallResult(
                        ProviderCallStatus.TransientFailure, attempt, SafeMessage: notProcessed.Message, RequestNotProcessed: true);
                }

                if (stopped && call.Exception?.InnerException is ProviderPermanentException permanent)
                {
                    // "Retrying won't help" (502) is more useful to the caller than an ambiguous timeout.
                    return new ProviderCallResult(ProviderCallStatus.PermanentFailure, attempt, SafeMessage: permanent.Message);
                }

                return new ProviderCallResult(ProviderCallStatus.TimedOut, attempt);
            }
            catch (ProviderTransientException ex) when (ex.RequestNotProcessed && attempt < options.MaxAttempts)
            {
                notProcessedReason = ex.Message;
                logger.LogInformation(
                    "Provider {Provider} did not process attempt {Attempt} of {MaxAttempts}; retrying after {BackoffMs} ms (correlation {CorrelationId})",
                    provider.Name, attempt, options.MaxAttempts, options.RetryBackoffMs, request.CorrelationId);

                if (!await BackoffAsync(token))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // The deadline passed during backoff. Every attempt so far was rejected before processing, so
                    // report a safe transient failure rather than an ambiguous timeout.
                    return new ProviderCallResult(
                        ProviderCallStatus.TransientFailure, attempt, SafeMessage: ex.Message, RequestNotProcessed: true);
                }
            }
            catch (ProviderTransientException ex)
            {
                return new ProviderCallResult(
                    ProviderCallStatus.TransientFailure, attempt, SafeMessage: ex.Message, RequestNotProcessed: ex.RequestNotProcessed);
            }
            catch (ProviderPermanentException ex)
            {
                return new ProviderCallResult(ProviderCallStatus.PermanentFailure, attempt, SafeMessage: ex.Message);
            }
            catch (Exception ex)
            {
                // Includes an OperationCanceledException the adapter raised on its own (e.g. an HTTP client's
                // internal timeout) while our token was not cancelled. Only the type is kept: exceptions from
                // vendor SDKs or HTTP clients may carry request content in their message.
                return new ProviderCallResult(ProviderCallStatus.UnexpectedFailure, attempt, ExceptionType: ex.GetType().FullName);
            }
        }
    }

    /// <summary>
    /// Starts an attempt on the thread pool. An adapter that blocks before returning its task therefore can't
    /// block this method, and a synchronous throw becomes a faulted task, so all failures take one path. The
    /// cost is one thread-pool hop per attempt.
    /// </summary>
    private static Task<ProviderResult> Start(IModelProvider provider, ProviderRequest request, CancellationToken token) =>
        Task.Run(() => provider.GenerateAsync(request, token), CancellationToken.None);

    private async Task<bool> BackoffAsync(CancellationToken token)
    {
        if (_backoff == TimeSpan.Zero)
        {
            return !token.IsCancellationRequested;
        }

        try
        {
            await Task.Delay(_backoff, timeProvider, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Waits up to the grace period for a cancelled call to stop. Returns false if it was abandoned.</summary>
    private async Task<bool> LetCallStopAsync(
        Task<ProviderResult> call, IModelProvider provider, ProviderRequest request, Action<Task>? onAbandoned)
    {
        if (!call.IsCompleted)
        {
            using var graceCancellation = new CancellationTokenSource();
            var grace = Task.Delay(_grace, timeProvider, graceCancellation.Token);
            var first = await Task.WhenAny(call, grace);
            await graceCancellation.CancelAsync();

            if (first != call)
            {
                Abandon(call, provider, request, onAbandoned);
                return false;
            }
        }

        _ = call.Exception; // observe the (expected) cancellation or failure
        return true;
    }

    private void Abandon(Task<ProviderResult> call, IModelProvider provider, ProviderRequest request, Action<Task>? onAbandoned)
    {
        var route = ProviderRoutes.Key(provider.Name, request.ProviderModel);
        var limit = options.MaxAbandonedCallsPerRoute;
        var abandoned = Interlocked.Increment(ref _abandonedInFlight);
        var forRoute = _abandonedByRoute.AddOrUpdate(route, 1, static (_, n) => n + 1);
        logger.LogError(
            "Provider {Provider} did not stop within {GraceMs} ms of cancellation (route {Route}, attempt {Attempt}, correlation {CorrelationId}); " +
            "the call is abandoned and its permit released. Abandoned calls still running: {AbandonedForRoute} on this route " +
            "(limit {MaxAbandoned}), {AbandonedInFlight} in total",
            provider.Name, options.CancellationGraceMs, route, request.Attempt, request.CorrelationId, forRoute, limit, abandoned);

        // Logged on the transitions only: refused requests are logged at Debug, so a looping client can't flood the
        // log during the incident.
        if (forRoute == limit)
        {
            logger.LogWarning(
                "Route {Route} reached its limit of {MaxAbandoned} abandoned calls; refusing new calls to it with 503 until they end",
                route, limit);
        }

        _ = call.ContinueWith(
            (completed, state) =>
            {
                _ = completed.Exception; // observed, so it is not reported as an unobserved task exception
                var self = (ProviderInvoker)state!;
                var remaining = self._abandonedByRoute.AddOrUpdate(route, 0, static (_, n) => n - 1);
                Interlocked.Decrement(ref self._abandonedInFlight);
                if (remaining == limit - 1)
                {
                    logger.LogInformation(
                        "Route {Route} is below its abandoned-call limit again ({Remaining} still running); accepting new calls",
                        route, remaining);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        onAbandoned?.Invoke(call);
    }
}
