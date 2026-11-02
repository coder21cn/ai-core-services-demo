# AI Core Services — Reference Platform Requirements

**Status:** M1/M2 delivered; M3 implementation delivered, independent review pending

**Prepared:** 31 October 2026; public specification updated 2 November 2026

**Scope:** A locally demonstrable reference service and documented architecture trade-offs

> **Purpose.** Create a compact, technically credible .NET reference implementation of the shared infrastructure that independent product and agentic-AI teams could consume. This is an educational/portfolio system informed by general engineering experience. It is **not** AVEVA software, an assertion about AVEVA's internal architecture, or a production-ready security system.

## 1. Outcomes and limits

### Primary outcomes

1. A functioning API with a deterministic model-provider simulator and executable automated tests.
2. A clear design demonstrating tenant isolation, bounded resource use, graceful failure, provider abstraction, and observability.
3. Concise architecture decisions explaining what is implemented in a single instance and what must change for an enterprise deployment.
4. A technical assessment and reproducible demonstration grounded in **actual implemented behavior**. Personal interview preparation is tracked separately.

### Time and resource constraints

- Prioritize a **working, tested vertical slice** over feature breadth.
- Implement milestones sequentially and make each one independently reviewable.
- The project must build and run without accounts, paid LLM APIs, databases, cloud deployment, or secrets.
- Use an available supported .NET LTS SDK (prefer .NET 10; document and justify the chosen target framework).
- Minimize dependencies and setup steps. Prefer built-in ASP.NET Core facilities where appropriate.
- Keep code and documentation under source control. Each change should produce a reviewable branch/PR or commit and a short verification report.
- Never use credentials, repository history, code, or confidential information belonging to a former employer.

## 2. Users and architectural boundary

| Actor | Needs | Boundary |
| --- | --- | --- |
| Product application developer | Stable model invocation API, error semantics, instrumentation | Consumes core API or SDK; does not manage provider credentials |
| Agentic-AI team | Calls model endpoints and potentially governed tools | Owns orchestration, planning and agent memory; platform owns common controls |
| Platform operator | Configuration, diagnostics, quotas, service health | Owns shared infrastructure and service policy |
| Tenant administrator | Isolation, quota and usage reporting | Cannot view other tenants' requests or results |

**In scope:** A single-process reference implementation of model invocation and operational controls, together with enterprise-scale design notes.

**Out of scope:** An autonomous agent, a vector database/RAG system, live brokerage connections, real production user management, actual model billing, persistent prompts, fine-tuning, a frontend dashboard, live deployment, and an external MCP tool connector. These may be discussed as extension points but must not be represented as implemented.

## 3. Functional requirements

The IDs below are stable acceptance references. If a requirement is intentionally deferred, document why and do not claim it is complete.

| ID | Requirement | Observable acceptance criteria | Priority |
| --- | --- | --- | --- |
| FR-01 | Provide `POST /v1/generate` accepting a logical model alias and a bounded prompt or message input | Valid request returns correlation ID, response text, model/provider identifier, latency, and simulated usage; invalid requests return appropriate 4xx with structured error | Must |
| FR-02 | Define an `IModelProvider` abstraction and deterministic `MockModelProvider` | Mock supports configurable latency, success, transient failure, permanent failure and cancellation; no network or credentials required | Must |
| FR-03 | Identify a tenant from authenticated server-side identity | Tenant ID is derived from validated credentials, **never trusted from client body/header**; missing/invalid credentials get 401/403 | Must |
| FR-04 | Isolate per-tenant limits | Under concurrent tests, one tenant exhausting its quota does not deny another tenant's available capacity; excess calls get 429 with coherent semantics | Must |
| FR-05 | Bound simultaneous provider calls | A configurable concurrency limit is enforced with bounded waiting or immediate rejection; no unbounded work queue. See the reference-scope qualification below for adapters that ignore cancellation. | Must |
| FR-06 | Propagate cancellation and enforce a provider-call timeout | Client cancellation stops waiting/work where possible; a controlled timeout produces a documented failure; no hanging tests | Must |
| FR-07 | Return stable, safe error responses | Invalid input, auth failures, rate-limit rejection, provider unavailability, timeout and internal error are distinguishable; no secrets or prompts appear in exceptions sent to callers | Must |
| FR-08 | Expose basic health endpoints | Liveness is independent of external model services; readiness semantics and limitations are documented | Should |
| FR-09 | Provide structured logging, tracing and measurements | Capture request/correlation ID, pseudonymous tenant identity, logical model alias, result type, latency and simulated usage; **do not log prompt or API keys** | Should |
| FR-10 | Demonstrate resilience choices | At least one deterministic test illustrates bounded retry **only for an explicitly retryable failure**, or an explicit documented policy disabling retries because duplicate billable inference cannot always be prevented; include circuit-breaker design discussion | Should |
| FR-11 | Provide configuration validated at startup | Invalid negative/zero concurrency, timeout, request-size or quota settings fail with actionable errors | Should |
| FR-12 | Document provider swapping | A second adapter can be introduced without changing the public endpoint; document streaming, tool-calling and provider-difference implications without implementing all of them | Should |

