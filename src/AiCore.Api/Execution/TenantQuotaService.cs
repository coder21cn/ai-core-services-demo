using System.Collections.Concurrent;
using AiCore.Api.Configuration;

namespace AiCore.Api.Execution;

public enum QuotaRejection
{
    None,

    /// <summary>The tenant's request-rate bucket is empty.</summary>
    RequestRate,

    /// <summary>The tenant already has its maximum number of requests queued or in flight.</summary>
    Concurrency,

    /// <summary>
    /// The tenant's abandoned calls on the requested route, plus its requests in progress, reach
    /// <c>MaxConcurrentRequests</c>. Answered with 503 for that route only: the cause is the route's adapter
    /// ignoring cancellation, and the tenant stays free to use every other route.
    /// </summary>
    AbandonedCalls,
}

/// <summary>Result of <see cref="TenantQuotaService.TryAdmit"/>: a lease, or a rejection with a retry hint.</summary>
public sealed record QuotaAdmission(TenantQuotaLease? Lease, QuotaRejection Rejection, TimeSpan RetryAfter)
{
    public bool IsAdmitted => Lease is not null;
}

/// <summary>Point-in-time view of one tenant's quota state, for diagnostics and tests.</summary>
public sealed record TenantQuotaSnapshot(int ActiveRequests, double AvailableRequestTokens);

/// <summary>
/// Per-tenant admission control (FR-04), checked before a request competes for shared provider capacity.
/// Two independent limits apply to each authenticated tenant:
/// <list type="bullet">
/// <item><b>Request rate</b>: a token bucket refilled at <c>RequestsPerMinute</c> with capacity
/// <c>BurstRequests</c>. An admitted request takes one token.</item>
/// <item><b>Concurrency</b>: at most <c>MaxConcurrentRequests</c> of the tenant's requests may be queued for or
/// holding a provider permit at once, so one tenant can't occupy every shared permit and queue slot.</item>
/// </list>
/// Invariants:
/// <list type="bullet">
/// <item>Each tenant has its own state, so one tenant's usage never changes another tenant's admission.</item>
/// <item>A request that is admitted but never reaches the provider (global capacity rejection, queue timeout,
/// client cancellation while queued) gets its token back when the lease is disposed. Once the provider call
/// has started the token is spent, whatever the outcome, because the provider may have done billable work.</item>
/// <item>The concurrency slot is released exactly once: when the lease is disposed or, for a provider call abandoned
/// while still running, at the moment it is abandoned. So an abandoned call never locks its tenant out of other
/// routes behind a 429.</item>
/// <item>An abandoned call still counts against its tenant's <c>MaxConcurrentRequests</c>, <b>for its route
/// only</b>, until it really ends. A tenant's in-progress requests plus its abandoned calls on a route never exceed
/// <c>MaxConcurrentRequests</c>, so one tenant (for example by disconnecting from its own requests) can hold at most
/// that many orphans per route and can't use up a route's shared budget (<c>MaxAbandonedCallsPerRoute</c>) alone.</item>
/// <item>Bucket arithmetic is exact: credit is held as integers (one token = <see cref="CreditPerToken"/> units,
/// refilled at <c>RequestsPerMinute</c> units per tick), so partial refills never drift below a whole token.</item>
/// </list>
/// State is in-process. With N replicas each enforces its own copy, so the effective limits are N times the
/// configured ones (see docs/m2-design-notes.md). State is keyed only by authenticated tenant IDs, so
/// unauthenticated callers cannot grow it.
/// </summary>
public sealed class TenantQuotaService
{
    /// <summary>Credit units per token. A token refills in TicksPerMinute / RequestsPerMinute ticks.</summary>
    public const long CreditPerToken = TimeSpan.TicksPerMinute;

    private static readonly TimeSpan ConcurrencyRetryAfter = TimeSpan.FromSeconds(1);

    private readonly TenantQuotaOptions _default;
    private readonly Dictionary<string, TenantQuotaOptions> _overrides = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TenantBucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public TenantQuotaService(QuotaOptions options, TimeProvider timeProvider, ILogger<TenantQuotaService>? logger = null)
    {
        _time = timeProvider;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _default = options.Default;
        foreach (var (tenantId, o) in options.Tenants)
        {
            _overrides[tenantId] = new TenantQuotaOptions
            {
                RequestsPerMinute = o.RequestsPerMinute ?? _default.RequestsPerMinute,
                BurstRequests = o.BurstRequests ?? _default.BurstRequests,
                MaxConcurrentRequests = o.MaxConcurrentRequests ?? _default.MaxConcurrentRequests,
            };
        }
    }

    /// <summary>Effective limits for a tenant: its override if one exists, else the default.</summary>
    public TenantQuotaOptions LimitsFor(string tenantId) =>
        _overrides.TryGetValue(tenantId, out var limits) ? limits : _default;

