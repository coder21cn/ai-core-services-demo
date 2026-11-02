# ADR 007 — Separate invocation infrastructure from agent orchestration

**Status:** Accepted boundary; tool governance is not implemented.

**Problem.** Product and agent teams share identity, quotas, routing and operational needs, while planning,
memory and tool workflows vary. Combining everything creates a platform bottleneck and unclear ownership
of side effects.

**Decision.** The platform owns the invocation API, provider credentials/adapters, tenant admission, stable
errors and telemetry. Product/agent teams own prompts, orchestration, task state, memory and evaluation.
Any future tool execution belongs behind explicit tool authorization and audit controls; model output
alone is not authority to execute an action.

**Alternatives.** Each product can own a full provider integration, increasing duplicated security and
reliability work. A centralized agent runtime can standardize workflows but couples every product to its
planning and state model. An unrestricted tool proxy creates an unacceptable side-effect boundary.

**Drawbacks.** A narrow platform cannot solve product quality, prompt injection, tool safety or business
authorization. Cross-service debugging and versioned contracts need coordination. Quotas do not enforce
business permission or guarantee model correctness.

**Change trigger.** Add a shared orchestration component only after repeated product requirements justify
it. Govern tool capabilities by tenant/user/workload identity, enforce least privilege and budgets, and
audit approvals/results separately. Keep deterministic authorization outside model-generated instructions.

**Evidence.** The source contains invocation and mock-provider controls only: no tool executor, agent loop,
memory store or business workflow. This boundary is a deliberate scope decision, not a missing hidden layer.
