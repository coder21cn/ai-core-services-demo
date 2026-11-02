using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiCore.Api.Execution;
using AiCore.Api.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests.Infrastructure;

/// <summary>
/// In-memory test host for the real API pipeline (auth, routing, validation, quotas, gate, provider, error
/// mapping). No network, no external services. Test-only API keys are injected via configuration.
/// Tenant quotas are set high by default so tests about other behaviour are not shaped by them; quota tests
/// override them. Pass a <see cref="FakeTimeProvider"/> to control timeouts, backoff and quota refill.
/// </summary>
public sealed class TestApi : WebApplicationFactory<Program>
{
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";
    public const string KeyA = "test-only-key-a";
    public const string KeyB = "test-only-key-b";

    /// <summary>Alias routed to the injected test provider (when one is supplied).</summary>
    public const string TestAlias = "test-model";

    private readonly Dictionary<string, string?> _settings;
    private readonly IModelProvider? _extraProvider;
    private readonly TimeProvider? _timeProvider;
    private readonly string _environment;

    public TestApi(
        Dictionary<string, string?>? settings = null,
        IModelProvider? extraProvider = null,
        TimeProvider? timeProvider = null,
        string environment = "Testing")
    {
        _settings = new Dictionary<string, string?>
        {
            ["AiCore:Tenants:0:TenantId"] = TenantA,
            ["AiCore:Tenants:0:ApiKey"] = KeyA,
            ["AiCore:Tenants:1:TenantId"] = TenantB,
            ["AiCore:Tenants:1:ApiKey"] = KeyB,
            [$"AiCore:Models:{TestAlias}:Provider"] = "test",
            [$"AiCore:Models:{TestAlias}:ProviderModel"] = "test-model-v1",
            ["AiCore:Quotas:Default:RequestsPerMinute"] = "1000000",
            ["AiCore:Quotas:Default:BurstRequests"] = "1000000",
            ["AiCore:Quotas:Default:MaxConcurrentRequests"] = "1000000",
            // Cooperative test providers stop within microseconds; a long grace keeps a slow CI machine from
            // turning that into an "abandoned call". Abandonment tests set it explicitly.
            ["AiCore:Resilience:CancellationGraceMs"] = "10000",
        };
        foreach (var (key, value) in settings ?? new())
        {
            _settings[key] = value;
        }

        _extraProvider = extraProvider;
        _timeProvider = timeProvider;
        _environment = environment;
    }

    public ProviderConcurrencyGate Gate => Services.GetRequiredService<ProviderConcurrencyGate>();

    public TenantQuotaService Quotas => Services.GetRequiredService<TenantQuotaService>();

    public ProviderInvoker Invoker => Services.GetRequiredService<ProviderInvoker>();

    /// <summary>Every log entry written by the host (all categories and levels), for redaction assertions.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(Logs));
        // Tests must not read or create keys in the developer's profile, including the key-ring warmup service.
        builder.ConfigureServices(services => services.AddDataProtection()
            .AddKeyManagementOptions(options => options.XmlRepository = new MemoryKeyRepository()));
        if (_extraProvider is not null)
        {
            // Added alongside (not instead of) the mock: a second adapter is introduced without
            // touching the public endpoint (FR-12).
            builder.ConfigureTestServices(services => services.AddSingleton(_extraProvider));
        }

        if (_timeProvider is not null)
        {
            builder.ConfigureTestServices(services => services.AddSingleton(_timeProvider));
        }
    }

    public HttpClient CreateClientFor(string? apiKey)
    {
        var client = CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    public static Task<HttpResponseMessage> PostGenerate(
        HttpClient client, string model, string prompt, CancellationToken cancellationToken = default) =>
        client.PostAsJsonAsync("/v1/generate", new { model, prompt }, cancellationToken);

    public static Task<HttpResponseMessage> PostRaw(HttpClient client, string body, string contentType = "application/json") =>
        client.PostAsync("/v1/generate", new StringContent(body, Encoding.UTF8, contentType));

    /// <summary>
    /// Waits for an observable condition (not a fixed sleep). Fails the test after a generous deadline
    /// rather than hanging.
    /// </summary>
    public static async Task WaitUntil(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not reached: {description}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Advances fake time in steps until a condition holds. Used when the code under test registers a timer at
    /// a moment the test cannot observe (e.g. a grace period that starts after a timeout fires). The outcome is
    /// deterministic; only the number of steps varies.
    /// </summary>
    public static async Task AdvanceUntil(FakeTimeProvider time, TimeSpan step, Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not reached: {description}");
            }

            time.Advance(step);
            await Task.Delay(5);
        }
    }

    public static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    private sealed class MemoryKeyRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = new();
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements) return _elements.Select(e => new XElement(e)).ToArray();
        }
        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements) _elements.Add(new XElement(element));
        }
    }
}
