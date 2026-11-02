namespace AiCore.Api.Providers;

/// <summary>
/// Provider abstraction. Deliberately small and vendor-neutral: implementations translate this into a
/// vendor SDK/HTTP call. Contract for implementations:
/// <list type="bullet">
/// <item>Honour <paramref name="cancellationToken"/> promptly.</item>
/// <item>Throw <see cref="ProviderTransientException"/> for failures that may succeed later (throttling,
/// 5xx, connection loss) and <see cref="ProviderPermanentException"/> for failures that will not
/// (invalid model, content rejected, bad credentials on the provider side).</item>
/// <item>Set <see cref="ProviderTransientException.RequestNotProcessed"/> only when the provider guarantees it
/// did no work for the request, because the platform retries exactly those failures.</item>
/// <item>Never put prompt text or credentials in exception messages or adapter telemetry. The platform drops
/// exception messages from its logs even for typed provider failures.</item>
/// </list>
/// </summary>
public interface IModelProvider
{
    /// <summary>Stable provider name used in model routes and returned to callers (e.g. "mock").</summary>
    string Name { get; }

    Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken);
}

/// <param name="TenantId">Server-derived tenant identity; available for attribution, never client-supplied.</param>
/// <param name="ProviderModel">The provider-specific model resolved from the logical alias.</param>
/// <param name="Attempt">1 for the first call; incremented for each platform retry of the same request.</param>
public sealed record ProviderRequest(
    string CorrelationId,
    string TenantId,
    string ProviderModel,
    string Prompt,
    int MaxOutputTokens,
    int Attempt = 1);

public sealed record ProviderResult(string Output, int InputTokens, int OutputTokens);

/// <summary>Base type for failures a provider reports deliberately. Messages must be safe to log.</summary>
public abstract class ProviderException(string message) : Exception(message);

/// <summary>A failure that may succeed if tried again later.</summary>
/// <param name="requestNotProcessed">
/// <c>true</c> only when the provider guarantees that it performed no inference for this request and will not
/// bill it: for example, it was throttled at admission (HTTP 429 before processing) or the connection was
/// refused before the request was sent. The platform retries only these failures. Leave it <c>false</c> when
/// the outcome is unknown (a timeout, a connection reset after the request was sent, a 5xx with no
/// guarantee), because the provider may already have produced, and billed, a completion.
/// </param>
public sealed class ProviderTransientException(string message, bool requestNotProcessed = false) : ProviderException(message)
{
    /// <summary>True when retrying cannot duplicate work or billing. See the constructor parameter.</summary>
    public bool RequestNotProcessed { get; } = requestNotProcessed;
}

public sealed class ProviderPermanentException(string message) : ProviderException(message);
