# ADR 002 — Local quotas with explicit replica limits

**Status:** Accepted for one process.

**Problem.** A busy tenant must not consume unlimited platform capacity, and quota bookkeeping must remain
correct across queue rejection, cancellation and concurrent requests.

**Decision.** Use per-tenant token buckets and active-request limits, atomically updated under a per-tenant
lock. A lease holds a concurrency slot through queueing/execution and refunds its rate token only if the
provider phase never began. Retries share one token. Abandoned work is charged separately on its route.
429 identifies tenant usage; 503 identifies shared/provider availability.

**Alternatives.** A global rate limiter lacks tenant isolation. Separate non-atomic read/decrement counters
oversubscribe under races. A distributed limiter would support replicas but introduces network latency,
store availability, failover consistency and lease recovery decisions.

**Drawbacks.** Restarts reset credit. N replicas permit consumption of N local quotas. Request count does
not bound inference tokens or spend. No durable usage ledger exists. A low-volume tenant has no guaranteed
share of a global queue occupied by several other tenants.

**Change trigger.** Multi-replica enforcement or spend commitments require atomic shared admission, a
specified store-outage policy, and token reservations/reconciliation where appropriate. Distributed
concurrency needs expiring owned leases and crash recovery, not just a decrement counter. Keep local
overload protection even when central enforcement is added.

**Evidence.** `TenantQuotaServiceTests` and `TenantQuotaTests` cover atomic admission, refunds, overrides,
refill and cross-tenant behavior. They do not demonstrate distributed consistency.
