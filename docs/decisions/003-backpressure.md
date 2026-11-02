# ADR 003 — Bounded waiting and liveness after adapter failure

**Status:** Accepted with an explicit defective-adapter trade-off.

**Problem.** Slow inference can create an unbounded queue. An adapter that ignores cancellation creates a
conflict between a strict execution bound and keeping healthy requests serviceable.

**Decision.** Use one process-wide `ConcurrencyLimiter` with FIFO ordering, finite queue length and finite
queue wait. Reject overload with 503 and a short retry hint. Hold permits while cooperative cancellation
unwinds; after grace, release them and track remaining work against tenant-on-route and route limits.
Check the route before admission and again after waiting.

**Alternatives.** Immediate rejection minimizes queue latency and is supported by a zero queue limit.
An unbounded semaphore wait retains requests indefinitely. Holding permits forever after ignored
cancellation eventually blocks all routes. Per-route bulkheads or worker isolation offer stronger failure
isolation but require capacity allocation and orchestration that this reference does not have.

**Drawbacks.** FIFO provides ordering among admitted waiters, not weighted fairness. Slow routes can fill
the common pool. With a defective adapter, physical calls can exceed the permit count; per-route abandoned
calls stay below threshold plus local concurrency, and allowances add across routes/replicas. `Task.Run`
does not isolate CPU, memory, threads or malicious code.

**Change trigger.** Use provider/route bulkheads when mixed workloads show head-of-line delay, weighted
scheduling when service classes need guarantees, and supervised worker processes when strict physical
limits or adapter isolation are required. Validate scheduler and cancellation behavior under sustained load.

**Evidence.** `ConcurrencyTests` includes the 100-request synthetic bound; `TimeoutAndCancellationTests`
exercises grace, abandonment, recovery and route/tenant accounting. No production throughput is claimed.
