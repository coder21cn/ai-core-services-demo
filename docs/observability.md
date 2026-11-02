# M3 telemetry contract

The service emits structured JSON console logs using `ILogger`, activities from `ActivitySource`
`AiCore.Api`, and measurements from `Meter` `AiCore.Api`. No new runtime package is required.
Microsoft documents the underlying [ActivitySource APIs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs)
and [Meter APIs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation).

## Completion records

One Information-level completion event covers every request reaching the middleware, including health,
authentication/validation failures, unmatched paths, quota/capacity rejection and cancelled requests.

| Field | Meaning / privacy boundary |
| --- | --- |
| `CorrelationId` | Fresh server-generated identifier, also returned in header/body |
| `TraceId` | Current activity trace ID; can inherit W3C `traceparent`, so never an identity credential |
| `TenantRef` | Full HMAC-SHA256 of authenticated tenant ID using a random host-lifetime key; `anonymous` when unauthenticated |
| `Endpoint` | `/v1/generate`, `/health/live` or `unmatched`; never the raw URL/query |
| `ModelAlias`, `Provider` | Configured route values; canonical configured alias spelling, or `none` |
| `Result` | Stable Problem Details code, `success` or `cancelled` |
| `StatusCode` | HTTP status, telemetry-only 499 for cancellation, or 500 for an escaped failure before headers; no synthetic response is promised |
| `DurationMs` | Wall-clock elapsed time through the middleware, including parsing/admission/execution/response writing |
| `InputTokens`, `OutputTokens`, `UsageSimulated` | Usage from a successful provider result available to the endpoint; null when none is available; simulated is true |

Only configured aliases become labels. An arbitrary unknown alias, an API key, prompt, output, raw tenant
ID, JSON error path, request header or exception message is not copied into this record. Existing execution
logs keep correlation IDs and operational metadata; typed provider exception messages are no longer logged.
Unexpected service exceptions use the generic completion/error contract. The framework's full exception
diagnostics are suppressed with .NET 10's
[`SuppressDiagnosticsCallback`](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0).
This deliberately sacrifices exception stacks in normal telemetry; reproduce defects with synthetic inputs
in a controlled development environment.

Endpoint labels are captured before exception handling can clear routing metadata. An exception escaping
the pipeline is recorded as `internal_error`, even when response headers have already sent a 200 status;
in that case the record preserves the wire status and marks the span as failed. Client disconnects remain
`cancelled` and do not mark the request span as a server error, including disconnected response writes.

The HMAC key is generated per host and never persisted or exported. References can join a tenant's calls
within that host, but change after restart and across replicas. Production cross-host attribution needs a
managed/versioned pseudonym key and restricted access; it is not configured here.

## Traces

`aicore.request` is an internal child of the ASP.NET server activity. It carries correlation, tenant
reference, canonical alias/provider, result, HTTP status and simulated usage. `aicore.queue` and
`aicore.provider` are child activities for phases actually entered. The provider phase contains all
attempts/backoff/grace, records attempt count and result, and ends when the invoker returns, even if an
abandoned task continues. Request and provider 5xx failures set error status; client cancellation is
recorded separately. Queue spans cover acquisition only, including immediate rejection or cancellation.

No prompt, output or exception event is added to activities. Incoming trace context is untrusted diagnostic
context. The application does not copy headers/baggage into tags. An exporter or automatic instrumentation
that imports baggage, tracestate, URLs, headers, exceptions or vendor SDK diagnostics needs a separate
privacy review; T-08 does not certify arbitrary future instrumentation.

There is **no trace exporter or sampling policy installed**. `ActivitySource` creates an activity only when
a listener requests it. `TelemetryTests` attaches an `ActivityListener` and verifies parent/child linkage
and sensitive-marker exclusion. An optional OpenTelemetry integration would register `AddSource("AiCore.Api")`
and ASP.NET instrumentation, configure sampling and an exporter, and apply an export allowlist. That
integration remains future work, rather than a hidden prerequisite for running the demo.

## Measurements

| Instrument | Type / unit | Dimensions |
| --- | --- | --- |
| `aicore.requests` | Counter / request | endpoint, model.alias, provider.name, result, http.response.status_code |
| `aicore.request.duration` | Histogram / seconds | Same as requests |
| `aicore.queue.duration` | Histogram / seconds | model.alias, provider.name |
| `aicore.provider.duration` | Histogram / seconds | model.alias, provider.name |
| `aicore.provider.attempts` | Counter / attempt | provider.name; includes first attempt |
| `aicore.provider.retries` | Counter / retry | provider.name; attempts after the first |
| `aicore.usage.tokens` | Counter / token | model.alias, provider.name, direction (input/output), usage.simulated=true |
| `aicore.provider.permits_in_use` | Observable gauge / permit | None; includes execution/backoff/grace, excludes abandoned calls |
| `aicore.queue.depth` | Observable gauge / request | None |
| `aicore.provider.abandoned` | Observable gauge / call | None; still-running tasks after grace |
| `aicore.telemetry.failures` | Observable gauge / failure | None; cumulative local emission failures since host startup |

Durations use monotonic `Stopwatch` wall time. The API response's `latencyMs` remains the provider-phase
duration measured through the injected `TimeProvider`. Fake-clock tests therefore do not imply that wall
histograms advance with simulated deadlines. No tenant, correlation or trace identifier is a metric label.
Route configuration bounds cardinality within a host; dynamic or very large configuration still needs an
operator budget. Gauges are snapshots, not a transactionally consistent census across instruments.

Usage includes successful provider results available to the endpoint, even if the response subsequently
fails or the caller disconnects. It excludes abandoned results and failed attempts, not all billable work.
It is unsuitable for invoices, cost reconciliation or spend limits. No monetary estimate is fabricated.

Application metric emission, activity sampling/start/stop, and completion logging contain throwing
listeners/sinks. They do not change request outcomes or leak admission permits. Failures increment a local
counter without recursively logging through the broken sink; the observable failure gauge lets a healthy
collector detect them later. Emission is best-effort, so a failed callback can lose part of a record.
Activity cleanup restores the ambient parent even when a stop callback throws. Callbacks must still be
nonblocking: this is exception containment, not isolation from a listener that hangs or exhausts resources.

## Observe and verify locally

Run the API using the README commands; stdout contains JSON completion records. For offline executable
evidence of both trace and measurement emission, run:

```bash
dotnet test AiCore.slnx --filter "FullyQualifiedName~TelemetryTests|FullyQualifiedName~PrivacyRegressionTests"
```

The tests use listeners scoped to each test host's source/meter instance, inspect structured log fields,
scopes and exceptions, check trace tags/events and metric labels, and exercise success, errors, retries,
unauthenticated/invalid requests, quota/global overload, timeout and cancellation. No collector is needed.

For production operations, alert separately on sustained platform 503/5xx, queue delay, provider duration,
and abandoned calls. Treat 429 and client cancellation as separate signals; measure their prevalence
before assigning thresholds. Exclude liveness from invocation service-level calculations. Add export
health monitoring and limits on telemetry volume before depending on these signals during an incident.