### API contract guidance

**FR-05 / NFR-05 reference-scope qualification:** `MaxConcurrentProviderCalls` bounds provider-phase permits
and physical calls for cooperative adapters. After cancellation grace expires, a non-cooperative call is
abandoned and its permit released to preserve availability; it remains counted against separate tenant-on-route
and route abandonment limits until it ends. Actual work can therefore exceed the permit limit. This is the
existing M2 trade-off documented in [ADR 003](decisions/003-backpressure.md), not a newly implemented hard
execution cap. Recovering capacity while keeping a strict physical cap for uncooperative adapters requires
killable isolated workers and remains out of scope; retaining their permits instead would sacrifice availability.

- Prefer simple JSON and a consistent Problem Details-style error body.
- The sample API may use development-only API-key authentication, with keys supplied by environment/configuration and never committed. Document how production OIDC/OAuth2 or workload identity would replace this.
- Set a finite maximum input size, configurable timeout and bounded response size.
- Include a `GET /health/live` endpoint. Add `/health/ready` only if its meaning is useful and tested.
- Preserve stable logical model aliases; do not hardcode the public contract to one vendor's SDK types.
- Document which telemetry fields may contain personal or sensitive information and ensure prompt content is not logged by default.

## 4. Non-functional requirements

| ID | Area | Requirement / verification |
| --- | --- | --- |
| NFR-01 | Repeatability | A fresh clone builds and runs with documented commands; all tests run offline against mock dependencies. |
| NFR-02 | Determinism | Concurrency, retries and timeout tests use controllable clocks, signals, gates or injected delays where practical; avoid flaky timing guesses. |
| NFR-03 | Separation of concerns | HTTP contract, tenant authentication, request policy, provider execution and telemetry have discernible boundaries; avoid unnecessary layers. |
| NFR-04 | Security | No real secrets in Git; no arbitrary shell/tool execution; no cross-tenant state exposure; no prompt/credential logging; no assumption that a demo API-key scheme is enterprise production authentication. |
| NFR-05 | Failure safety | A failing provider cannot leave permits held indefinitely; all cancellation/disposable resources are released; exceptions are mapped safely. |
| NFR-06 | Performance | Under a documented synthetic concurrent test (e.g., 50–100 requests), demonstrate the configured in-flight bound. Do **not** claim production throughput or latency based on a mock. |
| NFR-07 | Maintainability | README explains run/test commands; comments explain non-obvious invariants; architecture decisions show alternatives and trade-offs. |
| NFR-08 | Observability | Operator can distinguish client rejection, provider failure, cancellation and timeout without inspecting prompt text. |
| NFR-09 | Deployment | Development must work locally. A minimal Dockerfile is optional; Kubernetes, Helm and cloud deployment are not prerequisites. |

## 5. Required tests

Tests must check behavior rather than merely checking that components were instantiated.

| ID | Test scenario | Expected outcome |
| --- | --- | --- |
| T-01 | Successful request using mock provider | Stable response contract and correlation ID |
| T-02 | Invalid credentials and missing credentials | Denied; no provider invocation |
| T-03 | Tenant A reaches its quota; tenant B has capacity | A rejected with 429; B can proceed |
| T-04 | More concurrent requests than configured capacity | Observed in-flight calls never exceed bound; waiting/rejection policy verified |
| T-05 | Provider call cancels or times out | Proper error and no leaked capacity |
| T-06 | Provider fails permanently | Safe error; no unlimited retries |
| T-07 | Configured retryable transient error | Bounded number of attempts if retries implemented |
| T-08 | Prompt contains sensitive marker | Marker does not appear in structured telemetry or response errors |
| T-09 | Invalid configuration | Startup/configuration validation rejects invalid values |
| T-10 | Controller-to-provider request through test host | End-to-end response and error mapping verified without external services |

