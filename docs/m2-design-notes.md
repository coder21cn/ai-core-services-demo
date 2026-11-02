# M2 design notes: reliability and tenant controls

Historical M2 snapshot. M3 now adds telemetry and privacy fixes; see [architecture.md](architecture.md),
[observability.md](observability.md), [decisions/](decisions/README.md) and [architecture-review.md](architecture-review.md).

How M2 interprets `docs/REQUIREMENTS.md`, what it implements, and where it stops. The full architecture overview
and formal decision records are M3 deliverables. This file records the M2 choices, the alternatives that were
considered, and the limitations that must not be mistaken for implemented capability.

**Implemented in M2:** per-tenant request-rate and concurrency quotas (FR-04), a time bound on queue waiting
(FR-05), a provider-phase deadline with cancellation propagation and bounded abandonment (FR-06, NFR-05), a
retry policy restricted to failures the provider guarantees were not processed (FR-10), and strict, validated
configuration (FR-11). Tests: T-03, T-05, T-06, T-07 and T-09 (see `verification.md`).

**Not implemented:** distributed enforcement, token-based (TPM) quotas, usage reporting, a circuit breaker,
idempotency keys, streaming, and request telemetry/metrics (M3). These are discussed below as design only.

## 1. Interpretations

| # | Requirement text | Issue | M2 resolution |
| --- | --- | --- | --- |
| B1 | FR-04 "isolate per-tenant limits" | Which resource is a quota? | Two limits per tenant. **Request rate**: a token bucket (`RequestsPerMinute` refill, `BurstRequests` capacity). **Concurrency**: requests queued or in flight (`MaxConcurrentRequests`). Token-based (TPM) quotas are deferred (§3.4). |
| B2 | FR-04 "429 with coherent semantics" | When is a request "excess", and what does a client do with 429? | 429 is used **only** when the tenant's own usage caused the rejection, with distinct codes `rate_quota_exceeded` (`Retry-After` = time until the next token) and `concurrency_quota_exceeded` (`Retry-After: 1`). Platform conditions stay 503. Invalid requests never consume quota. A request that never reaches the provider gets its token back. |
| B3 | FR-06 "provider-call timeout" | Per attempt or per request? Does queue time count? | One deadline (`ProviderTimeoutMs`) covers the whole provider phase, all attempts and backoff, so retries can't stretch it. Queue waiting has its own bound (`MaxQueueWaitMs`, B4). |
| B4 | FR-05 "bounded waiting" (M1 bounded the count only, see M1 A3) | Unbounded wait *time* remained | `MaxQueueWaitMs` (default 10 s). A request still waiting at that point leaves the queue and gets 503 `capacity_exhausted` with a distinct detail. |
| B5 | FR-10 "bounded retry only for an explicitly retryable failure, **or** an explicit policy disabling retries" | Which option? | Bounded retry, restricted to failures whose adapter sets `RequestNotProcessed`. All other failures get exactly one attempt. §4 explains why. |
| B6 | FR-07 "timeout … distinguishable" | Which status? | 504 `provider_timeout`, **without** `Retry-After`: unlike 503, the outcome is unknown and the provider may have billed the work. |
| B7 | NFR-05 "a failing provider cannot leave permits held indefinitely" vs FR-05 "bound simultaneous provider calls" | They conflict when an adapter ignores cancellation: either the permit is held forever, or the bound stops counting a call that is still running | The permit is held while the cancelled call stops, up to `CancellationGraceMs`, so the bound stays exact for well-behaved adapters. After that the call is **abandoned**: logged at Error, and its global permit and tenant slot released. It still counts against its tenant's `MaxConcurrentRequests` on that route only, and against a per-route cap (`MaxAbandonedCallsPerRoute`, route = provider + model) that stops new calls to a model that keeps hanging. §3.3. |
| B8 | FR-11 "invalid … settings fail with actionable errors" | What counts as invalid, and what is merely unwise? | Values that break behaviour fail startup. Each message names the key, the rule and the value, and all failures are reported together. Unknown keys and non-numeric values also fail (strict binding). Valid but weak settings only log a warning, e.g. a per-tenant concurrency limit ≥ shared capacity, which is legitimate for single-tenant use. M1's choice to cap (not reject) `DefaultOutputTokens > MaxOutputTokens` is kept. |

