using AiCore.Api.Configuration;
using AiCore.Api.Errors;
using AiCore.Api.Providers;
using AiCore.Api.Observability;
using System.Diagnostics;

namespace AiCore.Api.Execution;

/// <summary>Validated, server-side view of a generate call. Tenant comes from authentication only.</summary>
public sealed record GenerationCommand(
    string CorrelationId,
    string TenantId,
    string ModelAlias,
    string Prompt,
    int MaxOutputTokens);

public sealed record GenerationSuccess(
    string ModelAlias,
    string Provider,
    string ProviderModel,
    ProviderResult Result,
    TimeSpan ProviderLatency);

/// <summary>Either a success or a safe <see cref="ApiError"/>; never both.</summary>
public sealed record GenerationOutcome(GenerationSuccess? Success, ApiError? Error)
{
    public static GenerationOutcome Ok(GenerationSuccess success) => new(success, null);

    public static GenerationOutcome Fail(ApiError error) => new(null, error);
}

/// <summary>
/// Request policy and provider execution. Admission happens in a fixed order, cheapest and most
/// caller-specific first:
/// <list type="number">
/// <item>Resolve the alias (400 if unknown). Invalid requests consume no quota or capacity.</item>
/// <item>Refuse a route (provider + model) that has too many abandoned calls still running (503). Checked again
/// after a permit is acquired, because requests that queued before the limit was reached would otherwise reach
/// the hanging route.</item>
/// <item>Tenant quota: rate and concurrency (429, FR-04), plus the tenant's own abandoned calls on this route
/// (503, this route only). Checked before the shared queue so a tenant over its quota never occupies shared queue
/// slots.</item>
/// <item>Global provider permit: bounded queue, bounded wait (503, FR-05).</item>
/// <item>Provider phase: deadline, safe retries, cancellation (502/503/504/500, FR-06, FR-10).</item>
/// </list>
/// Both leases are released by <c>using</c> declarations on every path. Knows nothing about HTTP parsing.
/// </summary>
public sealed class GenerationService(
    IReadOnlyDictionary<string, ModelRouteOptions> routes,
    IEnumerable<IModelProvider> providers,
    TenantQuotaService quotas,
    ProviderConcurrencyGate gate,
    ProviderInvoker invoker,
    TimeProvider timeProvider,
    ILogger<GenerationService> logger,
    AiCoreTelemetry? telemetry = null)
{
    private readonly Dictionary<string, IModelProvider> _providers =
        providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<GenerationOutcome> GenerateAsync(GenerationCommand command, CancellationToken cancellationToken)
    {
        if (!routes.TryGetValue(command.ModelAlias, out var route))
        {
            return GenerationOutcome.Fail(ApiProblem.UnknownModel());
        }

        if (!_providers.TryGetValue(route.Provider, out var provider))
        {
            // Configuration defect, not a caller error. Logged without prompt content.
            logger.LogError(
                "Model alias {ModelAlias} routes to unregistered provider {Provider} (correlation {CorrelationId})",
                command.ModelAlias, route.Provider, command.CorrelationId);
            return GenerationOutcome.Fail(ApiProblem.Internal());
        }

        var routeKey = ProviderRoutes.Key(provider.Name, route.ProviderModel);

        // A route that has accumulated abandoned calls (its adapter ignores cancellation) gets no new work until they
        // end. Checked before the tenant quota: a provider-side condition must not cost the tenant a token.
        if (RouteIsRefused(routeKey, command))
        {
            return GenerationOutcome.Fail(ApiProblem.ProviderPaused(RouteRefusalRetryAfter));
        }

        var admission = quotas.TryAdmit(command.TenantId, routeKey);
        if (!admission.IsAdmitted)
        {
            switch (admission.Rejection)
            {
                case QuotaRejection.AbandonedCalls:
                    // This tenant's own abandoned calls on this route (e.g. requests it disconnected from) are at its
                    // limit. Only this tenant, only on this route: 503, since the route's adapter is the defect.
                    logger.LogDebug(
                        "Refusing alias {ModelAlias}: the tenant's abandoned calls on route {Route} are at its concurrency limit (correlation {CorrelationId})",
                        command.ModelAlias, routeKey, command.CorrelationId);
                    return GenerationOutcome.Fail(ApiProblem.ProviderPaused(admission.RetryAfter));
                case QuotaRejection.Concurrency:
                    return GenerationOutcome.Fail(ApiProblem.ConcurrencyQuotaExceeded(admission.RetryAfter));
                default:
                    return GenerationOutcome.Fail(ApiProblem.RateQuotaExceeded(admission.RetryAfter));
            }
        }

        // Disposed last: releases the tenant's concurrency slot, and refunds its rate token if the provider
        // was never called.
        using var tenantLease = admission.Lease!;

        // Waits in the bounded queue or is rejected. Client cancellation while queued throws
        // OperationCanceledException, which the host treats as a client abort.
        GateAcquisition acquisition;
        var queueStarted = Stopwatch.GetTimestamp();
        using (var queueActivity = telemetry?.StartPhase("aicore.queue", command.ModelAlias, provider.Name))
        {
            try { acquisition = await gate.AcquireAsync(cancellationToken); }
            finally { telemetry?.RecordPhase(true, command.ModelAlias, provider.Name, Stopwatch.GetElapsedTime(queueStarted)); }
        }
        using var permit = acquisition.Lease;
        if (permit is null)
        {
            return GenerationOutcome.Fail(ApiProblem.CapacityExhausted(acquisition.QueueWaitTimedOut));
        }

        // Re-checked after queueing: calls abandoned while this request waited may have reached the limit. The
        // provider is not called, so the permit is returned and the tenant's token refunded.
        if (RouteIsRefused(routeKey, command))
        {
            return GenerationOutcome.Fail(ApiProblem.ProviderPaused(RouteRefusalRetryAfter));
        }

        // The gate's fast path takes a free permit without looking at the client token. A client that has already
        // gone gets no (possibly billable) provider call, and its token is refunded because the call never started.
        cancellationToken.ThrowIfCancellationRequested();

        tenantLease.MarkProviderCallStarted();
        var started = timeProvider.GetTimestamp();
        ProviderCallResult call;
        var providerStarted = Stopwatch.GetTimestamp();
        using (var providerActivity = telemetry?.StartPhase("aicore.provider", command.ModelAlias, provider.Name))
        {
            try
            {
                call = await invoker.InvokeAsync(
                    provider,
                    new ProviderRequest(command.CorrelationId, command.TenantId, route.ProviderModel, command.Prompt, command.MaxOutputTokens),
                    cancellationToken,
                    // An abandoned call keeps counting against this tenant on this route (only) until it really ends.
                    onAbandoned: tenantLease.CountAbandonedCall);
                providerActivity?.SetTag("result", call.Status.ToString());
                providerActivity?.SetTag("attempts", call.Attempts);
                if (call.Status != ProviderCallStatus.Succeeded) providerActivity?.SetStatus(ActivityStatusCode.Error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                providerActivity?.SetTag("result", "cancelled");
                throw;
            }
            finally { telemetry?.RecordPhase(false, command.ModelAlias, provider.Name, Stopwatch.GetElapsedTime(providerStarted)); }
        }

        switch (call.Status)
        {
            case ProviderCallStatus.Succeeded:
                return GenerationOutcome.Ok(new GenerationSuccess(
                    command.ModelAlias, provider.Name, route.ProviderModel, call.Result!, timeProvider.GetElapsedTime(started)));

            case ProviderCallStatus.TimedOut:
                logger.LogWarning(
                    "Provider {Provider} timed out after {TimeoutMs} ms for alias {ModelAlias} on attempt {Attempt} (correlation {CorrelationId})",
                    provider.Name, (long)invoker.Timeout.TotalMilliseconds, command.ModelAlias, call.Attempts, command.CorrelationId);
                return GenerationOutcome.Fail(ApiProblem.ProviderTimeout());

            case ProviderCallStatus.TransientFailure:
                logger.LogWarning(
                    "Provider {Provider} transient failure for alias {ModelAlias} after {Attempts} attempt(s) (request not processed: {RequestNotProcessed}) (correlation {CorrelationId})",
                    provider.Name, command.ModelAlias, call.Attempts, call.RequestNotProcessed, command.CorrelationId);
                return GenerationOutcome.Fail(ApiProblem.ProviderUnavailable());

            case ProviderCallStatus.PermanentFailure:
                logger.LogWarning(
                    "Provider {Provider} permanent failure for alias {ModelAlias} (correlation {CorrelationId})",
                    provider.Name, command.ModelAlias, command.CorrelationId);
                return GenerationOutcome.Fail(ApiProblem.ProviderError());

            default:
                // Only the exception type is logged; see ProviderInvoker. The framework exception handler is not
                // involved, so the message (which may carry request content) is never written anywhere.
                logger.LogError(
                    "Provider {Provider} threw unexpected {ExceptionType} for alias {ModelAlias} (correlation {CorrelationId})",
                    provider.Name, call.ExceptionType, command.ModelAlias, command.CorrelationId);
                return GenerationOutcome.Fail(ApiProblem.Internal());
        }
        // The permit, then the tenant lease, are released by the using declarations on every path, including
        // client cancellation (an exception) and timeouts.
    }

    private static readonly TimeSpan RouteRefusalRetryAfter = TimeSpan.FromSeconds(1);

    private bool RouteIsRefused(string routeKey, GenerationCommand command)
    {
        if (!invoker.HasTooManyAbandonedCalls(routeKey))
        {
            return false;
        }

        // Debug only: ProviderInvoker logs reaching and leaving the limit once each at Warning/Information.
        logger.LogDebug(
            "Refusing alias {ModelAlias}: route {Route} is at its abandoned-call limit (correlation {CorrelationId})",
            command.ModelAlias, routeKey, command.CorrelationId);
        return true;
    }
}
