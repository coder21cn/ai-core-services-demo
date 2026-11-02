# M3 technical assessment

**Date:** 1 November 2026. **Scope:** current M1/M2 code plus M3 instrumentation, security boundaries,
design decisions and scaling claims. **Method:** source inspection, adversarial regression tests, a separate
review pass after implementation, and the full automated suite. Commands and failures are recorded in
[verification.md](verification.md#m3--observability-and-architecture-review).

**Review provenance:** this implementation and assessment were produced by the same coding agent.
The separate review pass is not independent reviewer sign-off. A second human/agent reviewer remains an
explicit M3 gate; no independent review has been fabricated.

## Assessment

The service is a credible offline demonstration of tenant-aware admission, bounded cooperative execution,
careful retry semantics and privacy-conscious instrumentation. Its tests substantiate those local
properties. It is not ready for production tenant traffic: identity/authorization, distributed admission,
real provider behavior, telemetry export operations and infrastructure security are not implemented.

The main architectural tension is resolved explicitly: returning permits after grace preserves service
liveness but no longer strictly bounds all physical upstream work. Route and tenant-on-route accounting
limit the damage; neither cancellation tokens nor `Task.Run` can forcibly terminate an in-process adapter.
The second major boundary is semantic: request quotas and successful simulated usage are not cost controls.

## Reproduced defects and fixes

These findings were demonstrated by failing tests before the fix, rather than inferred from code alone.

| ID | Finding and evidence | Resolution / remaining limit |
| --- | --- | --- |
| D-01 | A sensitive marker used as an unknown JSON property name appeared in 400 Problem Details through `JsonException.Path`. `T08_UnknownJsonProperty_IsNotEchoedInError` failed before the fix. | Removed JSON paths and exception text from parsing errors. Status/code remain unchanged; detail is generic. Fixed. |
| D-02 | A deliberately unsafe adapter's typed permanent/transient exception message reached execution logs, including retry logs. All three `T08_ProviderExceptionMessages_NeverReachLogsOrErrors` cases failed. This reproduced reliance on adapter discipline, not an exploit in the shipped mock. | Removed those messages from logs. Failure class, attempt count, provider and correlation remain. Adapter-owned logging is still trusted code and outside this protection. Fixed at the platform log boundary. |
| D-03 | An exception during service resolution outside `ProviderInvoker` reached the framework logger with its full sensitive message. `T08_UnhandledServiceException_IsNotLoggedInFull` failed. | Suppress full framework exception diagnostics and emit safe `internal_error` completion telemetry. Fixed for handled exceptions in the request pipeline; startup/process crashes and future SDK instrumentation require separate review. |
| D-04 | On this restricted Windows host, default Event Log writes failed and caused 78 baseline test failures. After changing logging, the test host also attempted to read/write the user's Data Protection key ring, producing unrelated error logs. | JSON console logging removes Event Log dependence. Test hosts use an isolated in-memory XML key repository, including key-ring warmup. User key files were not changed. Production Data Protection defaults are unchanged. Fixed for the documented test workflow. |
| D-05 | The 2 November audit reproduced a validated `MaxOutputTokens=536870912` request producing `500 internal_error`. Follow-up boundary tests also reproduced wrapped/truncated output, incorrect token estimates near Int32.MaxValue and a false shared-capacity warning. | Widen multiplication, token-rounding addition and shared-capacity addition before arithmetic. `NumericBoundaryTests` covers explicit/default budgets through the API and arithmetic boundaries without allocating huge payloads. |

The initial five privacy regression cases all failed, and subsequently passed. The full passing suite is
recorded in verification.md. This is evidence about these exercised paths, not a proof that every possible
third-party logger/exporter is safe.

### PR #7 follow-up fixes

The requested iterative review found and fixed additional M3 bugs; the exact passes and evidence are in
[pr-7-review.md](pr-7-review.md). Throwing metric/activity listeners could leak a provider permit or change
request outcomes; completion logger failures could abort successful responses. Escaped pipeline failures
could be labelled successful, and handled exceptions lost the endpoint label. A follow-up test also caught
disconnected response writes incorrectly marking cancellation as a server-error span. These are now covered
by adversarial listener, logger, exception and cancellation tests. This remains a same-agent review.

## Open design risks

Severity describes priority **before enterprise deployment**, not an assertion that a production incident
was reproduced. These are not additional demonstrated defects from this session.

| ID / priority | Risk and code basis | Recommended next step / completion criterion |
| --- | --- | --- |
| R-01 / High | `GenerationService` maps both known-not-processed and ambiguous transient failures to `ApiProblem.ProviderUnavailable()`, which says retry later and sets Retry-After. The platform itself correctly avoids ambiguous retries, but clients cannot infer that distinction from the stable code/header. No double billing was observed because no real provider exists. | Version/document a client-visible outcome-ambiguity signal and safe retry guidance. Test ambiguous, definitely-not-processed and locally refused cases separately before using billable inference. |
| R-02 / High | API keys are development credentials; all authenticated tenants can invoke every configured alias. No token validation, model entitlements or revocation workflow exists. | Production identity integration and authorization tests for allowed/denied tenant-model pairs, plus TLS and key rotation plans. |
| R-03 / High | `TenantQuotaService` and the gate are local singletons. Replica count/restarts multiply or reset allowance. Request quotas do not cap tokens or cost. | Specify global admission and store-failure policy; test concurrent multi-replica enforcement, crash recovery and token reconciliation with a real adapter. |
| R-04 / High | Abandoned tasks continue in-process, retaining memory and potentially threads/upstream work. Bounds are per route and allowances accumulate across routes/replicas. | Measure faulty-adapter resource behavior, then isolate workers if strict process resource or upstream execution bounds are required. Track orphan age and route-specific health before production. |
| R-05 / Medium | One global FIFO queue permits a slow route or a group of tenants to consume healthy-route capacity. Per-tenant limits do not guarantee fair shares. | Run mixed-workload tests; add provider bulkheads or weighted admission if required service classes cannot be met. |
| R-06 / High | Adapters control `ProviderResult`, including output length and usage counts. The mock bounds output, but `GenerationService` does not validate arbitrary adapter results. The endpoint always labels usage simulated. | Add adapter contract tests for output byte/token bounds, valid usage, real-versus-estimated metadata and streaming limits before registering a real provider. |
| R-07 / Medium | Route identifiers concatenate provider and model with `/`, and route accounting is case-insensitive. Configuration validation does not prove these semantics match every future provider, or validate registered provider/model existence at startup. | Use a structural route key and define provider-specific model comparison. Validate registration/route references and test colliding or case-distinct identifiers when extending adapters. |
| R-08 / Medium | The mock output-budget, token-estimate and shared-capacity overflows were reproduced and fixed as D-05. Configuration still has no deployment-specific operational ceilings; arithmetic safety does not make huge capacity settings operationally sensible. | Establish workload-appropriate policy limits before production deployment; retain boundary regression tests. |
| R-09 / Medium | Application logs are allowlisted, but future HTTP logging, trace baggage import, SDK instrumentation or exporters can bypass that allowlist. Invalid configuration paths can include raw tenant IDs. | Review each sink/SDK, use secret-free configuration identifiers, enforce an export allowlist and test structured payloads after every instrumentation change. |
| R-10 / Medium | Request completion emits a log for every request. Throwing application listeners and completion sinks are now contained and counted, but blocking callbacks, export backpressure, sampling, retention and access control remain deployment concerns. | Benchmark overhead under load; require nonblocking bounded export and verify collector outages. Exception containment does not sandbox third-party code. |
| R-11 / Medium | Provider/queue deadlines do not cover slow uploads, response transfer or thread-pool starvation. In-memory tests do not enforce Kestrel's body-size setting. | Add real-server transport tests and ingress limits; measure end-to-end deadlines with real payloads rather than interpreting mock tests as an SLA. |

## Improvements and deliberate deferrals

| Improvement | Value and boundary |
| --- | --- |
| OpenTelemetry collector/export integration | Makes traces/metrics operationally accessible; local BCL instrumentation and listener tests are implemented now, export is not |
| Per-route and queue-reason metrics | Current request codes separate platform/provider failures; queue-full versus wait-expired and orphan age would speed incident diagnosis |
| Readiness and graceful drain | Define when an instance should receive new traffic without tying liveness to an upstream outage; no `/health/ready` is implemented |
| Circuit breaker and jitter | Useful with verified real-provider semantics and measurements; avoid adding arbitrary thresholds to a mock |
| Durable audit and usage reconciliation | Separate from sampled telemetry and mock estimates; necessary for financial accountability or regulated operations |
| CI and transport/integration coverage | Existing in-process tests are valuable; a reviewed CI run and real Kestrel/adapter tests improve confidence across environments |

## Requirement evidence and handoff

| Acceptance area | Current evidence / limitation |
| --- | --- |
| FR-09, NFR-08 and T-08 | Structured completion logs, activities, measurements and privacy regressions; no external exporter |
| NFR-03 / NFR-07 | HTTP, authentication, admission, execution and instrumentation boundaries; architecture overview and seven ADRs |
| FR-12 | Injected second test provider and documented adapter/streaming/capability changes; no live provider |
| FR-04/05/06/10 | Existing tenant/concurrency/timeout/retry suites continue to pass; local process scope only |
| NFR-06 | Existing controlled 100-request test demonstrates cooperative concurrency bound, not throughput |
| Independent architecture review | Pending a second reviewer; this assessment is the implementation agent's review |

Recommended order: (1) independent review of this diff and the failure evidence; (2) clarify client retry
ambiguity and harden adapter/configuration boundaries; (3) add a sandboxed real adapter and transport tests;
(4) production identity/authorization and telemetry operations; (5) distributed admission and load/SLO
work when deployment requirements justify them. Keep M4 interview preparation a separate deliverable.