## 2. Admission pipeline

```
POST /v1/generate
  │ authentication (401) → strict JSON + validation (400/415) → alias lookup (400)
  │                                        └─ nothing below is consumed by an invalid request
  │
  │ route (provider + model) has ≥ MaxAbandonedCallsPerRoute abandoned calls → 503 provider_unavailable (no quota used)
  ▼
TenantQuotaService.TryAdmit(tenant, route)   per-tenant lock; concurrency, then the tenant's abandoned calls on
  │                                            this route (503 here only), then rate
  │  rejected → 429 concurrency_quota_exceeded | rate_quota_exceeded (no token spent, no slot held)
  │  admitted → TenantQuotaLease (slot + token)            ── disposed last ──┐
  ▼                                                                         │
ProviderConcurrencyGate.AcquireAsync         FIFO, ≤ MaxQueuedRequests, ≤ MaxQueueWaitMs
  │  queue full / wait timed out → 503 capacity_exhausted (token refunded)  │
  │  client abort while queued → leaves queue (token refunded)              │
  │  acquired → permit                                   ── disposed first ─┤
  │  route reached its abandoned-call limit while queued → 503 (token refunded)
  ▼                                                                         │
client already gone? → OperationCanceledException (token refunded, provider never called)
lease.MarkProviderCallStarted()              from here the token is spent   │
  ▼                                                                         │
ProviderInvoker.InvokeAsync                  deadline = ProviderTimeoutMs   │
  │  attempt n → success (200)                                              │
  │           → transient + RequestNotProcessed, n < MaxAttempts → backoff, retry (not if the deadline
  │             passed, the client left or the route reached its abandoned-call limit: 503, nothing sent)
  │           → transient (ambiguous / retries exhausted) → 503 provider_unavailable
  │           → permanent → 502 provider_error;  unexpected → 500 (type logged only)
  │  deadline → cancel token → wait ≤ CancellationGraceMs for the adapter
  │           → stopped with a result: 200 (it exists and was billed) | "not processed": 503 | permanent: 502
  │           → stopped otherwise: 504 provider_timeout | still running: abandoned (counted per route), 504
  │  client abort → cancel token → same wait → OperationCanceledException (no response)
  ▼                                                                         │
using-declarations release permit, then tenant slot (and refund if unspent) ┘
```

Ordering rationale:

- **Tenant checks before the shared queue.** A tenant over its quota never occupies a shared queue slot, so it
  can't push another tenant into 503.
- **Concurrency before rate inside the tenant check.** A tenant that is merely busy doesn't also lose a token.
- **Token reserved at admission, refunded on dispose unless the provider was called.** Checking the token only
  after acquiring a permit would let a tenant fill queue slots with requests it can't afford. Checking it before
  and consuming it after would admit more than the quota under concurrency. Reserve-then-refund is atomic per
  tenant and makes every rejection *before the provider is called* quota-neutral (capacity exhausted,
  queue-wait timeout, client abort while queued, abandoned-call limit). Once the provider has been called the
  token stays spent, even for "not processed" failures. §4.1 explains why.

Worst-case request duration is about `MaxQueueWaitMs + ProviderTimeoutMs + CancellationGraceMs` plus parsing,
41 s with the defaults.

## 3. Design details

### 3.1 Quotas: algorithm and isolation

- **Token bucket** rather than a fixed window: no 2× burst at window boundaries, a natural burst allowance, and
  an exact `Retry-After` (time until the bucket holds one token). Credit is an integer: one token is
  `TimeSpan.TicksPerMinute` units, and each elapsed tick earns `RequestsPerMinute` units. Partial refills
  therefore add up exactly. The first version used `double` and drifted: refills of 0.7 s, 0.2 s and 0.1 s
  summed to 0.9999999999999999 tokens, so a client retrying at the advertised `Retry-After` was rejected again.
  The refill compares elapsed time with the time needed to fill the bucket before multiplying, so long idle
  periods can't overflow. State is one small object per tenant, guarded by a per-tenant lock, so
  check-and-take is atomic and tenants never contend with each other.
