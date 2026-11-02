# Architecture decision records

Accepted for the reference implementation, 1 November 2026. Production alternatives are design proposals,
not implemented features. M1/M2 design notes preserve the historical reasoning; these records summarize
the current decisions and their change triggers.

1. [Tenant identity and authorization boundary](001-tenant-identity.md)
2. [Local quotas and distributed admission](002-quotas.md)
3. [Bounded waiting, fairness and abandoned work](003-backpressure.md)
4. [Retry safety and inference idempotency](004-retry-safety.md)
5. [Provider abstraction and capability differences](005-provider-abstraction.md)
6. [Telemetry privacy and cardinality](006-observability.md)
7. [Platform and agent ownership](007-platform-ownership.md)
