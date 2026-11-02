using System.Security.Cryptography;
using System.Text;

namespace AiCore.Api.Providers;

public enum MockBehavior
{
    Success,

    /// <summary>Transient failure with unknown outcome (the provider may have processed it). Not retried.</summary>
    TransientFailure,

    PermanentFailure,

    /// <summary>Every attempt is throttled before processing (transient, request not processed). Retried, then fails.</summary>
    Throttled,

    /// <summary>The first attempt is throttled before processing; later attempts succeed. Shows a safe retry recovering.</summary>
    ThrottledOnFirstAttempt,
}

public sealed class MockModelOptions
{
    public MockBehavior Behavior { get; set; } = MockBehavior.Success;

    /// <summary>Simulated latency before the outcome is produced.</summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// When <c>false</c>, the simulated latency ignores cancellation, imitating a defective adapter. Used to
    /// demonstrate that such a call is abandoned after the cancellation grace period and its permit released.
    /// </summary>
    public bool HonourCancellation { get; set; } = true;
}

/// <summary>Bound from <c>AiCore:MockProvider</c>.</summary>
public sealed class MockProviderOptions
{
    /// <summary>Provider-model name -> simulated behaviour.</summary>
    public Dictionary<string, MockModelOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Deterministic, offline model simulator (FR-02). The same input always yields the same output and
/// usage. Behaviour is selected per provider model via configuration, so success, transient failure,
/// throttling, permanent failure and slow or unresponsive calls can be exercised through the public API.
/// Outcomes depend only on configuration and the request (including its attempt number). Latency uses an injected
/// <see cref="TimeProvider"/> so tests can control time instead of sleeping.
/// </summary>
public sealed class MockModelProvider(MockProviderOptions options, TimeProvider timeProvider) : IModelProvider
{
    public const string ProviderName = "mock";

    private readonly Dictionary<string, MockModelOptions> _models =
        new(options.Models, StringComparer.OrdinalIgnoreCase);

    public string Name => ProviderName;

    public async Task<ProviderResult> GenerateAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        if (!_models.TryGetValue(request.ProviderModel, out var model))
        {
            // Mirrors a real provider rejecting an unknown model; a routing/config error, not retryable.
            throw new ProviderPermanentException($"Mock provider has no model '{request.ProviderModel}'.");
        }

        if (model.LatencyMs > 0)
        {
            var latency = TimeSpan.FromMilliseconds(model.LatencyMs);
            if (model.HonourCancellation)
            {
                // Throws OperationCanceledException if the caller cancels while "the model is thinking".
                await Task.Delay(latency, timeProvider, cancellationToken);
            }
            else
            {
                // Deliberate defect for demonstrations: the token is ignored, as a badly written adapter might.
                await Task.Delay(latency, timeProvider, CancellationToken.None);
            }
        }

        if (model.HonourCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        switch (model.Behavior)
        {
            case MockBehavior.TransientFailure:
                throw new ProviderTransientException("Mock provider simulated a transient failure.");
            case MockBehavior.PermanentFailure:
                throw new ProviderPermanentException("Mock provider simulated a permanent failure.");
            case MockBehavior.Throttled:
            case MockBehavior.ThrottledOnFirstAttempt when request.Attempt == 1:
                throw new ProviderTransientException(
                    "Mock provider simulated throttling; the request was not processed.", requestNotProcessed: true);
        }

        // The output intentionally does not echo the prompt; a fingerprint keeps it input-dependent
        // and deterministic without reflecting caller content.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Prompt)))[..12];
        var output = $"[{request.ProviderModel}] Simulated completion for a {request.Prompt.Length}-character prompt (fingerprint {fingerprint}).";

        // Bound the response by the requested token budget (~4 chars per token).
        // Valid Int32 budgets can exceed Int32.MaxValue / 4. Widen before multiplying; only narrow
        // when the bound is below an existing string length, which necessarily fits in Int32.
        var maxChars = (long)request.MaxOutputTokens * 4;
        if (output.Length > maxChars)
        {
            output = output[..(int)maxChars];
        }

        return new ProviderResult(output, EstimateTokens(request.Prompt.Length), EstimateTokens(output.Length));
    }

    /// <summary>Crude, deterministic token estimate: ceil(chars / 4), minimum 1.</summary>
    public static int EstimateTokens(int characters) => (int)Math.Max(1L, ((long)characters + 3) / 4);
}
