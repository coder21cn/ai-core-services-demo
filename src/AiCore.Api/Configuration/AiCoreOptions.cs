using AiCore.Api.Providers;

namespace AiCore.Api.Configuration;

/// <summary>
/// Root of the "AiCore" configuration section. Bound strictly (unknown keys fail startup) and validated at
/// startup by <see cref="AiCoreOptionsValidator"/> (FR-11), so a typo or an unusable value never silently
/// falls back to a default.
/// </summary>
public sealed class AiCoreOptions
{
    public const string SectionName = "AiCore";

    public RequestLimitsOptions Limits { get; set; } = new();

    public ConcurrencyOptions Concurrency { get; set; } = new();

    public ResilienceOptions Resilience { get; set; } = new();

    public QuotaOptions Quotas { get; set; } = new();

    /// <summary>Logical model alias -> provider route. Aliases are the stable public contract.</summary>
    public Dictionary<string, ModelRouteOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Behaviour of the built-in deterministic mock provider, per provider model.</summary>
    public MockProviderOptions MockProvider { get; set; } = new();

    /// <summary>
    /// Development-only API keys. Supplied via environment variables or user secrets; never committed.
    /// </summary>
    public List<TenantApiKeyOptions> Tenants { get; set; } = new();
}

public sealed class RequestLimitsOptions
{
    /// <summary>Maximum prompt length in UTF-16 characters.</summary>
    public int MaxPromptChars { get; set; } = 8_000;

    /// <summary>Upper bound for <c>maxOutputTokens</c>; also bounds the size of the response text.</summary>
    public int MaxOutputTokens { get; set; } = 1_024;

    /// <summary>Default when the caller omits <c>maxOutputTokens</c> (capped at <see cref="MaxOutputTokens"/>).</summary>
    public int DefaultOutputTokens { get; set; } = 256;

    /// <summary>Kestrel request body limit (bytes). Applies to the real server, not the in-memory test host.</summary>
    public long MaxRequestBodyBytes { get; set; } = 64 * 1024;
}

public sealed class ConcurrencyOptions
{
    /// <summary>
    /// Maximum concurrent provider-phase permits per process, including retries and cancellation grace.
    /// Calls abandoned after ignoring cancellation no longer hold permits; they have separate route limits.
    /// </summary>
    public int MaxConcurrentProviderCalls { get; set; } = 8;

    /// <summary>
    /// Maximum requests allowed to wait for a permit. Requests beyond this are rejected immediately
    /// (503 capacity_exhausted). 0 means "reject immediately when all permits are in use".
    /// </summary>
    public int MaxQueuedRequests { get; set; } = 16;

    /// <summary>
    /// Longest time a request may wait in the queue for a permit before it is rejected with
    /// 503 capacity_exhausted. Bounds waiting in time as well as in count.
    /// </summary>
    public int MaxQueueWaitMs { get; set; } = 10_000;
}

public sealed class ResilienceOptions
{
    /// <summary>
    /// Deadline for the whole provider phase of one request: every attempt plus retry backoff.
    /// Queue wait is not included (see <see cref="ConcurrencyOptions.MaxQueueWaitMs"/>).
    /// </summary>
    public int ProviderTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// After a provider call is cancelled (timeout or client abort), how long to wait for the adapter to
    /// stop before the call is abandoned and its permit released. 0 abandons a call that has not already stopped.
    /// </summary>
    public int CancellationGraceMs { get; set; } = 1_000;

    /// <summary>
    /// Total provider attempts per request, including the first. Only failures the provider marks as
    /// "request not processed" are retried. 1 disables retries.
    /// </summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Fixed delay before a retry. The permit stays held during the delay.</summary>
    public int RetryBackoffMs { get; set; } = 250;

    /// <summary>
    /// Abandoned calls (calls that ignored cancellation past the grace period) a route, meaning a provider plus
    /// provider model, may have running before new calls to it are refused with 503 provider_unavailable. Bounds
    /// the orphans a hanging model can accumulate, without affecting other models.
    /// </summary>
    public int MaxAbandonedCallsPerRoute { get; set; } = 8;
}

public sealed class QuotaOptions
{
    /// <summary>Quota applied to every tenant without an override.</summary>
    public TenantQuotaOptions Default { get; set; } = new();

    /// <summary>
    /// Per-tenant overrides keyed by tenant ID (exact match). Each field that is set replaces the default;
    /// fields left unset inherit it.
    /// </summary>
    public Dictionary<string, TenantQuotaOverrideOptions> Tenants { get; set; } = new(StringComparer.Ordinal);
}

public sealed class TenantQuotaOptions
{
    /// <summary>Sustained request rate (token-bucket refill rate).</summary>
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>Token-bucket capacity: requests a tenant can make in a burst after being idle.</summary>
    public int BurstRequests { get; set; } = 20;

    /// <summary>
    /// Requests of one tenant that may be queued for, or holding, a provider permit at the same time.
    /// Keeps one tenant from occupying all shared permits and queue slots.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 4;
}

public sealed class TenantQuotaOverrideOptions
{
    public int? RequestsPerMinute { get; set; }

    public int? BurstRequests { get; set; }

    public int? MaxConcurrentRequests { get; set; }
}

public sealed class ModelRouteOptions
{
    /// <summary>Name of a registered <see cref="Providers.IModelProvider"/>.</summary>
    public string Provider { get; set; } = "";

    /// <summary>Provider-specific model identifier; never exposed as the public contract key.</summary>
    public string ProviderModel { get; set; } = "";
}

public sealed class TenantApiKeyOptions
{
    public string TenantId { get; set; } = "";

    public string ApiKey { get; set; } = "";
}
