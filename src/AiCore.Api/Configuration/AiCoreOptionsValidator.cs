using AiCore.Api.Providers;
using Microsoft.Extensions.Options;

namespace AiCore.Api.Configuration;

/// <summary>
/// Startup validation of the "AiCore" section (FR-11). Every failure names the full configuration key, the
/// rule and the configured value, so an operator can fix it without reading code. All failures are reported
/// together rather than one per restart. Secret values (API keys) are never echoed.
/// </summary>
public sealed class AiCoreOptionsValidator : IValidateOptions<AiCoreOptions>
{
    /// <summary>Upper bound on attempts, so a configuration change can't turn retries into load amplification.</summary>
    public const int MaxAttemptsCeiling = 5;

    private const string Root = AiCoreOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, AiCoreOptions options)
    {
        var failures = Validate(options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> Validate(AiCoreOptions options)
    {
        var failures = new List<string>();

        var limits = options.Limits;
        AtLeast(failures, "Limits:MaxPromptChars", limits.MaxPromptChars, 1, "the maximum prompt length in characters");
        AtLeast(failures, "Limits:MaxOutputTokens", limits.MaxOutputTokens, 1, "the upper bound for maxOutputTokens");
        AtLeast(failures, "Limits:DefaultOutputTokens", limits.DefaultOutputTokens, 1, "the output budget used when the caller omits maxOutputTokens");
        AtLeast(failures, "Limits:MaxRequestBodyBytes", limits.MaxRequestBodyBytes, 1, "the HTTP request body size limit in bytes");

        var concurrency = options.Concurrency;
        AtLeast(failures, "Concurrency:MaxConcurrentProviderCalls", concurrency.MaxConcurrentProviderCalls, 1, "the maximum simultaneous provider calls per process");
        AtLeast(failures, "Concurrency:MaxQueuedRequests", concurrency.MaxQueuedRequests, 0, "the number of requests that may wait for a permit; 0 rejects immediately when full");
        AtLeast(failures, "Concurrency:MaxQueueWaitMs", concurrency.MaxQueueWaitMs, 1, "the longest time a request may wait for a permit");

        var resilience = options.Resilience;
        AtLeast(failures, "Resilience:ProviderTimeoutMs", resilience.ProviderTimeoutMs, 1, "the deadline for the provider phase of a request");
        AtLeast(failures, "Resilience:CancellationGraceMs", resilience.CancellationGraceMs, 0, "how long a cancelled provider call may take to stop before it is abandoned");
        AtLeast(failures, "Resilience:RetryBackoffMs", resilience.RetryBackoffMs, 0, "the delay before a retry");
        AtLeast(failures, "Resilience:MaxAbandonedCallsPerRoute", resilience.MaxAbandonedCallsPerRoute, 1, "abandoned calls a provider model may have running before new calls to it are refused");
        if (resilience.MaxAttempts is < 1 or > MaxAttemptsCeiling)
        {
            failures.Add(
                $"{Root}:Resilience:MaxAttempts must be between 1 and {MaxAttemptsCeiling} but is {resilience.MaxAttempts} " +
                "(total provider attempts per request; 1 disables retries).");
        }
        else if (resilience.MaxAttempts > 1 && resilience.ProviderTimeoutMs >= 1 &&
                 resilience.RetryBackoffMs >= resilience.ProviderTimeoutMs)
        {
            failures.Add(
                $"{Root}:Resilience:RetryBackoffMs ({resilience.RetryBackoffMs}) must be less than " +
                $"{Root}:Resilience:ProviderTimeoutMs ({resilience.ProviderTimeoutMs}) when MaxAttempts > 1; " +
                "otherwise a retry can never start before the deadline. Lower the backoff or set MaxAttempts to 1.");
        }

        ValidateQuotas(options, failures);
        ValidateTenants(options.Tenants, failures);

        foreach (var (alias, route) in options.Models)
        {
            Required(failures, $"Models:{alias}:Provider", route.Provider, "the name of the provider adapter serving this alias");
            Required(failures, $"Models:{alias}:ProviderModel", route.ProviderModel, "the provider-specific model identifier");
        }

        foreach (var (model, mock) in options.MockProvider.Models)
        {
            AtLeast(failures, $"MockProvider:Models:{model}:LatencyMs", mock.LatencyMs, 0, "the simulated latency of this mock model");

            // The binder accepts numeric strings for enums, so "7" would otherwise bind to an undefined value
            // that the mock treats as success.
            if (!Enum.IsDefined(mock.Behavior))
            {
                failures.Add(
                    $"{Root}:MockProvider:Models:{model}:Behavior must be one of {string.Join(", ", Enum.GetNames<MockBehavior>())} " +
                    $"but is {(int)mock.Behavior}.");
            }
        }

        return failures;
    }

    private static void ValidateQuotas(AiCoreOptions options, List<string> failures)
    {
        var quotas = options.Quotas;
        AtLeast(failures, "Quotas:Default:RequestsPerMinute", quotas.Default.RequestsPerMinute, 1, "the sustained per-tenant request rate");
        AtLeast(failures, "Quotas:Default:BurstRequests", quotas.Default.BurstRequests, 1, "the per-tenant burst size");
        AtLeast(failures, "Quotas:Default:MaxConcurrentRequests", quotas.Default.MaxConcurrentRequests, 1, "the per-tenant limit on queued plus in-flight requests");

        var knownTenants = options.Tenants.Select(t => t.TenantId).ToHashSet(StringComparer.Ordinal);
        foreach (var (tenantId, quota) in quotas.Tenants)
        {
            if (!knownTenants.Contains(tenantId))
            {
                // An override that matches nobody is almost always a typo that would silently leave the
                // intended tenant on the default quota.
                failures.Add(
                    $"{Root}:Quotas:Tenants:{tenantId} does not match any configured {Root}:Tenants:*:TenantId " +
                    "(tenant IDs are case-sensitive). Fix the tenant ID or remove the override.");
            }

            if (quota.RequestsPerMinute is { } rpm)
            {
                AtLeast(failures, $"Quotas:Tenants:{tenantId}:RequestsPerMinute", rpm, 1, "this tenant's sustained request rate");
            }

            if (quota.BurstRequests is { } burst)
            {
                AtLeast(failures, $"Quotas:Tenants:{tenantId}:BurstRequests", burst, 1, "this tenant's burst size");
            }

            if (quota.MaxConcurrentRequests is { } concurrent)
            {
                AtLeast(failures, $"Quotas:Tenants:{tenantId}:MaxConcurrentRequests", concurrent, 1, "this tenant's limit on queued plus in-flight requests");
            }
        }
    }

    private static void ValidateTenants(List<TenantApiKeyOptions> tenants, List<string> failures)
    {
        var firstIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < tenants.Count; i++)
        {
            Required(failures, $"Tenants:{i}:TenantId", tenants[i].TenantId, "the tenant this API key authenticates");
            Required(failures, $"Tenants:{i}:ApiKey", tenants[i].ApiKey, "the development API key; supply it via an environment variable or user secrets");

            if (!string.IsNullOrWhiteSpace(tenants[i].ApiKey) && !firstIndexByKey.TryAdd(tenants[i].ApiKey, i))
            {
                // Never echo the key itself.
                failures.Add(
                    $"{Root}:Tenants:{i}:ApiKey duplicates the key of {Root}:Tenants:{firstIndexByKey[tenants[i].ApiKey]}:ApiKey; " +
                    "each API key must identify exactly one tenant.");
            }
        }
    }

    private static void AtLeast(List<string> failures, string key, long value, long minimum, string meaning)
    {
        if (value < minimum)
        {
            failures.Add($"{Root}:{key} must be at least {minimum} but is {value} ({meaning}).");
        }
    }

    private static void Required(List<string> failures, string key, string? value, string meaning)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{Root}:{key} is required ({meaning}).");
        }
    }
}