- **`Retry-After` for a concurrency rejection** is 1 s, or the time until the next token if that is longer, so a
  client that honours it is not immediately rejected for rate instead.
- **Clock:** `TimeProvider`. Tests use `FakeTimeProvider`, so refill happens only when a test advances time (NFR-02).
- **Isolation guarantee, stated precisely.** Tenant A's quota usage never changes tenant B's admission
  decision, because their state is separate. A's share of the *shared* permits and queue slots is capped at
  `MaxConcurrentRequests`. This does **not** give B reserved capacity. If many tenants together exceed the
  shared capacity, every one of them can receive 503. That is real platform overload and is reported as such.
  Reserved/guaranteed capacity per tenant (a sum of reservations ≤ capacity) is an enterprise option, not
  implemented.
- **Fairness.** The shared queue is FIFO across tenants. The per-tenant cap bounds how much of it one tenant
  can hold, but there is no weighted fair queuing, so a tenant at its cap with long calls still holds its whole
  share for their full duration.
- **Per-tenant overrides** (`Quotas:Tenants:<id>`) replace only the fields they set. An override for an
  unknown tenant ID fails startup, because it is almost always a typo that would silently leave the intended
  tenant on default limits.
- **State growth.** Buckets are keyed by authenticated tenant IDs only, so anonymous traffic can't grow the map.

### 3.2 Timeouts

`CancellationTokenSource(timeout, TimeProvider)` is linked with the client's `RequestAborted` token and passed
to the adapter. The invoker awaits `call.WaitAsync(token)`, so the request is released at the deadline even
if the adapter never looks at its token. Each attempt is started with `Task.Run`, so an adapter that blocks
*before* returning its task (sync-over-async in a vendor SDK, a blocking DNS or TLS handshake) can't keep the
invoker from reaching that wait. Without it, such an adapter held the permit and tenant slot indefinitely. The
cost is one thread-pool hop per attempt, and a blocking adapter still occupies a pool thread. A timeout is
**not retried** (§4).

### 3.3 Cancellation, grace and abandonment

On a timeout or client abort, the invoker waits up to `CancellationGraceMs` (default 1 s) for the adapter's
task to finish before releasing the permit:

- **Cooperative adapter** (the normal case): it stops within the grace period, and the permit is released only
  once the call has actually ended. The in-flight bound therefore stays exact even during cancellation storms.
  `T05_AfterTimeout_PermitIsHeldUntilTheProviderActuallyStops` asserts this.
- **Call completes during the grace period:** its real outcome is used. A success is returned as 200, because the
  completion exists and was billed, and answering 504 would invite the client to pay for a second one. A "not
  processed" failure stays a safe 503, and a permanent failure stays 502 ("retrying won't help"). Anything else is
  reported as a timeout.
- **Adapter that ignores cancellation** (a defect): after the grace period the call is abandoned. The caller
  still gets 504 (or nothing, if it disconnected), and an Error log plus the per-route abandoned counter make the
  defect visible. Reaching the route's limit is logged once at Warning and dropping below it once at
  Information. Refused requests are logged at Debug only, so a looping client can't flood the log during the
  incident. A continuation observes the task's eventual result, so it raises no unobserved-exception noise, and
  decrements the counter.

**Who keeps what when a call is abandoned:**

| Resource | Released | Why |
| --- | --- | --- |
| Global permit | Immediately | Liveness: a hung provider must not starve every tenant (NFR-05) |
| Tenant concurrency slot | Immediately | The tenant must not be blamed with a 429 or locked out of healthy routes (see below) |
| The tenant's share of **that route** | When the abandoned call really ends: it counts against the tenant's `MaxConcurrentRequests` on that route only (503 there) | One tenant can't use up a route's shared budget alone, even by disconnecting from its own requests |
| New calls to that **route** (provider + model) | Refused (503) while it has `MaxAbandonedCallsPerRoute` orphans; checked before queueing **and** after a permit is granted | Caps the orphans a hanging model can accumulate, for every tenant together, without affecting other models |

