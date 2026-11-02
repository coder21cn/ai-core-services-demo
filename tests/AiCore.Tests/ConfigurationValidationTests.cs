using AiCore.Api.Configuration;
using AiCore.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiCore.Tests;

/// <summary>
/// T-09 / FR-11: invalid settings stop the service at startup with errors that name the key, the rule and
/// the configured value. Exercised both on the validator and through the real host start-up path.
/// </summary>
public sealed class ConfigurationValidationTests
{
    public static TheoryData<string, string, string> InvalidSettings => new()
    {
        // key (under AiCore:), invalid value, expected fragment of the error
        { "Concurrency:MaxConcurrentProviderCalls", "0", "AiCore:Concurrency:MaxConcurrentProviderCalls must be at least 1 but is 0" },
        { "Concurrency:MaxConcurrentProviderCalls", "-4", "AiCore:Concurrency:MaxConcurrentProviderCalls must be at least 1 but is -4" },
        { "Concurrency:MaxQueuedRequests", "-1", "AiCore:Concurrency:MaxQueuedRequests must be at least 0 but is -1" },
        { "Concurrency:MaxQueueWaitMs", "0", "AiCore:Concurrency:MaxQueueWaitMs must be at least 1 but is 0" },
        { "Resilience:ProviderTimeoutMs", "0", "AiCore:Resilience:ProviderTimeoutMs must be at least 1 but is 0" },
        { "Resilience:ProviderTimeoutMs", "-30000", "AiCore:Resilience:ProviderTimeoutMs must be at least 1 but is -30000" },
        { "Resilience:CancellationGraceMs", "-1", "AiCore:Resilience:CancellationGraceMs must be at least 0 but is -1" },
        { "Resilience:MaxAttempts", "0", "AiCore:Resilience:MaxAttempts must be between 1 and 5 but is 0" },
        { "Resilience:MaxAttempts", "10", "AiCore:Resilience:MaxAttempts must be between 1 and 5 but is 10" },
        { "Resilience:RetryBackoffMs", "-5", "AiCore:Resilience:RetryBackoffMs must be at least 0 but is -5" },
        { "Resilience:RetryBackoffMs", "30000", "AiCore:Resilience:RetryBackoffMs (30000) must be less than AiCore:Resilience:ProviderTimeoutMs (30000)" },
        { "Limits:MaxPromptChars", "0", "AiCore:Limits:MaxPromptChars must be at least 1 but is 0" },
        { "Limits:MaxOutputTokens", "-1", "AiCore:Limits:MaxOutputTokens must be at least 1 but is -1" },
        { "Limits:DefaultOutputTokens", "0", "AiCore:Limits:DefaultOutputTokens must be at least 1 but is 0" },
        { "Limits:MaxRequestBodyBytes", "0", "AiCore:Limits:MaxRequestBodyBytes must be at least 1 but is 0" },
        { "Quotas:Default:RequestsPerMinute", "0", "AiCore:Quotas:Default:RequestsPerMinute must be at least 1 but is 0" },
        { "Quotas:Default:BurstRequests", "-2", "AiCore:Quotas:Default:BurstRequests must be at least 1 but is -2" },
        { "Quotas:Default:MaxConcurrentRequests", "0", "AiCore:Quotas:Default:MaxConcurrentRequests must be at least 1 but is 0" },
        { "Quotas:Tenants:tenant-a:RequestsPerMinute", "0", "AiCore:Quotas:Tenants:tenant-a:RequestsPerMinute must be at least 1 but is 0" },
        { "Quotas:Tenants:tenant-a:MaxConcurrentRequests", "-1", "AiCore:Quotas:Tenants:tenant-a:MaxConcurrentRequests must be at least 1 but is -1" },
        { "Quotas:Tenants:tenant-typo:BurstRequests", "5", "AiCore:Quotas:Tenants:tenant-typo does not match any configured AiCore:Tenants:*:TenantId" },
        { "MockProvider:Models:mock-echo-v1:LatencyMs", "-1", "AiCore:MockProvider:Models:mock-echo-v1:LatencyMs must be at least 0 but is -1" },
        { "MockProvider:Models:mock-echo-v1:Behavior", "7", "AiCore:MockProvider:Models:mock-echo-v1:Behavior must be one of" },
        { "Models:broken:Provider", "", "AiCore:Models:broken:ProviderModel is required" },
        { "Tenants:2:TenantId", "tenant-c", "AiCore:Tenants:2:ApiKey is required" },
    };

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void T09_InvalidSetting_FailsHostStartup_WithAnActionableMessage(string key, string value, string expected)
    {
        using var api = new TestApi(new() { [$"AiCore:{key}"] = value });

        var ex = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void T09_AllProblemsAreReportedTogether()
    {
        using var api = new TestApi(new()
        {
            ["AiCore:Concurrency:MaxConcurrentProviderCalls"] = "0",
            ["AiCore:Resilience:ProviderTimeoutMs"] = "-1",
            ["AiCore:Quotas:Default:RequestsPerMinute"] = "0",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Equal(3, ex.Failures.Count());
        Assert.Contains("MaxConcurrentProviderCalls", ex.Message);
        Assert.Contains("ProviderTimeoutMs", ex.Message);
        Assert.Contains("RequestsPerMinute", ex.Message);
    }

    [Fact]
    public void T09_MisspeltKey_FailsStartup_InsteadOfSilentlyUsingTheDefault()
    {
        using var api = new TestApi(new() { ["AiCore:Concurrency:MaxConcurentProviderCalls"] = "2" });

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains("MaxConcurentProviderCalls", ex.Message);
    }

    [Fact]
    public void T09_NonNumericValue_FailsStartup_NamingTheKey()
    {
        using var api = new TestApi(new() { ["AiCore:Resilience:ProviderTimeoutMs"] = "thirty seconds" });

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => api.CreateClient());

        Assert.Contains("AiCore:Resilience:ProviderTimeoutMs", ex.Message);
    }

    [Fact]
    public void T09_DuplicateApiKey_IsRejected_WithoutRevealingIt()
    {
        using var api = new TestApi(new()
        {
            ["AiCore:Tenants:2:TenantId"] = "tenant-c",
            ["AiCore:Tenants:2:ApiKey"] = TestApi.KeyA,
        });

        var ex = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("AiCore:Tenants:2:ApiKey duplicates the key of AiCore:Tenants:0:ApiKey", ex.Message);
        Assert.DoesNotContain(TestApi.KeyA, ex.Message);
    }

    [Fact]
    public void T09_ShippedConfiguration_IsValid()
    {
        using var api = new TestApi();

        var options = api.Services.GetRequiredService<AiCoreOptions>();

        Assert.Empty(AiCoreOptionsValidator.Validate(options));
        Assert.Equal(30_000, options.Resilience.ProviderTimeoutMs); // values from appsettings.json are bound
        Assert.Equal(2, options.Resilience.MaxAttempts);
    }

    [Theory]
    [InlineData(8, true)]  // a tenant could hold 8 abandoned calls on a route: enough to close it for everyone
    [InlineData(4, false)] // the defaults: 4 < 8
    public void StartupWarns_WhenOneTenantCouldReachARoutesAbandonedCallLimitAlone(int tenantConcurrency, bool expected)
    {
        using var api = new TestApi(new()
        {
            ["AiCore:Quotas:Default:MaxConcurrentRequests"] = tenantConcurrency.ToString(),
            ["AiCore:Resilience:MaxAbandonedCallsPerRoute"] = "8",
        });

        using var client = api.CreateClient(); // starts the host

        Assert.Equal(expected, api.Logs.Entries.Any(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Contains("MaxAbandonedCallsPerRoute")));
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", false)]
    [InlineData("Production", false)]
    public void FaultInjectionAliasesThatHoldCapacity_AreConfiguredOnlyInDevelopment(string environment, bool expected)
    {
        using var api = new TestApi(environment: environment);

        var models = api.Services.GetRequiredService<AiCoreOptions>().Models;

        Assert.Equal(expected, models.ContainsKey("sim-timeout"));
        Assert.Equal(expected, models.ContainsKey("sim-unresponsive"));
        Assert.True(models.ContainsKey("sim-throttled")); // fails fast, holds no capacity: available everywhere
    }

    [Theory]
    [InlineData(1, 0)]   // retries disabled: any backoff is irrelevant
    [InlineData(2, 999)] // backoff below the deadline
    public void T09_BackoffRule_OnlyAppliesWhenRetriesAreEnabled(int maxAttempts, int backoffMs)
    {
        var options = new AiCoreOptions
        {
            Resilience = new ResilienceOptions { MaxAttempts = maxAttempts, RetryBackoffMs = backoffMs, ProviderTimeoutMs = 1_000 },
        };

        Assert.Empty(AiCoreOptionsValidator.Validate(options));
    }

    [Fact]
    public void T09_ZeroQueueAndZeroGrace_AreValidChoices()
    {
        var options = new AiCoreOptions
        {
            Concurrency = new ConcurrencyOptions { MaxQueuedRequests = 0 },
            Resilience = new ResilienceOptions { CancellationGraceMs = 0 },
        };

        Assert.Empty(AiCoreOptionsValidator.Validate(options));
    }
}
