# ADR 005 — Small buffered provider contract

**Status:** Accepted for text generation.

**Problem.** Product callers need stable aliases and failure semantics without depending on one vendor's
SDK, but vendors differ in streaming, usage, tools, modalities and safety metadata.

**Decision.** Keep `IModelProvider.GenerateAsync` small: normalized request/result and typed failure
classification. Configuration maps aliases to provider names/models. Adapters translate vendor behavior;
the public endpoint and admission policy remain independent. A second test adapter already exercises
this registration pattern.

**Alternatives.** Exposing SDK objects couples callers and versioning to a vendor. A universal schema with
every vendor option becomes complex before requirements are known. Hiding arbitrary provider JSON in
the prompt prevents reliable validation and policy enforcement.

**Drawbacks.** The current contract supports buffered text only. Usage is always labelled simulated at the
HTTP layer; a real adapter cannot honestly be added without revisiting that metadata. Adapter result size,
usage validity, provider-specific model case semantics and route startup validation need additional work.
An in-process adapter remains trusted code.

**Change trigger.** Add explicit capability/versioned contracts for streaming, tool calls, multimodal input,
safety outcomes and real usage. Streaming requires partial-error semantics and cleanup after headers are
sent. Enforce response limits at the adapter boundary before exposing real provider data. Keep vendor
extensions explicit so unsupported behavior fails clearly.

**Evidence.** `GenerateSuccessTests`, `EndToEndErrorMappingTests`, mock tests and injected test providers
demonstrate swapping for the present contract only.