**Why per route, and per tenant within a route.** The design went through three review rounds:

1. **First revision:** the tenant's concurrency slot stayed held until the abandoned call ended. This blamed the
   tenant for a provider defect: it got 429 `concurrency_quota_exceeded` ("your own usage") with a
   `Retry-After: 1` that never came true. And because the slot isn't tied to a provider, a model that never
   returned locked the tenant out of *healthy* providers indefinitely.
2. **Second revision:** the slot was released and orphans were bounded per route only. That left the route's
   shared budget unprotected from a single tenant. The cheapest attack was to disconnect from one's own
   requests: with the defaults, 8 aborted calls in about 2 s closed a slow-but-working route for every tenant,
   at 24 requests a minute, inside the tenant's own quota.
3. **Current design:** both checks.
   - Per (tenant, route): a tenant's requests in progress plus its abandoned calls on a route never exceed its
     `MaxConcurrentRequests`. Only that tenant is refused, only on that route, with 503. It stays free on every
     other route.
   - Per route: a backstop for many tenants together (a model that genuinely hangs for everyone).

   With `MaxConcurrentRequests` < `MaxAbandonedCallsPerRoute` (4 < 8 by default), no tenant can close a route
   for others by itself. Program.cs logs a startup warning for any tenant whose limit isn't below the route
   limit.

Keying per route rather than per provider matters for the same reason: one hanging model must not take down
every model behind that vendor. That is also the scope §4.3 recommends for a circuit breaker.

**Why client aborts still count.** Review suggested, as an alternative, leaving client-abort abandonments out of
the route-wide count, because they say nothing about the route's health. They do still consume the upstream (a
running call, possibly billed), and the route-wide limit exists to cap exactly that across tenants. The
per-tenant check already removes the one-tenant attack, so both kinds of abandonment are counted the same way.

**Refusal response.** All abandoned-call refusals return 503 `provider_unavailable`, the same code as a provider
transient failure, because the client should react the same way. The detail is different, though: "calls to this
model are paused ...; no request was sent to the provider". So a client or operator can tell a local refusal,
which is quota-neutral and sent nothing upstream, from a failure the provider reported. A tenant reaching or
leaving its share of a route is logged once each (Warning, Information), without the tenant ID. Refusals
themselves are logged only at Debug. Retries are refused on the same terms as first attempts.

**Bound and trade-off:** one tenant's orphans per route are at most its `MaxConcurrentRequests`. The check is
made under the tenant's lock. An abandonment moves a request from "in progress" to "abandoned" in one locked step
(`TenantQuotaService.MoveToAbandoned`), so the sum never counts it twice. Before review round 4 the slot was freed only
when the lease was disposed, which briefly refused the tenant on that route one call early. Orphans per route in total are at most `MaxAbandonedCallsPerRoute + MaxConcurrentProviderCalls - 1`.
Calls already holding a permit when the limit is reached can still be abandoned, and queued requests re-check the
limit after their permit is granted, so they can't add more. Without that re-check, all queued requests could
also hang, giving about 31 orphans with the defaults. Orphans keep consuming resources (a socket, a
provider-side slot, possibly billing) outside the permit bound. If a model never returns at all, its route stays
refused. That makes the outage visible and contained rather than silent, but recovery then needs the adapter
fixed or the process restarted. A circuit breaker (§4.3) would add a time-based recovery probe.

Client cancellation follows the same path, so a disconnecting client can't free a permit before its provider
call has really stopped.

## 4. Retry policy and ambiguous, potentially billable completion

An LLM inference call is **not idempotent**. If the provider received the request, it may have generated a
completion and billed for it, even when the response never arrived. A retry after such a failure can:

- bill the tenant (or platform) twice for one logical request,
- return a *different* answer (sampling is nondeterministic), so the caller can't even dedupe on content,
- trigger side effects twice if the completion drives tools or agents downstream,
- amplify load on a provider that is already struggling.

