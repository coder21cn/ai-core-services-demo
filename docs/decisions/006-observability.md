# ADR 006 — Allowlisted telemetry with bounded metric dimensions

**Status:** Accepted for local instrumentation; export/retention deferred.

**Problem.** Operators must explain failures without reading prompt content. Tenant IDs can be identifying,
exception messages may contain credentials, and arbitrary labels make metric storage unbounded.

**Decision.** Use JSON `ILogger` completion records, BCL activities and meters. Allowlist metadata instead
of attempting to redact arbitrary strings. Remove provider exception messages and JSON paths, suppress
framework full-exception diagnostics, and use host-keyed HMAC tenant references in logs/traces only.
Metrics use configured aliases/providers, fixed endpoints/results and status; never tenant/request IDs.
Separate queue time, provider phase, active permits and abandoned work.

**Alternatives.** Logging request/response bodies makes diagnosis easy but exposes sensitive data. Regex
redaction is fragile with encoded or unexpected payloads. Plain hashing permits dictionary guessing of
predictable tenant IDs. A full OpenTelemetry collector/export stack adds deployment dependencies unnecessary
for proving instrumentation locally.

**Drawbacks.** Dropping stacks/messages reduces diagnostic detail. Host-keyed references cannot join tenants
across replicas/restarts. Configuration values and existing infrastructure/SDK diagnostics remain separate
trust boundaries. Completion logging is per request and has a cost; no log-volume limiting, export queue,
retention policy or production trace sampling has been configured.

Application telemetry callbacks and completion sinks can throw synchronously. Emission now contains those
exceptions, restores activity context, and increments a local observable failure count without recursively
logging. It does not isolate blocking or resource-exhausting callbacks; exporter code must remain trusted.

**Change trigger.** Before exported telemetry, define a versioned field schema, access/retention policy,
managed pseudonym keys if needed, bounded batch export and an explicit baggage/exception allowlist.
Sample traces based on measured volume, keeping counters unsampled. Head sampling may miss rare failures;
tail sampling requires buffering and its own privacy/capacity budget. Never use sampled traces or mock
token counters as a billing ledger.

**Evidence.** `TelemetryTests` and `PrivacyRegressionTests` validate structured fields, scopes, exception
payload exclusion, trace tags/events, metric dimensions, context linkage and failure distinctions.
`TelemetryResilienceTests` adds adversarial listeners/loggers, parent restoration and transport-failure cases.