    /// <summary>All distinct effective limits (default plus overrides), for startup diagnostics.</summary>
    public IEnumerable<(string Scope, TenantQuotaOptions Limits)> ConfiguredLimits(Func<string, string>? tenantReference = null) =>
        _overrides.Select(o => ($"tenant '{tenantReference?.Invoke(o.Key) ?? o.Key}'", o.Value)).Prepend(("default", _default));

    /// <param name="route">The provider route the request will use (<see cref="ProviderRoutes.Key"/>); null skips the
    /// per-route abandoned-call check.</param>
    public QuotaAdmission TryAdmit(string tenantId, string? route = null)
    {
        var limits = LimitsFor(tenantId);
        var now = _time.GetTimestamp();
        var bucket = _buckets.GetOrAdd(tenantId, static (_, capacity) => new TenantBucket(capacity), Capacity(limits));

        lock (bucket)
        {
            Refill(bucket, limits, now);
            var untilNextToken = TimeUntilNextToken(bucket, limits);

            // Concurrency is checked first so a tenant that is merely busy does not also lose a rate token. Its
            // Retry-After still covers an empty bucket, so a client that honours it is not rejected again for rate.
            if (bucket.Active >= limits.MaxConcurrentRequests)
            {
                var retryAfter = untilNextToken > ConcurrencyRetryAfter ? untilNextToken : ConcurrencyRetryAfter;
                return new QuotaAdmission(null, QuotaRejection.Concurrency, retryAfter);
            }

            // Checked before the rate so a refusal caused by the route's defect costs no token.
            if (route is not null && bucket.Active + bucket.AbandonedOn(route) >= limits.MaxConcurrentRequests)
            {
                return new QuotaAdmission(null, QuotaRejection.AbandonedCalls, ConcurrencyRetryAfter);
            }

            if (untilNextToken > TimeSpan.Zero)
            {
                return new QuotaAdmission(null, QuotaRejection.RequestRate, untilNextToken);
            }

            bucket.Credit -= CreditPerToken;
            bucket.Active++;
            return new QuotaAdmission(new TenantQuotaLease(this, bucket, limits, route), QuotaRejection.None, TimeSpan.Zero);
        }
    }

    public TenantQuotaSnapshot GetSnapshot(string tenantId)
    {
        if (!_buckets.TryGetValue(tenantId, out var bucket))
        {
            return new TenantQuotaSnapshot(0, LimitsFor(tenantId).BurstRequests);
        }

        lock (bucket)
        {
            Refill(bucket, LimitsFor(tenantId), _time.GetTimestamp());
            return new TenantQuotaSnapshot(bucket.Active, (double)bucket.Credit / CreditPerToken);
        }
    }

    /// <summary>The tenant's abandoned calls on a route that are still running.</summary>
    public int AbandonedCallsFor(string tenantId, string route)
    {
        if (!_buckets.TryGetValue(tenantId, out var bucket))
        {
            return 0;
        }

        lock (bucket)
        {
            return bucket.AbandonedOn(route);
        }
    }

    /// <summary>
    /// Moves an abandoned request from "in progress" to the route's abandoned count in one step under the tenant's
    /// lock, so the sum that <see cref="TryAdmit"/> compares with <c>MaxConcurrentRequests</c> never counts it twice.
    /// The provider was called, so there is no token to refund. Reaching and leaving a tenant's share of a route are
    /// logged once each (refusals themselves only at Debug), without the tenant ID. Request completion telemetry
    /// supplies a pseudonymous tenant reference for refusals.
    /// </summary>
    internal void MoveToAbandoned(TenantBucket bucket, TenantQuotaOptions limits, string route, bool releaseSlot)
    {
        int count;
        lock (bucket)
        {
            if (releaseSlot)
            {
                bucket.Active--;
            }

            count = bucket.AbandonedOn(route) + 1;
            bucket.AbandonedByRoute[route] = count;
        }

        if (count == limits.MaxConcurrentRequests)
        {
            _logger.LogWarning(
                "A tenant's abandoned calls on route {Route} reached its MaxConcurrentRequests ({Limit}); that tenant is refused on this route with 503 until they end",
                route, limits.MaxConcurrentRequests);
        }
    }

    internal void RemoveAbandoned(TenantBucket bucket, TenantQuotaOptions limits, string route)
    {
        int remaining;
        lock (bucket)
        {
            remaining = bucket.AbandonedOn(route) - 1;
            if (remaining > 0)
            {
                bucket.AbandonedByRoute[route] = remaining;
            }
            else
            {
                bucket.AbandonedByRoute.Remove(route); // keeps the map as small as the set of currently hanging routes
            }
        }

        if (remaining == limits.MaxConcurrentRequests - 1)
        {
            _logger.LogInformation(
                "A tenant's abandoned calls on route {Route} are below its MaxConcurrentRequests again ({Remaining} still running)",
                route, remaining);
        }
    }