The platform therefore distinguishes failures by **what is known about processing**, not by "is it transient":

| Failure | Known processed? | Platform retries? | Caller sees |
| --- | --- | --- | --- |
| Transient, `RequestNotProcessed = true` (throttled at admission, connection refused before sending) | Known **not** processed | Yes, up to `MaxAttempts` total (default 2, max 5), fixed `RetryBackoffMs`, within the same deadline | 200 if a retry succeeds, else 503 `provider_unavailable` |
| Transient, default (connection reset after send, 5xx with unknown state) | Unknown | **No** | 503 `provider_unavailable` |
| Deadline exceeded | Unknown | **No** | 504 `provider_timeout`, no `Retry-After` |
| Deadline exceeded *during backoff* (every attempt so far was "not processed") | Known not processed | No (out of time) | 503 `provider_unavailable`, not 504, because nothing can have been billed |
| Deadline exceeded, but the call **completes within the grace period** | Known | No | 200 with the result if it succeeded (it exists and was billed); 503 if it was "not processed"; 502 if it failed permanently; otherwise 504 |
| Permanent (invalid model, content rejected, provider-side auth) | Irrelevant | **No**: retrying can't help | 502 `provider_error` |
| Unexpected exception (adapter or SDK bug) | Unknown | **No** | 500 `internal_error` |

Further properties:

- **Adapters own the classification.** Only the adapter can know whether the vendor guarantees non-processing
  (e.g. a documented "rejected before processing" 429). The default is `false`: an adapter that doesn't know
  must not claim it.
- **Bounded amplification.** At most `MaxAttempts` provider calls per admitted request. Validation caps
  `MaxAttempts` at 5 and requires `RetryBackoffMs < ProviderTimeoutMs`.
- **Quota charging.** A request is charged one token however many attempts it takes. The failed attempts were,
  by definition, not processed.
- **The permit is held during backoff.** This is simple and keeps the bound obvious, at the cost of idle
  capacity for `RetryBackoffMs` per retry.
- **Not done:** jitter (a fixed backoff can synchronise retries across requests; jitter needs an injected random
  source to stay deterministic in tests), honouring an upstream `Retry-After`, and retry budgets (a cap on the
  ratio of retries to requests across the process).
- **Client-side retries remain possible.** The platform can't stop a client retrying a 504. The documented
  contract warns that a retry may produce a second billed completion. The production answer is a client-supplied
  idempotency key, stored per tenant with the completed result for a short TTL, so a retried request returns the
  original completion. Vendor support for idempotency keys varies, so the platform has to own this. Not
  implemented.

### 4.1 Quota charging for failed calls

A request's rate token is spent once the provider has been called, **including** when every attempt failed
as "not processed". This was challenged in review: the provider did no billable work, so why charge the
tenant? It is deliberate.

- The rate quota limits **provider calls made on a tenant's behalf**, not billed work. A throttled attempt still
  reaches the provider's admission and counts against the platform account's upstream rate limit, which every
  tenant shares.
- Refunding would exempt exactly the traffic that hurts most during an upstream outage. A client retrying in a
  tight loop would never be rate-limited, and with platform retries each request causes up to `MaxAttempts`
  upstream calls. Charging turns that retry storm into 429s after the tenant's burst.
- A well-behaved client loses nothing. One that honours `Retry-After: 1` sends at most 60 requests a minute, the
  default refill rate, so its bucket never drains on 503s.
- Rejections *before* the provider is called (global capacity, queue timeout, client abort while queued,
  abandoned-call limit) are refunded, because no provider capacity was used.

If billed usage is the quantity to limit, that is the TPM quota (§5.4), which would reconcile against actual usage
and could credit not-processed attempts. `T07_NotProcessedFailure_StillChargesTheRequestToken` pins this
decision.

### 4.2 Alternative: no retries

The alternative FR-10 allows, "disable retries entirely", is simpler and safe. It was rejected because it gives
up recoveries that are free and safe (throttling before processing is the most common transient failure in
practice) and demonstrates less. `MaxAttempts = 1` gives exactly that policy through configuration.

