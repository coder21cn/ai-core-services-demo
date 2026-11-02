# ADR 004 — Retry only work guaranteed not processed

**Status:** Accepted; no general idempotency guarantee.

**Problem.** A timed-out inference may have completed and incurred charges. A successful retry can generate
a second, different completion and double billing.

**Decision.** Retry only `ProviderTransientException.RequestNotProcessed == true`, within `MaxAttempts`
and one deadline covering attempts/backoff. Do not retry ambiguous failure, timeout, permanent failure
or unexpected exception. Honor results arriving within cancellation grace instead of discarding a
potentially billable success. Mock tests make these outcomes deterministic.

**Alternatives.** Retrying every 5xx improves some availability but duplicates ambiguous work. Disabling
all retries is safe but loses recovery from admission throttling. Caller idempotency keys help only with
durable coordination and an upstream idempotency guarantee or reliable result reconciliation.

**Drawbacks.** Correctness depends on the adapter's classification. Fixed backoff can synchronize callers;
there is no jitter or provider Retry-After integration. One request token covers multiple attempts, so it
is not a strict upstream-attempt rate limit. The current transient 503 contract does not distinguish
ambiguous completion to clients (see review R-01); platform retry restraint alone is insufficient.

**Change trigger.** A real provider requires verified retry classification, contract tests, budget-aware
jitter/backoff, and explicit client retry guidance. A circuit breaker should be partitioned by failure
domain with bounded half-open probes. It must exclude tenant/validation failures and should not treat
ordinary tenant throttling as evidence of a global provider outage. No circuit breaker is implemented.

**Evidence.** `RetryPolicyTests`, `ProviderInvokerTests` and `TimeoutAndCancellationTests` cover bounded
attempts, ambiguous failures, deadline/grace outcomes and cancellation during backoff.
