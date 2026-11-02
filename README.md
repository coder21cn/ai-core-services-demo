# AI Core Services — reference implementation

A compact .NET reference implementation of shared AI-platform infrastructure: a tenant-authenticated
model-invocation API with per-tenant quotas, bounded concurrency, provider timeouts and a restricted retry
policy, in front of a provider abstraction and a deterministic mock provider.

**Developed with AI assistance.** Implementation, documentation and automated reviews used coding agents.
See [provenance](docs/provenance.md) for the snapshot's origin and review limitations.

Start with the [5–10 minute demo](docs/demo.md) or the [architecture diagram](docs/architecture.md).

| Demonstrated capability | Evidence |
| --- | --- |
| Server-derived tenant identity and per-tenant quotas | Authentication and tenant-isolation tests |
| Bounded waiting, deadlines and restricted retries | Concurrency, cancellation and retry tests |
| Safe errors and telemetry without prompt/credential content | Error-contract and privacy regression tests |
| Explicit security and scaling decisions | Seven ADRs and a technical assessment |

Latest implementation verification: **216 tests passed, none skipped**, with Release CI passing on the
merged development revision. The public snapshot has its own [CI workflow](https://github.com/coder21cn/ai-core-services-demo/actions/workflows/ci.yml).
See [verification evidence](docs/verification.md) for commands, dates and historical results.

> **What this is not.** This is an educational/portfolio project. It is not AVEVA software, not a
> statement about any company's internal architecture, and not a production-ready security system.
> It calls no real LLM and needs no accounts, databases, cloud services or secrets.

**Status: M3 implementation and technical assessment; independent reviewer sign-off pending.** See
[`docs/architecture.md`](docs/architecture.md), the [seven decision records](docs/decisions/README.md),
the [telemetry contract](docs/observability.md), and the [architecture review](docs/architecture-review.md)
for implemented behavior, security/scaling trade-offs and unresolved issues.
[`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md) contains the specification and
[`docs/verification.md`](docs/verification.md) records build/test evidence. M1/M2 design notes retain
historical context. Personal interview preparation is maintained separately and is not claimed complete.

Limits are per process. Usage is simulated, there is no live model or external telemetry exporter, and
API keys are development credentials. Abandoned non-cooperative calls can outlive their permits; the
[backpressure decision](docs/decisions/003-backpressure.md) explains the availability trade-off.

## Prerequisites

- .NET SDK 10.0 (LTS). `global.json` pins `10.0.100` with `latestFeature` roll-forward.
  Target framework is `net10.0`: it is the current LTS release, and it supplies everything M1 needs
  (minimal APIs, authentication handlers, Problem Details, options validation,
  `System.Threading.RateLimiting`, `TimeProvider`), so the API project needs no extra NuGet packages.

## Build and test

```bash
dotnet build AiCore.slnx
```

```bash
dotnet test AiCore.slnx
```

All tests run in-process against `WebApplicationFactory` and mock/test providers; no network access is
required after the NuGet restore.

Optionally, check that the tests detect real defects. This needs Python 3.8+, takes about 15 minutes, and
temporarily rewrites files under `src/AiCore.Api`, so don't run it while editing them:

```bash
python3 tools/mutation_check.py
```

It injects each known defect from `docs/verification.md` one at a time, rebuilds, runs the suite, restores the
source, and fails if any defect goes undetected.

## Run locally

Real API keys must never be committed. The sample keys are **development-only**. Supply keys through environment variables
(or `dotnet user-secrets`). The values below are throwaway examples:

```bash
export AiCore__Tenants__0__TenantId=tenant-a
export AiCore__Tenants__0__ApiKey=dev-key-a-change-me
export AiCore__Tenants__1__TenantId=tenant-b
export AiCore__Tenants__1__ApiKey=dev-key-b-change-me
```

```bash
dotnet run --project src/AiCore.Api --urls http://127.0.0.1:5297
```

If no tenants are configured the service still starts, logs a warning, and rejects every `/v1` call with 401.
Invalid configuration stops the service before it serves anything; see [Configuration](#configuration).
For PowerShell setup and requests, follow the [demo guide](docs/demo.md).

### curl examples

```bash
curl -i -X POST http://127.0.0.1:5297/v1/generate \
  -H "X-Api-Key: dev-key-a-change-me" \
  -H "Content-Type: application/json" \
  -d '{"model":"chat-default","prompt":"Summarise the benefits of bounded queues."}'
```

Example response (output is deterministic for a given prompt; `latencyMs` varies):

```json
{
  "correlationId": "2c585ede11c440a2bc1151d7494e1895",
  "model": "chat-default",
  "provider": "mock",
  "providerModel": "mock-echo-v1",
  "output": "[mock-echo-v1] Simulated completion for a 41-character prompt (fingerprint e81229037f53).",
  "latencyMs": 27,
  "usage": { "inputTokens": 11, "outputTokens": 23, "totalTokens": 34, "simulated": true }
}
```

```bash
curl -i http://127.0.0.1:5297/health/live
```

```bash
curl -i -X POST http://127.0.0.1:5297/v1/generate \
  -H "X-Api-Key: dev-key-a-change-me" -H "Content-Type: application/json" \
  -d '{"model":"sim-permanent-failure","prompt":"hi"}'
```

To see a provider timeout quickly, start the service with a short deadline (for example
`export AiCore__Resilience__ProviderTimeoutMs=2000`) and call the `sim-timeout` alias. It exists only in the
Development environment, which `dotnet run` uses through `launchSettings.json`. To see a tenant
quota, send more than `BurstRequests` (default 20) requests in quick succession with one key.

## API contract

### `POST /v1/generate`

Header `X-Api-Key: <key>` is required. The tenant is derived **only** from the key; tenant identifiers
in headers or the body are never trusted (a `tenantId` body field is rejected as an unknown member).

| Field | Type | Rules |
| --- | --- | --- |
| `model` | string | Required. Logical alias from `AiCore:Models` (case-insensitive). |
| `prompt` | string | Required, non-blank, at most `AiCore:Limits:MaxPromptChars` (default 8000) characters. |
| `maxOutputTokens` | int | Optional, `1..AiCore:Limits:MaxOutputTokens` (default 1024). If omitted, `DefaultOutputTokens` (256), capped at the maximum. Bounds the response text. |

Parsing is strict. Unknown members, repeated members and member names in the wrong case (`"MODEL"`) are
rejected, and `maxOutputTokens` must be a JSON number, not a string. The whole body is also capped by Kestrel at
`AiCore:Limits:MaxRequestBodyBytes` (default 64 KiB).

Response fields: `correlationId` (also in the `X-Correlation-Id` header and always server-generated),
`model` (alias), `provider`, `providerModel`, `output`, `latencyMs` (duration of the provider phase, including
any retries and backoff but excluding time spent waiting in the queue) and `usage` (estimated tokens of the
successful attempt, `simulated: true`).

Configured aliases:

| Alias | Mock behaviour |
| --- | --- |
| `chat-default` | Success, 25 ms simulated latency |
| `chat-slow` | Success, 2 s latency (useful for observing back-pressure) |
| `sim-transient-failure` | Always a transient failure with unknown outcome. Not retried, 503 |
| `sim-permanent-failure` | Always a permanent provider failure. Not retried, 502 |
| `sim-throttled` | Every attempt throttled before processing. Retried up to `MaxAttempts`, then 503 |
| `sim-throttled-once` | First attempt throttled before processing; the retry succeeds, 200 |
| `sim-timeout` | **Development only.** 10 min latency, honours cancellation, so 504 at `ProviderTimeoutMs` |
| `sim-unresponsive` | **Development only.** 2 min latency, **ignores** cancellation (a defective adapter): 504 after `ProviderTimeoutMs + CancellationGraceMs`, logged as abandoned |

The two Development-only aliases hold provider capacity for as long as the deadline allows, so they are
defined in `appsettings.Development.json` rather than `appsettings.json`. Any other environment, including
the test host, doesn't have them. The other `sim-*` aliases fail within milliseconds and exist everywhere.

### `GET /health/live`

Anonymous. Returns `200 Healthy` if the process serves HTTP. It deliberately does not check model
providers, so a provider outage doesn't cause healthy instances to be restarted. `/health/ready` is not
implemented.

### Errors

All errors use `application/problem+json` (RFC 9457) with two extensions: `code` (stable; branch on it)
and `correlationId`. Messages are authored by the service. Exception text, prompt content and credentials
are never returned to callers. `type` is this README's URL with the fragment `#error-<code>`, which links to
the matching row in the public demo documentation below.

| HTTP | `code` | Meaning |
| --- | --- | --- |
| 400 | <a id="error-invalid_request"></a>`invalid_request` | Malformed JSON, unknown member, or field validation failure (`errors` lists fields) |
| 400 | <a id="error-unknown_model"></a>`unknown_model` | Alias not configured |
| 401 | <a id="error-unauthenticated"></a>`unauthenticated` | Missing or invalid API key (`WWW-Authenticate: ApiKey`) |
| 403 | <a id="error-forbidden"></a>`forbidden` | Reserved; no authorization policies exist yet |
| 404 / 405 | <a id="error-not_found"></a>`not_found` / <a id="error-method_not_allowed"></a>`method_not_allowed` | Unknown path / method |
| 413 | <a id="error-request_too_large"></a>`request_too_large` | Body exceeds `MaxRequestBodyBytes` (enforced by Kestrel; not by the in-memory test host) |
| 415 | <a id="error-unsupported_media_type"></a>`unsupported_media_type` | Body is not `application/json` |
| 429 | <a id="error-rate_quota_exceeded"></a>`rate_quota_exceeded` | The tenant's request-rate quota is used up; `Retry-After` is the time until the next request is allowed |
| 429 | <a id="error-concurrency_quota_exceeded"></a>`concurrency_quota_exceeded` | The tenant already has `MaxConcurrentRequests` requests queued or in flight; `Retry-After` is 1 s, or the time until the next rate token if that is longer |
| 502 | <a id="error-provider_error"></a>`provider_error` | Provider failed permanently; retrying will not help |
| 503 | <a id="error-provider_unavailable"></a>`provider_unavailable` | Provider transient failure (after any platform retries), or the requested model's route has `MaxAbandonedCallsPerRoute` abandoned calls still running, or this tenant's own abandoned calls on that route plus its requests in progress reach its `MaxConcurrentRequests`. For these local refusals the `detail` says that no request was sent to the provider; `Retry-After: 1` |
| 503 | <a id="error-capacity_exhausted"></a>`capacity_exhausted` | All provider-call permits and queue slots are in use, or no permit became free within `MaxQueueWaitMs`; `Retry-After: 1` |
| 504 | <a id="error-provider_timeout"></a>`provider_timeout` | The provider phase exceeded `ProviderTimeoutMs`. No `Retry-After`: the provider may still have completed, and billed, the request |
| 500 | <a id="error-internal_error"></a>`internal_error` | Unexpected failure or misconfiguration; quote the correlation ID |

429 always means "this tenant's own usage" and 503 always means "the platform or provider", so a client
(and an operator) can tell them apart. Global overload is 503 because it is a platform capacity condition,
not something the calling tenant did.

A client that disconnects gets no response. Its queued request leaves the queue, or its provider call is
cancelled, and its capacity is returned.

## Request admission and reliability

Each `/v1/generate` request passes these stages in order. A request rejected at one stage holds nothing
from a later one.

1. **Authentication and validation.** Invalid requests consume no quota and no capacity.
2. **Tenant quota (429).** Each tenant has its own token bucket (`RequestsPerMinute` refill,
   `BurstRequests` capacity) and its own limit on requests queued or in flight (`MaxConcurrentRequests`).
   One tenant exhausting either limit has no effect on another tenant. A request that is admitted but never
   reaches the provider (global capacity rejection, queue timeout, client disconnect while queued) gets its
   token back. Once the provider has been called, the token is spent whatever the outcome. This includes
   failures the provider reports as not processed, because the quota limits provider calls and protects the
   provider from client retry storms (see `docs/m2-design-notes.md` §4.1).
3. **Global provider capacity (503).** `MaxConcurrentProviderCalls` (default 8) caps provider-phase permits
   process-wide, including retries and cancellation grace. This also bounds physical calls for cooperative
   adapters; abandoned calls that ignore cancellation have separate limits described below. Up to
   `MaxQueuedRequests` (default 16; `0` = reject immediately) callers wait FIFO, each for at
   most `MaxQueueWaitMs`. Further callers get `503 capacity_exhausted` straight away, so the queue is bounded
   in both length and wait time.
4. **Provider phase (502/503/504).** `ProviderTimeoutMs` (default 30 s) is one deadline covering every attempt
   and backoff. On timeout the provider's cancellation token fires and the caller gets `504 provider_timeout`.
   The permit is held until the provider has actually stopped, so the in-flight bound stays exact. If the call
   finishes within the grace period, its real outcome is used instead of a 504: a success is returned (the
   completion exists and was billed), a "not processed" failure stays 503 and a permanent failure stays 502.
   If an adapter ignores cancellation for more than `CancellationGraceMs` (default 1 s), the call is **abandoned**:
   - it is logged as an error;
   - its global permit and its tenant's concurrency slot are released, so a hung provider can never hold
     capacity indefinitely, and the tenant is neither blamed with a 429 nor locked out of healthy models;
   - it still counts against its tenant's `MaxConcurrentRequests`, **on its route only**, until it really ends.
     A tenant whose requests in progress plus abandoned calls on a route reach that limit is refused on that
     route alone with 503. This caps how much of a route's budget one tenant can use, for example by
     disconnecting from its own requests;
   - it counts against its **route** (provider + model). While a route has `MaxAbandonedCallsPerRoute`
     (default 8) abandoned calls running, new calls to that route fail fast with 503 for every tenant, checked
     both before queueing and after a permit is granted. Other models, even on the same provider, are unaffected.
     Orphans per route therefore stay below `MaxAbandonedCallsPerRoute + MaxConcurrentProviderCalls`. With the
     defaults (4 per tenant, 8 per route) no single tenant can reach a route's limit alone, and a startup warning
     flags a configuration where it could. Reaching and leaving the limit are each logged once, and refused
     requests only at Debug.

   Each attempt starts on the thread pool, so an adapter that blocks before returning its task can't defeat the
   deadline either.

Every permit and tenant slot is released on every path: success, failure, exception, timeout and client
cancellation. Admission and execution take roughly `MaxQueueWaitMs + ProviderTimeoutMs + CancellationGraceMs`
at most under normal scheduling; this excludes body upload, response transfer and scheduler starvation.

### Retry policy

The platform retries **only** failures that the provider guarantees it did not process
(`ProviderTransientException.RequestNotProcessed`, e.g. throttled at admission or connection refused before
sending). It makes at most `MaxAttempts` (default 2, max 5) attempts in total, with a fixed `RetryBackoffMs`
(default 250 ms) between them and within the same deadline. It never retries these:

- **Timeouts and ambiguous transient failures** (connection reset after sending, 5xx with unknown state). An LLM
  call is not idempotent: the provider may already have generated, and billed, a completion. A retry could
  produce a second, separately billed and possibly different answer.
- **Permanent failures and unexpected exceptions.** Retrying can't help.

A retried request uses one quota token, not one per attempt. The reasoning, the alternatives and the
circuit-breaker discussion are in [`docs/m2-design-notes.md`](docs/m2-design-notes.md).

### Per-process limits

All of these limits are in-process. With N replicas, a tenant's effective quota and the global in-flight
bound are both N times the configured values, and a replica restart resets its quota state. Distributed
enforcement (for example a Redis-backed token bucket) is discussed in `docs/m2-design-notes.md`; it is not implemented.

## Configuration

All settings live under `AiCore` in `appsettings.json` and can be overridden with environment variables
(`AiCore__Section__Key`, e.g. `AiCore__Concurrency__MaxConcurrentProviderCalls=4`).

| Key | Default | Rule |
| --- | --- | --- |
| `Limits:MaxPromptChars` | 8000 | ≥ 1 |
| `Limits:MaxOutputTokens` | 1024 | ≥ 1 |
| `Limits:DefaultOutputTokens` | 256 | ≥ 1 (capped at `MaxOutputTokens` when used) |
| `Limits:MaxRequestBodyBytes` | 65536 | ≥ 1 |
| `Concurrency:MaxConcurrentProviderCalls` | 8 | ≥ 1 |
| `Concurrency:MaxQueuedRequests` | 16 | ≥ 0 |
| `Concurrency:MaxQueueWaitMs` | 10000 | ≥ 1 |
| `Resilience:ProviderTimeoutMs` | 30000 | ≥ 1 |
| `Resilience:CancellationGraceMs` | 1000 | ≥ 0 |
| `Resilience:MaxAttempts` | 2 | 1–5 (1 disables retries) |
| `Resilience:RetryBackoffMs` | 250 | ≥ 0, and below `ProviderTimeoutMs` when `MaxAttempts` > 1 |
| `Resilience:MaxAbandonedCallsPerRoute` | 8 | ≥ 1 |
| `Quotas:Default:RequestsPerMinute` | 60 | ≥ 1 |
| `Quotas:Default:BurstRequests` | 20 | ≥ 1 |
| `Quotas:Default:MaxConcurrentRequests` | 4 | ≥ 1 |
| `Quotas:Tenants:<tenantId>:<field>` | (none) | Optional per-tenant override of any `Quotas:Default` field; the tenant ID must match a configured tenant |

Validation runs at startup and reports **every** problem in one error, each naming the key, the rule and the
value, for example:

```text
Microsoft.Extensions.Options.OptionsValidationException: AiCore:Concurrency:MaxConcurrentProviderCalls must be
at least 1 but is 0 (the maximum simultaneous provider calls per process).; AiCore:Resilience:ProviderTimeoutMs must
be at least 1 but is -1 (the deadline for the provider phase of a request).
```

Binding is strict. An unknown key under `AiCore` (for example a misspelt `MaxConcurentProviderCalls`), a
non-numeric value, or an undefined enum value (such as a mock `Behavior` of `7`) also stops startup, instead of
silently falling back to a default. A valid but weak
setting, a per-tenant `MaxConcurrentRequests` at or above `MaxConcurrentProviderCalls + MaxQueuedRequests`
(which lets one tenant occupy all capacity), only logs a startup warning.

## Observability

Each request emits a JSON completion log with correlation/trace ID, pseudonymous tenant reference,
configured model/provider, result, duration and simulated usage. Prompt/output text, API keys, raw tenant
IDs and exception messages are excluded from application request telemetry. Tenant references are stable
within one host lifetime and change across restarts/replicas.

`ActivitySource` and `Meter` named `AiCore.Api` expose request, queue and provider instrumentation, attempt/retry
counts, simulated tokens, queue depth, permit use and abandoned calls. Metric labels exclude tenant/request
identifiers and arbitrary caller-supplied aliases. Traces require a listener; no exporter or backend is
installed. See [observability.md](docs/observability.md) for the schema and local verification commands.

## Security notes

- The `X-Api-Key` scheme is a development stand-in. In production, validate OIDC/OAuth2 access tokens
  (e.g. client-credentials for services, workload identity for in-cluster callers) using the standard
  JWT bearer handler, and map a verified `tenant` claim to the same `tenant_id` claim this service uses.
  Nothing downstream of authentication would change.
- The key store indexes SHA-256 digests of keys and looks up by digest (no early-exit string comparison
  of secrets). Plaintext keys still exist in the configuration system's memory, as with any config-sourced secret.
- Provider failure, timeout, retry and abandonment logs contain operational metadata and correlation IDs,
  with only the type for unexpected provider exceptions. All provider exception messages are omitted.
  Full framework diagnostics for handled request exceptions are suppressed; the completion event records
  `internal_error`. Parsing errors omit JSON paths because property names are caller-controlled.
  T-08 checks structured logs/scopes, trace tags/events, metric labels and error responses. Future SDKs,
  exporters and infrastructure logs require their own privacy review.
- Configuration validation errors name the key of a bad or duplicate API key, never its value.

## Repository layout

```text
src/AiCore.Api/
  Program.cs               composition root, middleware order, endpoint mapping
  GenerateEndpoint.cs      HTTP contract: parsing, validation, response mapping
  Authentication/          dev API-key handler and tenant key store
  Execution/               request policy: tenant quotas, concurrency gate, provider invoker
                           (deadline, retry, cancellation) and the orchestrating GenerationService
  Providers/               IModelProvider abstraction and deterministic MockModelProvider
  Observability/           safe completion logs, ActivitySource, Meter and tenant pseudonyms
  Errors/                  Problem Details error contract
  Configuration/           options types and startup validator
tests/AiCore.Tests/        xUnit tests against the in-memory test host and unit tests
docs/                      requirements, design notes, verification evidence
```