### 4.3 Circuit breaker (design discussion only; not implemented)

A circuit breaker would stop sending traffic to a provider route that is failing, instead of letting every
request wait for its own timeout.

- **Scope:** per provider route (provider + model, possibly + region), not per tenant. A breaker trips on
  provider health, which tenants share.
- **Trip signal:** the rate of timeouts, ambiguous transient failures and abandoned calls over a sliding window
  with a minimum sample size. Permanent failures (4xx-like: bad input, content policy) and quota rejections must
  **not** count, or one tenant's bad requests could open the circuit for everyone. That would be a cross-tenant
  denial of service.
- **Open state:** fail fast with 503 `provider_unavailable`, `Retry-After` = remaining open interval. This frees
  permits for healthy routes immediately.
- **Half-open:** admit a small number of probe requests. Real tenant traffic used as probes is billable and
  ambiguous, so a synthetic health probe (or vendor status endpoint) is preferable where available.
- **Interaction with M2:** the breaker sits between the gate and the invoker. Retries (only "not processed")
  happen inside the breaker decision. M2's `MaxAbandonedCallsPerRoute` is already a crude, count-based
  breaker for one signal (abandoned calls), at the recommended per-route scope: it opens while orphans are running
  and closes when they end, but it has no time window, no error-rate input and no probe.
- **Fallback routing** (another provider or model for the same alias) is a product decision. Different models
  give different answers and costs, so it must be opt-in per alias.
- **Distributed state:** each replica keeping its own breaker is usually acceptable (each observes the same
  provider) and avoids a shared dependency in the failure path.
- **Why not now:** with one deterministic mock provider there is no realistic signal to tune against, and an
  untested breaker is worse than none. Error mapping, deadline and abandonment counter were implemented first,
  because a breaker depends on them.

## 5. Local versus distributed enforcement

Everything in M2 is **in-process**: quota buckets, concurrency counters, the global gate and the abandoned-call
counter. That is correct for one instance and wrong in specific, predictable ways for many.

### 5.1 What breaks with N replicas

| Limit | Effect with N replicas behind a load balancer |
| --- | --- |
| Tenant request rate | Up to **N ×** `RequestsPerMinute` sustained and **N ×** `BurstRequests` burst, depending on how requests spread |
| Tenant concurrency | Up to **N ×** `MaxConcurrentRequests` |
| Global provider bound | **N ×** `MaxConcurrentProviderCalls` simultaneous calls to the provider, which has its own account-wide limits |
| Uneven balancing | A tenant pinned to one replica is limited at 1×, while one spread evenly gets N×. The effective quota depends on routing. |
| Restart or scale-out | New replicas start with full buckets, so each deploy grants every tenant a fresh burst |
| Upstream limits | Vendor RPM/TPM limits apply to the whole platform account. No replica can see total usage, so local limits can't protect the shared upstream quota. |

### 5.2 Race conditions

- **In-process (handled):** check-and-take is atomic under a per-tenant lock, refunds go through the same lock,
  and lease release is idempotent (`Interlocked.Exchange`). Two concurrent tests, each making 200 racing
  admissions (one bounded by concurrency, one by the token bucket), confirm that neither limit is exceeded.
- **Distributed (not handled; must be designed in):**
  - *Read-modify-write races.* Two replicas both read "1 token left" and both admit. The update must be atomic in
    the store (e.g. a Redis Lua script or GCRA, which keeps a single timestamp per key), not a GET followed by a SET.
  - *Clock skew.* Replicas computing refill from their own clocks disagree. Use the store's clock, or GCRA with
    the store's time.
  - *Leaked distributed slots.* A replica that crashes holding concurrency slots never releases them. This is
    the distributed version of NFR-05. Slots need leases with a TTL (renewed while the call is in flight), so a
    dead replica's slots expire.
  - *Refund races.* A refund after the window or bucket state changed must be capped (as M2 caps at burst) and
    idempotent per request ID.

### 5.3 Options at scale

