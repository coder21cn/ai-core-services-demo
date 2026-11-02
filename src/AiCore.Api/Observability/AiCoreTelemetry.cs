using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using AiCore.Api.Configuration;
using AiCore.Api.Execution;

namespace AiCore.Api.Observability;

/// <summary>Allowlisted telemetry only. Never pass request bodies, headers, output or exceptions here.</summary>
public sealed class AiCoreTelemetry : IDisposable
{
    public const string Name = "AiCore.Api";
    private readonly byte[] _tenantKey = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, string> _aliases;
    private readonly Counter<long> _requests;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _queueDuration;
    private readonly Histogram<double> _providerDuration;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _retries;
    private long _emissionFailures;

    public ActivitySource Activities { get; } = new(Name);
    public Meter Meter { get; } = new(Name);

    public AiCoreTelemetry(IReadOnlyDictionary<string, ModelRouteOptions> routes)
    {
        _aliases = routes.Keys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
        _requests = Meter.CreateCounter<long>("aicore.requests", "{request}");
        _duration = Meter.CreateHistogram<double>("aicore.request.duration", "s");
        _queueDuration = Meter.CreateHistogram<double>("aicore.queue.duration", "s");
        _providerDuration = Meter.CreateHistogram<double>("aicore.provider.duration", "s");
        _tokens = Meter.CreateCounter<long>("aicore.usage.tokens", "{token}");
        _attempts = Meter.CreateCounter<long>("aicore.provider.attempts", "{attempt}");
        _retries = Meter.CreateCounter<long>("aicore.provider.retries", "{retry}");
        Meter.CreateObservableGauge("aicore.telemetry.failures", () => EmissionFailures, "{failure}");
    }

    public long EmissionFailures => Interlocked.Read(ref _emissionFailures);

    // Listener/exporter code runs synchronously in the BCL APIs. Diagnostics must not throw through
    // admission/lease handling or change an already-produced response. Do not log failures through the
    // same possibly broken sink; retain an unsent local count that a healthy listener can observe later.
    public bool Observe(Action emit)
    {
        try { emit(); return true; }
        catch (Exception) { Interlocked.Increment(ref _emissionFailures); return false; }
    }

    public TelemetryActivity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        var parent = Activity.Current;
        Activity? activity = null;
        if (!Observe(() =>
        {
            // Keep the reference before Start invokes listeners so failed start callbacks can be cleaned up.
            activity = Activities.CreateActivity(name, kind);
            activity?.Start();
        }))
        {
            if (activity is not null) Observe(activity.Dispose);
            Activity.Current = parent;
            return null;
        }
        return activity is null ? null : new TelemetryActivity(this, activity, parent);
    }

    public string CanonicalAlias(string alias) => _aliases.GetValueOrDefault(alias, "none");

    // A fresh key for each host prevents dictionary attacks on predictable tenant IDs without adding a secret
    // setup requirement to the demo. These pseudonyms intentionally cannot be joined across restarts/replicas.
    public string TenantReference(string? tenantId) => tenantId is null ? "anonymous" :
        Convert.ToHexStringLower(HMACSHA256.HashData(_tenantKey, Encoding.UTF8.GetBytes(tenantId)));

    public void ObserveCapacity(ProviderConcurrencyGate gate, ProviderInvoker invoker)
    {
        Meter.CreateObservableGauge("aicore.provider.permits_in_use",
            () => gate.PermitLimit - gate.GetStatistics().CurrentAvailablePermits, "{permit}");
        Meter.CreateObservableGauge("aicore.queue.depth",
            () => gate.GetStatistics().CurrentQueuedCount, "{request}");
        Meter.CreateObservableGauge("aicore.provider.abandoned",
            () => invoker.AbandonedInFlight, "{call}");
    }

    public TelemetryActivity? StartPhase(string name, string alias, string provider) => StartActivity(name)?
        .SetTag("model.alias", CanonicalAlias(alias)).SetTag("provider.name", provider);

    public void RecordPhase(bool queue, string alias, string provider, TimeSpan duration)
    {
        var tags = new TagList { { "model.alias", CanonicalAlias(alias) }, { "provider.name", provider } };
        Observe(() => (queue ? _queueDuration : _providerDuration).Record(duration.TotalSeconds, tags));
    }

    public void RecordAttempt(string provider, int attempt)
    {
        var tag = new KeyValuePair<string, object?>("provider.name", provider);
        Observe(() => _attempts.Add(1, tag));
        if (attempt > 1) Observe(() => _retries.Add(1, tag));
    }

    public void RecordRequest(RequestObservation observation, string endpoint, string result, int status, TimeSpan elapsed)
    {
        // No request IDs, tenant IDs/pseudonyms, free-form paths, or caller-supplied aliases in metric labels.
        var tags = new TagList
        {
            { "endpoint", endpoint }, { "model.alias", observation.Model },
            { "provider.name", observation.Provider }, { "result", result }, { "http.response.status_code", status },
        };
        Observe(() => _requests.Add(1, tags));
        Observe(() => _duration.Record(elapsed.TotalSeconds, tags));
        if (observation.InputTokens is { } input && observation.OutputTokens is { } output)
        {
            var usageTags = new TagList
            {
                { "model.alias", observation.Model }, { "provider.name", observation.Provider }, { "usage.simulated", true },
                { "direction", "input" },
            };
            Observe(() => _tokens.Add(input, usageTags));
            usageTags[3] = new("direction", "output");
            Observe(() => _tokens.Add(output, usageTags));
        }
    }

    public void Dispose()
    {
        Activities.Dispose();
        Meter.Dispose();
        CryptographicOperations.ZeroMemory(_tenantKey);
    }
}

/// <summary>Activity lifetime that contains throwing listeners and restores the ambient parent.</summary>
public sealed class TelemetryActivity(AiCoreTelemetry telemetry, Activity activity, Activity? parent) : IDisposable
{
    public TelemetryActivity SetTag(string key, object? value) { activity.SetTag(key, value); return this; }
    public void SetStatus(ActivityStatusCode status) => activity.SetStatus(status);
    public void Dispose()
    {
        try { telemetry.Observe(activity.Dispose); }
        finally { Activity.Current = parent; }
    }
}

/// <summary>Safe per-request data shared by the endpoint, error writer and completion middleware.</summary>
public sealed class RequestObservation
{
    private static readonly object ItemKey = new();
    public string Model { get; set; } = "none";
    public string Provider { get; set; } = "none";
    public string? ErrorCode { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    public static RequestObservation For(HttpContext context)
    {
        if (context.Items[ItemKey] is not RequestObservation observation)
            context.Items[ItemKey] = observation = new();
        return observation;
    }
}
