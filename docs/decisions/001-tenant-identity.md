# ADR 001 — Server-derived tenant identity

**Status:** Accepted for the demo; production identity remains deferred.

**Problem.** Tenant quotas are meaningless if the caller can choose the quota bucket independently of
authentication. The reference must run without an identity provider or committed secrets.

**Decision.** Map environment/configuration-supplied development API keys to tenant IDs through SHA-256
digest lookup. The authentication handler creates `tenant_id`; downstream policy reads that claim only.
Body tenant fields are rejected. Invalid credentials never invoke the provider. No model-specific
authorization is implemented.

**Alternatives.** Caller-supplied tenant headers are easy but forgeable. OIDC/OAuth2 access tokens and
workload identity provide a stronger deployment model but require issuer configuration and lifecycle
management beyond this offline slice. An API gateway can authenticate callers only if forwarded identity
is protected and the backend cannot be reached by an untrusted bypass path.

**Drawbacks.** Demo keys have no expiry, revocation endpoint, rotation workflow or scopes. Plaintext values
remain in configuration memory; hashed lookup is not a production credential management system. There is
no claim that dictionary lookup is a constant-time authentication protocol.

**Change trigger.** Before real tenants or external exposure, validate issuer, audience, signature and
lifetime through a supported token handler; map verified tenant membership and enforce model entitlements.
Use workload identity for provider access where available. Preserve the downstream tenant contract.

**Evidence.** `AuthenticationTests`, `TenantKeyStoreTests`, and `TenantQuotaTests` exercise trust and quota
boundaries; startup validation checks missing/duplicate keys without reporting their values.