| Approach | Accuracy | Cost and risk |
| --- | --- | --- |
| Divide limits by replica count | Approximate; wrong during scale events and with uneven balancing | No new dependency |
| Consistent-hash routing by tenant (sticky) | Exact per tenant | Hot tenants overload one replica; failover resets state |
| Central store (Redis) per request | Exact (atomic script) | An extra network hop per request; the store becomes a critical dependency. Fail-open or fail-closed must be decided (fail-open with local fallback limits is typical for availability) |
| Hybrid: replicas lease token batches from the central store | Bounded over-admission (≤ batch × replicas) | Fewer round trips; more complex |
| API gateway or service mesh rate limiting | Exact for request rate | Doesn't understand in-flight concurrency or LLM token usage; rate only |

The M2 abstraction boundary (`TenantQuotaService.TryAdmit` → lease with refund-on-dispose) maps directly
onto a distributed implementation: admission becomes an atomic script, and the lease becomes a TTL-bound slot
with idempotent release. The callers would not change.

### 5.4 Token-based (TPM) quotas: deferred

LLM cost scales with tokens, not requests. A TPM quota has to **reserve** an estimate before the call
(input tokens plus `maxOutputTokens`) and **reconcile** afterwards with actual usage. Timeouts and abandoned
calls complicate reconciliation, because actual usage may never be reported. This would follow the same
reserve/refund pattern as the request token, but it needs a usage-reporting design (tenant administrators
are meant to see usage) and real token counts. The mock's are estimates. Deferred.

## 6. Configuration validation

- `AiCoreOptions` is bound with `ErrorOnUnknownConfiguration = true` and validated by `AiCoreOptionsValidator`
  (`IValidateOptions`) with `ValidateOnStart`. `Program.cs` also resolves the options eagerly after `Build()`,
  so the process stops before any stateful singleton (gate, quota service) is built from invalid values.
- Each failure names the key, the rule, the configured value and what the setting means, and all failures are
  returned together. API key values are never echoed; duplicates are reported by index.
- The mock provider's settings moved under `AiCoreOptions.MockProvider`, so the whole `AiCore` section is
  validated by one validator and strict binding covers it.
- The Kestrel body limit is read from the validated options rather than raw configuration, so an invalid value
  is reported with all the others instead of as a Kestrel exception.
- **Limitations:** options are read once at startup (no reload). The validator doesn't check that a route's
  `Provider` is registered; that still surfaces as 500 `internal_error` at request time, as in M1. Strict
  binding means an operator can't leave stray keys under `AiCore`, which is deliberate.

## 7. Known M2 limitations

- All enforcement is per process (§5). Quota state is lost on restart.
- No TPM quotas, no per-tenant usage reporting, no reserved capacity, no weighted fair queuing.
- One global `ProviderTimeoutMs` for every alias. Real deployments need per-route deadlines (long completions
  versus short classifications), and streaming would need time-to-first-token and inter-token idle timeouts
  instead of one total deadline.
- No circuit breaker, jitter, retry budget, upstream `Retry-After` handling or idempotency keys (§4).
- Abandoned calls are capped per tenant and route, and per route, but there is no time-based recovery: a model whose
  calls never return stays refused until the process restarts (§3.3). An adapter that blocks a thread before returning
  its task occupies a pool thread for as long as it blocks.
- For M3 metrics, count gate rejections with `ProviderConcurrencyGate.QueueFullRejections` and `QueueWaitTimeouts`.
  The limiter's `TotalFailedLeases` also counts fast-path misses (requests that then queued and succeeded).
- `Retry-After: 1` for `capacity_exhausted` (and for `concurrency_quota_exceeded` when tokens remain) is a fixed
  hint, not derived from state, so clients that honour it retry in step.
- `sim-timeout` and `sim-unresponsive` exist only in the Development environment, because they deliberately hold
  capacity. The fast-failing `sim-*` aliases are available everywhere.
- Quota rejections, retries and timeouts are visible in logs only where noted. Metrics and pseudonymous
  tenant identity in telemetry are M3 (FR-09, NFR-08).
- The cancellation grace period also applies after a client abort, which is intended (it keeps the bound
  exact), so a disconnected client's permit can stay held for up to `CancellationGraceMs`.
