using AiCore.Api;
using AiCore.Api.Authentication;
using AiCore.Api.Configuration;
using AiCore.Api.Errors;
using AiCore.Api.Execution;
using AiCore.Api.Providers;
using AiCore.Api.Observability;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Portable structured output; do not depend on Windows Event Log privileges.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// Configuration (FR-11). Binding is strict, so an unknown key such as a misspelt setting fails startup instead
// of silently leaving the default in place. AiCoreOptionsValidator checks every value, and ValidateOnStart makes
// the host fail before it serves anything. Options are bound lazily, after all configuration sources
// (including test-host overrides) have been applied, and then exposed as immutable-by-convention singletons.
builder.Services.AddOptions<AiCoreOptions>()
    .BindConfiguration(AiCoreOptions.SectionName, binder => binder.ErrorOnUnknownConfiguration = true)
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AiCoreOptions>, AiCoreOptionsValidator>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AiCoreOptions>>().Value);
builder.Services.AddSingleton(sp => sp.GetRequiredService<AiCoreOptions>().Limits);
builder.Services.AddSingleton(sp => sp.GetRequiredService<AiCoreOptions>().Resilience);
builder.Services.AddSingleton(sp => sp.GetRequiredService<AiCoreOptions>().MockProvider);
builder.Services.AddSingleton<IReadOnlyDictionary<string, ModelRouteOptions>>(sp =>
    new Dictionary<string, ModelRouteOptions>(sp.GetRequiredService<AiCoreOptions>().Models, StringComparer.OrdinalIgnoreCase));

// The body limit comes from the validated options, so an invalid value is reported by the validator together
// with every other problem, rather than as a Kestrel exception.
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<IOptions<AiCoreOptions>>((kestrel, aiCore) =>
        kestrel.Limits.MaxRequestBodySize = aiCore.Value.Limits.MaxRequestBodyBytes);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new TenantKeyStore(sp.GetRequiredService<AiCoreOptions>().Tenants));
builder.Services.AddSingleton(sp => new TenantQuotaService(
    sp.GetRequiredService<AiCoreOptions>().Quotas,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<TenantQuotaService>>()));
builder.Services.AddSingleton(sp => new ProviderConcurrencyGate(
    sp.GetRequiredService<AiCoreOptions>().Concurrency, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ProviderInvoker>();
builder.Services.AddSingleton<IModelProvider, MockModelProvider>();
builder.Services.AddSingleton<GenerationService>();
builder.Services.AddSingleton<AiCoreTelemetry>();

builder.Services
    .AddAuthentication(ApiKeyDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks();

var app = builder.Build();

// Fail fast: resolving the options runs the validator, so invalid settings stop the process here with one
// OptionsValidationException that lists every problem. Then build the stateful singletons eagerly.
var options = app.Services.GetRequiredService<AiCoreOptions>();
var keyStore = app.Services.GetRequiredService<TenantKeyStore>();
var quotas = app.Services.GetRequiredService<TenantQuotaService>();
app.Services.GetRequiredService<ProviderConcurrencyGate>();
app.Services.GetRequiredService<AiCoreTelemetry>().ObserveCapacity(
    app.Services.GetRequiredService<ProviderConcurrencyGate>(), app.Services.GetRequiredService<ProviderInvoker>());
if (keyStore.Count == 0)
{
    app.Logger.LogWarning("No tenant API keys are configured (AiCore:Tenants); all /v1 requests will be rejected with 401.");
}

// Valid but weak: a per-tenant concurrency limit at or above the shared capacity lets one tenant fill every
// permit and queue slot, so the limit no longer isolates tenants. Allowed (single-tenant setups), but flagged.
var sharedCapacity = (long)options.Concurrency.MaxConcurrentProviderCalls + options.Concurrency.MaxQueuedRequests;
foreach (var (scope, limits) in quotas.ConfiguredLimits(app.Services.GetRequiredService<AiCoreTelemetry>().TenantReference))
{
    if (limits.MaxConcurrentRequests >= sharedCapacity)
    {
        app.Logger.LogWarning(
            "Quota MaxConcurrentRequests for {Scope} ({TenantLimit}) is not below the shared capacity of {SharedCapacity} " +
            "(MaxConcurrentProviderCalls + MaxQueuedRequests); one such tenant can occupy all provider capacity.",
            scope, limits.MaxConcurrentRequests, sharedCapacity);
    }

    // A tenant can hold up to MaxConcurrentRequests abandoned calls per route. At or above the route-wide limit, one
    // such tenant could close a route for every tenant by itself (e.g. by disconnecting from its own requests).
    if (limits.MaxConcurrentRequests >= options.Resilience.MaxAbandonedCallsPerRoute)
    {
        app.Logger.LogWarning(
            "Quota MaxConcurrentRequests for {Scope} ({TenantLimit}) is not below Resilience:MaxAbandonedCallsPerRoute ({RouteLimit}); " +
            "one such tenant can reach a route's abandoned-call limit alone and close the route for every tenant.",
            scope, limits.MaxConcurrentRequests, options.Resilience.MaxAbandonedCallsPerRoute);
    }
}

// Correlation ID: always server-generated (client values are not trusted) and echoed on every response.
// OnStarting is used so the header survives the exception handler clearing the response.
app.Use((context, next) =>
{
    var correlationId = Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlationId;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        return Task.CompletedTask;
    });
    return next(context);
});

app.UseMiddleware<RequestTelemetryMiddleware>();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Exception messages and diagnostic events may contain request data. Emit only our safe completion record.
    SuppressDiagnosticsCallback = _ => true,
    // Client aborts (OperationCanceledException on RequestAborted) are not routed here by the framework.
    ExceptionHandler = context => ApiProblem.Write(context, ApiProblem.Internal()),
});
app.UseStatusCodePages(context => ApiProblem.Write(context.HttpContext, ApiProblem.ForStatus(context.HttpContext.Response.StatusCode)));

app.UseAuthentication();
app.UseAuthorization();

// Liveness only reports that the process can serve HTTP; it deliberately runs no checks against
// model providers, so a provider outage does not cause the orchestrator to restart healthy instances.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

app.MapPost("/v1/generate", GenerateEndpoint.HandleAsync).RequireAuthorization();

app.Run();

/// <summary>Entry point; partial declaration makes it visible to WebApplicationFactory in tests.</summary>
public partial class Program;
