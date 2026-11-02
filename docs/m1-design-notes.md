# M1 design notes

Historical M1 snapshot. For the current M3 architecture, telemetry and privacy behavior, see
[architecture.md](architecture.md), [observability.md](observability.md) and [architecture-review.md](architecture-review.md).

Short notes on how M1 interprets `docs/REQUIREMENTS.md`, and why. The full architecture overview and
decision records are M3 deliverables. This file covers only what was needed to build M1 honestly.

## Ambiguities and how M1 resolves them

| # | Requirement text | Issue | M1 resolution |
| --- | --- | --- | --- |
| A1 | FR-03 "missing/invalid credentials get 401/403" | Which status applies when? | 401 for missing **and** invalid keys (with `WWW-Authenticate: ApiKey`). 403 is reserved for an authenticated tenant that isn't permitted to do something. No such policy exists in M1, so 403 isn't reachable yet, although the handler emits the same contract if one is added. |
| A2 | FR-04 "excess calls get 429" vs FR-05 "immediate rejection" | Both are "too many requests", but they mean different things | 429 is reserved for **per-tenant quota** (M2). Exhausted **global** provider capacity returns **503 `capacity_exhausted`** with `Retry-After`, because it's a platform overload condition and an operator must be able to tell the two apart (NFR-08). |
| A3 | FR-05 "bounded waiting" (M1) vs FR-06 timeouts (M2) | Bounded waiting could mean bounded count or bounded time | M1 bounds the **number** of waiters (`MaxQueuedRequests`) and removes a waiter when the client disconnects. It doesn't bound wait **time**. Wait time is bounded indirectly by provider-call duration, and that only becomes bounded once M2 adds the provider timeout. |
| A4 | FR-07 error mapping and the mock's failure modes (FR-02) are M1 items, while failure tests T-05/T-06 are M2 | Where does M1 stop? | M1 maps provider transient → 503 `provider_unavailable`, permanent → 502 `provider_error`, and unexpected → 500, because T-10 (M1) requires "error mapping verified". **No retries** happen in M1. Retry policy, T-06 "no unlimited retries" and T-07 are M2. |
| A5 | FR-01 "prompt or message input" | Either shape satisfies the requirement | M1 accepts a single `prompt` string. A chat `messages` array would be an additive contract change. |
| A6 | "keys supplied by environment/configuration and never committed" | `appsettings.Development.json` is configuration but is committed | No key appears in any committed file. Keys come from environment variables or user secrets. Tests inject throwaway values in memory. |
| A7 | FR-01 "latency" | End-to-end or provider-only? | `latencyMs` is the provider-call duration, excluding queue wait. Queue time will be exposed as telemetry in M3. |
| A8 | "bounded response size" | Not specified how | `maxOutputTokens` (bounded by config) is passed to the provider. The mock truncates output to ~4 chars per token. |
| A9 | FR-11 startup validation is M2, yet some settings break correctness immediately | Should M1 validate anything? | Only structural failures fail at startup: limiter constructor arguments (permits ≤ 0, queue < 0), and tenant entries with blank or duplicate keys. Actionable, comprehensive validation (and T-09) remains M2. |

## Architecture (minimum for M1)

```
HTTP request
  │  correlation-ID middleware (server-generated, X-Correlation-Id)
  │  exception handler → 500 internal_error (safe body)
  │  ApiKeyAuthenticationHandler → tenant_id claim  (401 on failure; body never read)
  ▼
GenerateEndpoint        HTTP contract: content type, strict JSON, bounds, outcome → response/problem
  ▼
GenerationService       request policy: alias → route, acquire permit, call provider, map failures
  │  ProviderConcurrencyGate (System.Threading.RateLimiting.ConcurrencyLimiter)
  ▼
IModelProvider          MockModelProvider (deterministic, TimeProvider-driven latency)
```

- **One API project.** A separate `AiCore.Core` assembly doesn't pay for itself at this size (see
  REQUIREMENTS §7). The boundaries required by NFR-03 are folders/types with one-directional
  dependencies. The endpoint is the only code that knows HTTP parsing. The service has no knowledge of JSON.
- **Built-in facilities only.** Minimal APIs, the ASP.NET Core authentication pipeline, health checks,
  and the BCL `ConcurrencyLimiter` (FIFO queue with a hard limit, cancellation-aware waits). The API project
  adds no NuGet packages. The test project adds `Microsoft.AspNetCore.Mvc.Testing` (in-memory host) and
  `Microsoft.Extensions.TimeProvider.Testing` (deterministic latency in mock tests).
- **The limiter wraps only the provider call**, not the whole HTTP request. Invalid or unauthenticated
  requests therefore never consume capacity, and the bound means exactly "simultaneous provider calls" (FR-05).
- **Permit safety.** The lease is a `using` declaration around the provider call, so success, provider
  exceptions, unexpected exceptions and cancellation all release it. This is verified by tests and by a
  mutation check (see `verification.md`).
- **Strict request parsing.** Unknown JSON members are rejected, which makes "tenant is never taken from the
  body" observable and also catches client typos. The lenient `JsonSerializerDefaults.Web` behaviours are
  switched off too: member names must match exactly, numbers must not be strings, and a member may appear only
  once. Loosening a contract later is compatible; tightening it is not. Error messages echo only the JSON path,
  never values.
- **Unexpected provider exceptions are logged by type only.** Vendor SDK and HTTP-client exceptions can carry
  request content in their message, so `GenerationService` maps them to 500 `internal_error` itself and logs
  type, alias and correlation ID instead of letting the framework log the full exception. Client aborts are
  excluded and still propagate as cancellations.
- **Mock behaviour is selected by configuration per provider model**, not by magic strings in the prompt.
  Failure scenarios are therefore reachable through the real public API (`sim-*` aliases) without test-only hooks.

## Known M1 limitations (intentional, scheduled)

> **Update (M2):** provider timeouts, per-tenant quotas, the retry decision and full configuration validation
> are now implemented. See [`m2-design-notes.md`](m2-design-notes.md). The list below is kept as the M1 record.

- No provider-call timeout (M2, FR-06). A hung provider that ignores cancellation would hold a permit.
- No per-tenant quotas (M2, FR-04). One tenant can currently consume all global capacity and queue slots.
  The global bound still holds, but fairness between tenants does not exist yet.
- No retries (M2 decision, FR-10).
- No structured request telemetry, tracing or metrics (M3, FR-09). Only provider-failure warnings and
  framework logs exist. The T-08 redaction test is not written yet.
- Configuration validation is minimal (M2, FR-11 / T-09).
- Limits are per process. Multiple replicas multiply the effective bound.
- `Retry-After` is a fixed 1 s for `capacity_exhausted` and `provider_unavailable`, so clients that honour it
  retry in step. Jitter or a value derived from queue state belongs in the backpressure decision record (M3).
- Exceptions thrown outside the provider call (defects in the service itself) still reach the framework
  exception handler, which logs them in full. T-08 (M3) is where redaction is verified systematically.