## 6. Design decisions to document

Each decision record should state the problem, chosen approach, alternatives, drawbacks and when the choice changes at scale.

1. **Tenant identity and trust boundary:** demo API keys versus production OIDC/workload identity.
2. **Quotas:** in-process counters versus distributed enforcement (e.g., Redis); identify race conditions and cross-replica limitations.
3. **Backpressure:** semaphore/queue bounds, overload responses, fairness and starvation.
4. **Retry and idempotency:** ambiguous inference completion and potential double billing; why retry policy must be restricted.
5. **Provider abstraction:** common interface versus preserving provider-specific capabilities.
6. **Observability and privacy:** trace context, cardinality, redaction, sampled traces and cost estimation.
7. **Platform ownership:** separation between shared AI infrastructure and agent orchestration/tool governance.

## 7. Target repository shape (adapt as needed)

```text
/
  README.md
  docs/
    REQUIREMENTS.md       # this file
    architecture.md
    decisions/
    demo.md
    provenance.md
    verification.md
  src/
    AiCore.Api/
    AiCore.Core/           # optional: only if separation earns its cost
  tests/
    AiCore.Tests/
```

The exact project split is a design choice, not a requirement. Do not create layers that serve no purpose.

## 8. Delivery milestones and gates

### M1 — Running vertical slice (do first)

- [x] Create solution, API, mock provider, request/response/error contract and README.
- [x] Add basic tenant authentication, bounded concurrency and tests T-01, T-02, T-04, T-10.
- [x] Build succeeds, tests pass, and a `curl` example can exercise the API.
- [x] Commit to a branch and record actual build/test output in `docs/verification.md`.

**Gate:** Do not expand scope if M1 has failed tests or is not retrievable from GitHub.

### M2 — Reliability and tenant controls

- [x] Add quota isolation, cancellation, timeouts and validated configuration.
- [x] Cover T-03, T-05, T-06, T-09 and the chosen retry behavior (T-07).
- [x] Document local versus distributed enforcement limitations.
- [x] Commit changes and include test output.

M1/M2 boxes were reconciled on 2 November 2026 against the implementation, test suite, merged history and
recorded real-Kestrel/curl evidence in [verification.md](verification.md). They do not assert production
readiness or remove the FR-05/NFR-05 qualification above.

### M3 — Observability and design review

- [x] Add structured logging/tracing/measurements with redaction test T-08.
- [x] Write architecture overview and at least four substantive decision records.
- [ ] Independently review architecture; classify **demonstrated defects** separately from **design risks**.
- [x] Record unresolved issues and recommended next steps.

M3 implementation and the implementation agent's technical assessment are in `architecture.md`,
`observability.md`, `decisions/` and `architecture-review.md`. A separate review pass classified reproduced
defects, design risks and improvements; independent second-reviewer sign-off remains pending.

### M4 — Personal interview preparation (separate deliverable)

Personal interview preparation is maintained outside this repository. The planned question/answer pack
and personal rehearsal remain pending; the public [demo guide](demo.md) does not complete M4.

## 9. Review checklist before merging any PR

- [ ] Diff contains only intended changes; no secret files, tokens or generated binaries.
- [ ] Build/test output is present and plausible; failed or skipped tests are disclosed.
- [ ] API responses and tenant isolation behave as documented.
- [ ] No request or provider failure can leave concurrency capacity permanently occupied.
- [ ] Prompts and credentials do not appear in telemetry.
- [ ] The branch/PR is available on GitHub; the change is retrievable by its intended reviewers.
- [ ] The next milestone begins from the approved revision.

## 10. Definition of done and success metric

The project is complete enough for its immediate purpose when M1/M2 pass executable tests, M3's major design trade-offs are documented and independently reviewed, and the local demonstration is reproducible. M4 remains a separate personal preparation deliverable. A realistic minimal implementation with clearly described limitations **passes**; a large untested system **fails**.

Future separately scoped work may add real providers, distributed quotas, richer evaluation, MCP integration and platform governance.