    internal void Release(TenantBucket bucket, TenantQuotaOptions limits, bool refundToken)
    {
        lock (bucket)
        {
            bucket.Active--;
            if (refundToken)
            {
                Refill(bucket, limits, _time.GetTimestamp());
                bucket.Credit = Math.Min(Capacity(limits), bucket.Credit + CreditPerToken);
            }
        }
    }

    private void Refill(TenantBucket bucket, TenantQuotaOptions limits, long now)
    {
        if (bucket.LastRefill is not { } last)
        {
            bucket.LastRefill = now;
            return;
        }

        // A caller that read the clock before another thread refilled can observe an older timestamp; skip it.
        var elapsedTicks = _time.GetElapsedTime(last, now).Ticks;
        if (elapsedTicks <= 0)
        {
            return;
        }

        // Integer arithmetic: elapsed ticks x RequestsPerMinute is exactly the credit earned. The comparison with
        // the time needed to fill the bucket comes first, so a long idle period can't overflow the product.
        var capacity = Capacity(limits);
        var ticksToFill = CeilingDivide(capacity - bucket.Credit, limits.RequestsPerMinute);
        bucket.Credit = elapsedTicks >= ticksToFill ? capacity : bucket.Credit + (elapsedTicks * limits.RequestsPerMinute);
        bucket.LastRefill = now;
    }

    private static TimeSpan TimeUntilNextToken(TenantBucket bucket, TenantQuotaOptions limits) =>
        bucket.Credit >= CreditPerToken
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(CeilingDivide(CreditPerToken - bucket.Credit, limits.RequestsPerMinute));

    private static long Capacity(TenantQuotaOptions limits) => limits.BurstRequests * CreditPerToken;

    private static long CeilingDivide(long value, long divisor) => value <= 0 ? 0 : ((value - 1) / divisor) + 1;

    /// <summary>Mutable per-tenant state; every access holds the instance lock.</summary>
    internal sealed class TenantBucket(long initialCredit)
    {
        /// <summary>Available credit; one request costs <see cref="CreditPerToken"/>.</summary>
        public long Credit { get; set; } = initialCredit;

        public long? LastRefill { get; set; }

        public int Active { get; set; }

        /// <summary>Abandoned calls still running, per route. Same comparer as the invoker's route counters.</summary>
        public Dictionary<string, int> AbandonedByRoute { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int AbandonedOn(string route) => AbandonedByRoute.TryGetValue(route, out var count) ? count : 0;
    }
}

/// <summary>
/// One admitted request's claim on its tenant's quota. Dispose it on every path (a <c>using</c> declaration);
/// call <see cref="MarkProviderCallStarted"/> just before the provider is invoked.
/// </summary>
public sealed class TenantQuotaLease : IDisposable
{
    private readonly TenantQuotaService _owner;
    private readonly TenantQuotaService.TenantBucket _bucket;
    private readonly TenantQuotaOptions _limits;
    private readonly string? _route;
    private int _released;
    private volatile bool _providerCallStarted;

    internal TenantQuotaLease(
        TenantQuotaService owner, TenantQuotaService.TenantBucket bucket, TenantQuotaOptions limits, string? route)
    {
        _owner = owner;
        _bucket = bucket;
        _limits = limits;
        _route = route;
    }

    /// <summary>From this point the request's token is spent and is not refunded on dispose.</summary>
    public void MarkProviderCallStarted() => _providerCallStarted = true;

    /// <summary>
    /// The provider call was abandoned but is still running: count it against this tenant on this lease's route
    /// until <paramref name="providerCall"/> ends. The concurrency slot is released at the same moment, in the same
    /// locked step, so the tenant can keep using other routes and the call is never counted both as in progress
    /// and as abandoned. <see cref="Dispose"/> then has nothing left to release.
    /// </summary>
    public void CountAbandonedCall(Task providerCall)
    {
        if (_route is not { } route)
        {
            return;
        }

        // Only reached after the provider was called, so releasing here never owes a refund.
        var releaseSlot = Interlocked.Exchange(ref _released, 1) == 0;
        _owner.MoveToAbandoned(_bucket, _limits, route, releaseSlot);
        providerCall.ContinueWith(
            static (_, state) =>
            {
                var lease = (TenantQuotaLease)state!;
                lease._owner.RemoveAbandoned(lease._bucket, lease._limits, lease._route!);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _owner.Release(_bucket, _limits, refundToken: !_providerCallStarted);
        }
    }
}
