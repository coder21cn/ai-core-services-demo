using AiCore.Api.Configuration;
using AiCore.Api.Execution;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests;

/// <summary>Unit tests of the orchestration in <see cref="GenerationService"/>, without the HTTP pipeline.</summary>
public sealed class GenerationServiceTests
{
    [Fact]
    public async Task ClientAlreadyGone_NeverReachesTheProvider_AndGetsItsTokenBack()
    {
        // The gate's fast path grants a free permit without looking at the client token, so a request whose client
        // disconnected just before must be stopped before the (possibly billable) provider call.
        var time = new FakeTimeProvider();
        var provider = new RecordingProvider();
        var quotas = new TenantQuotaService(
            new QuotaOptions { Default = new TenantQuotaOptions { RequestsPerMinute = 1, BurstRequests = 1, MaxConcurrentRequests = 1 } },
            time);
        var service = new GenerationService(
            new Dictionary<string, ModelRouteOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["alias"] = new ModelRouteOptions { Provider = "test", ProviderModel = "m1" },
            },
            [provider],
            quotas,
            new ProviderConcurrencyGate(new ConcurrencyOptions(), time),
            new ProviderInvoker(new ResilienceOptions(), time, NullLogger<ProviderInvoker>.Instance),
            time,
            NullLogger<GenerationService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(
            new GenerationCommand("corr", "t", "alias", "p", 10), new CancellationToken(canceled: true)));

        Assert.Empty(provider.Requests);
        Assert.Equal(1, quotas.GetSnapshot("t").AvailableRequestTokens, precision: 6);
        Assert.Equal(0, quotas.GetSnapshot("t").ActiveRequests);
    }
}
