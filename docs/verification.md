# Verification report

## Portfolio snapshot preparation — 2 November 2026

**Development base:** merged PR #8, `4569350`. Historical hashes and PR numbers below belong to the
private development repository; see [provenance](provenance.md). The snapshot's public CI is separate.

This pass adds a public demo and provenance guide, separates personal planning from the specification,
and changes the Problem Details documentation URL to the public demo repository. It does not change
admission, retry or provider behavior. The corresponding error-contract test checks the new URL.

```text
dotnet test AiCore.slnx --no-restore --disable-build-servers --configuration Release

Passed!  - Failed: 0, Passed: 216, Skipped: 0, Total: 216
```

The Release API and tests rebuilt successfully on Windows with .NET SDK 10.0.401. The PowerShell 7 and
Git Bash request blocks in [demo.md](demo.md) were extracted and executed against the built API on real
Kestrel using the documented Development settings and a temporary loopback port. Both shells produced
`200, 200, 401, 200, 503, 502, 504, 200` for health, success, invalid key, safe retry, ambiguous failure,
permanent failure, timeout and tenant B recovery. Stable error codes and public documentation URLs matched.
Completion/retry logs were present; the demo prompt marker and both disposable keys were absent.
The server was stopped afterward. No pre-existing process was stopped or restarted.

The full suite includes the guide's focused tenant, concurrency and privacy tests. Linux behavior is
covered separately by CI; the local Bash rehearsal used Git Bash on Windows. No real LLM, production
load test, external exporter or full mutation campaign was used. Independent M3 review remains pending.

## M1–M3 delivery audit fixes — 2 November 2026

**Base:** merged PR #7, `6bd057f`. **Environment:** Windows, .NET SDK 10.0.401.
These results verify the follow-up working tree, before commit. No live provider or cloud service was used.

The audit reproduced `500 internal_error` for an accepted `MaxOutputTokens=536870912` configuration.
Before fixing production code, the 12 new `NumericBoundaryTests` yielded **10 failed, 2 passed**:
output-budget multiplication could wrap or truncate, token-estimate rounding could overflow, and the
startup shared-capacity sum could produce a false warning. Widening arithmetic before each operation
preserves the accepted Int32 configuration range without allocating large test payloads.

```text
dotnet test AiCore.slnx --no-restore --disable-build-servers --configuration Release

Passed!  - Failed:     0, Passed:   216, Skipped:     0, Total:   216, Duration: 1 s - AiCore.Tests.dll (net10.0)
```

The complete suite rebuilt the API and tests successfully. Regression coverage includes explicit and
default output budgets through the HTTP pipeline, multiplication/rounding boundaries and startup logging.
M1/M2 checklist entries now reflect existing implementation and historical verification evidence.
The README, option documentation and requirements explicitly distinguish provider-phase permits from
physical calls abandoned after cancellation grace. No runtime concurrency policy was changed.

Final source review checked widening before arithmetic, the safe narrowing of a string-slice bound,
request/default validation, and retained lower-budget behavior. A second pass checked regression coverage
and milestone claims; neither pass identified another actionable defect in this change. Both passes were
performed by the implementing agent and **do not satisfy the independent M3 review gate**, which remains open.

`git diff --check` passed, local Markdown file links resolved, and all 33 mutation anchors still matched
exactly once. The full mutation campaign was not rerun for this arithmetic/documentation change.

## M3 — Observability and architecture review

**PR #7 follow-up:** implementation commit `77e8d0c` fixes telemetry callback failures, permit leakage,
completion classification, endpoint loss and disconnected-write span status. The subsequent full Release
suite passes **204/204** with no skipped tests; GitHub Ubuntu CI also passed. See
[the four-pass review record](pr-7-review.md) for pre-fix reproductions, fixes and the two consecutive passes
without new actionable implementation bugs. The 185-test result below records the original M3 delivery.

**Date:** 1 November 2026. **Environment:** Windows 10.0.26200 (win-x64), .NET SDK 10.0.401,
MSBuild 18.9.11, Python 3.13. **Base revision:** `7ba0640`; results below verify the M3 working-tree
changes, not a new committed/pushed revision. NuGet assets were already present; all .NET commands used
`--no-restore`. No live model, cloud service or telemetry collector was used.

### Build and complete suite

```text
dotnet build AiCore.slnx --no-restore --disable-build-servers

Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.52
```

```text
dotnet test AiCore.slnx --no-build --no-restore --disable-build-servers

Passed!  - Failed:     0, Passed:   185, Skipped:     0, Total:   185, Duration: 1 s - AiCore.Tests.dll (net10.0)
```

Both commands exited 0. The existing 170 tests still pass, with 15 new cases in `PrivacyRegressionTests`
and `TelemetryTests`. The tests inspect captured structured log fields/scopes and attached exceptions,
activity tags/events and metric dimensions. They verify trace/correlation linkage, canonical aliases,
tenant pseudonyms, simulated usage, retry counts, rejection classifications, timeout versus cancellation,
and abandoned work remaining visible after its permit is released, then returning to zero.

### Reproductions and intermediate failures

1. Initial baseline command:
   `dotnet test AiCore.slnx --no-restore --disable-build-servers`.
   Result: **78 failed, 92 passed, 170 total**, exit 1. The Windows Event Log provider threw
   `Cannot open log for source '.NET Runtime'` / access denied during startup logging. Configured portable
   JSON console logging as part of M3; no Event Log permissions were changed.
2. With portable logging and the five new privacy regressions, before privacy fixes:
   `dotnet test AiCore.slnx --no-restore --disable-build-servers --filter FullyQualifiedName~PrivacyRegressionTests`.
   Result: **5 failed, 0 passed**, exit 1. The sensitive marker appeared in the unknown JSON-property error,
   typed permanent/transient/retry logs, and the full framework log of an exception during service resolution.
   These are D-01 through D-03 in [the assessment](architecture-review.md).
3. First full M3 run: **3 failed, 181 passed, 184 total**, exit 1. One historical authentication test still
   expected `$.tenantId` in the error detail; it now checks the safe generic detail. Two cancellation tests
   detected unrelated Data Protection errors from accessing the developer profile's key ring. A trial
   ephemeral data-protection provider did not prevent the hosted key-ring warmup. The final fix isolates
   the test host with an in-memory XML key repository; production key-ring behavior was left unchanged.
4. After these corrections: **184 passed, 0 failed**, exit 0. Added one further observable-gauge test for
   abandonment/recovery; the final build and full **185-test** result are above.

The privacy changes retain HTTP statuses and stable error codes. Malformed JSON error details deliberately
omit caller-controlled paths; provider and framework exception messages deliberately disappear from logs.
No skipped test is hidden in the passing summary.

### Supporting checks and limits

```text
python -B -m unittest discover -s tools -v

Ran 9 tests in 0.034s
OK
```

The mutation runner's nine unit tests passed (exit 0). A read-only check verified that each of its 33
mutation anchors still occurs exactly once in the source; no mutation source edits were applied during
this M3 run. **The full mutation campaign was not rerun**; the M2 mutation evidence below is historical.
Local Markdown file links resolved, and `git diff --check` passed. No live Kestrel/load/production-provider
benchmark or exporter integration was performed for M3.

Direct .NET process IDs were recorded during execution: 45508 (baseline), 39500 (privacy reproduction),
37524 and 21976 (intermediate suites), 45636 (final build), 15860 (final suite), and 2664 (SDK information).
Python checks ran as 52464 and 39920. These foreground processes exited; no application server was left
running and no pre-existing user process was stopped or restarted. Shell PIDs were also recorded in the
execution transcript.

**Review gate:** architecture, seven decision records, security/scaling assessment and unresolved follow-ups
are written. The implementation agent performed the review; independent second-reviewer sign-off remains
pending and is not represented as completed. M4 interview material was not added.

## M1 — Running vertical slice

**Date:** 31 October 2026  
**Environment:** Claude Code cloud session, Ubuntu 24.04.5 LTS (x64), .NET SDK 10.0.114 (runtime 10.0.14).
The SDK came from the Ubuntu archive package `dotnet-sdk-10.0`, because the session's network policy blocks
the Microsoft `dotnet-install.sh` download hosts. `global.json` pins `10.0.100` with `latestFeature`
roll-forward, so any .NET 10 SDK at or above 10.0.100 works.  
**Commit verified:** `cb1616c` (M1 code). This report was added in the following commit.

Everything below was observed in this session. Nothing is extrapolated.

### 1. Build (clean `bin/`/`obj/`)

```bash
dotnet build AiCore.slnx
```

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.95
```

### 2. Tests

```bash
dotnet test AiCore.slnx --no-build --logger "console;verbosity=normal"
```

```
Total tests: 50
     Passed: 50
 Total time: 1.9513 Seconds
```

0 failed, 0 skipped. Exit code 0.

**Repeatability:** the suite was run 10 more times in a row (`dotnet test --no-build`). Result: 10/10 runs
were `Passed: 50, Failed: 0, Skipped: 0`.

**Fresh clone (NFR-01):** `git clone` of commit `cb1616c` into an empty directory, then `dotnet build`
→ `0 Warning(s) 0 Error(s)` and `dotnet test` → `Passed: 50, Failed: 0, Skipped: 0`. The NuGet packages
were already in the machine-level cache from earlier restores, so this shows that the repository contents
are complete. It doesn't show a restore from an empty cache on a new machine.

### 3. Required M1 tests → test cases

| ID | Scenario | Test(s) | Result |
| --- | --- | --- | --- |
| T-01 | Successful request, stable contract, correlation ID | `GenerateSuccessTests.T01_ValidRequest_ReturnsStableContractWithCorrelationId`, `T01_SameInput_IsDeterministic_WithDistinctCorrelationIds` | Pass |
| T-02 | Missing / invalid credentials denied, no provider call | `AuthenticationTests.T02_MissingOrInvalidCredentials_Return401_AndNeverInvokeProvider` (4 cases: missing, empty, wrong, wrong-case), `T02_Unauthenticated_IsRejectedBeforeBodyIsParsed` | Pass |
| T-04 | More concurrent requests than capacity; bound never exceeded; wait/reject policy | `ConcurrencyTests.T04_ExcessRequests_WaitInBoundedQueue_ThenAreRejected_AndBoundIsNeverExceeded` (4 permits, 2 queue, 16 requests: 6 × 200, 10 × 503), `T04_ZeroQueue_RejectsImmediatelyWhenFull` | Pass |
| T-10 | Endpoint → provider through test host, end-to-end response and error mapping | `EndToEndErrorMappingTests.*` (transient → 503, permanent → 502, unexpected exception → 500 with no leaked details, misrouted alias → 500, second adapter served by the same endpoint, 404/405 contract) | Pass |

Additional M1-scope tests: FR-03 tenant trust boundary (header spoofing ignored, `tenantId` body field
rejected), NFR-05 permit release after permanent/transient/unexpected failures, NFR-06 synthetic 100
concurrent requests (bound 8, observed max in flight = 8), request validation (11 invalid bodies + exact-limit
acceptance + 415), mock provider unit tests (determinism, output bound, `FakeTimeProvider` latency,
cancellation, failure types), and tenant key store tests.

<details>
<summary>Full list of the 50 executed test cases (all Passed)</summary>

```
AuthenticationTests.FR03_TenantFieldInBody_IsRejected_NotTrusted
AuthenticationTests.FR03_TenantIsDerivedFromCredential_NotFromClientHeaders
AuthenticationTests.HealthLive_IsAnonymous_AndIndependentOfProviders
AuthenticationTests.T02_MissingOrInvalidCredentials_Return401_AndNeverInvokeProvider(apiKey: "", ...)
AuthenticationTests.T02_MissingOrInvalidCredentials_Return401_AndNeverInvokeProvider(apiKey: "TEST-ONLY-KEY-A", ...)
AuthenticationTests.T02_MissingOrInvalidCredentials_Return401_AndNeverInvokeProvider(apiKey: "not-a-real-key", ...)
AuthenticationTests.T02_MissingOrInvalidCredentials_Return401_AndNeverInvokeProvider(apiKey: null, ...)
AuthenticationTests.T02_Unauthenticated_IsRejectedBeforeBodyIsParsed
ConcurrencyTests.NFR05_FailingProvider_ReleasesItsPermit(failure: "permanent")
ConcurrencyTests.NFR05_FailingProvider_ReleasesItsPermit(failure: "transient")
ConcurrencyTests.NFR05_FailingProvider_ReleasesItsPermit(failure: "unexpected")
ConcurrencyTests.NFR06_Synthetic100ConcurrentRequests_NeverExceedConfiguredBound
ConcurrencyTests.T04_ExcessRequests_WaitInBoundedQueue_ThenAreRejected_AndBoundIsNeverExceeded
ConcurrencyTests.T04_ZeroQueue_RejectsImmediatelyWhenFull
EndToEndErrorMappingTests.T10_AliasRoutedToUnregisteredProvider_IsAnInternalError_NotACallerError
EndToEndErrorMappingTests.T10_MockProviderFailures_MapToDistinctSafeErrors(alias: "sim-permanent-failure", ...)
EndToEndErrorMappingTests.T10_MockProviderFailures_MapToDistinctSafeErrors(alias: "sim-transient-failure", ...)
EndToEndErrorMappingTests.T10_ProviderSwap_SameEndpointServesASecondAdapter
EndToEndErrorMappingTests.T10_UnexpectedProviderException_Returns500_WithoutLeakingExceptionDetails
EndToEndErrorMappingTests.UnknownRoutesAndMethods_UseTheSameProblemContract(GET /v1/does-not-exist -> 404)
EndToEndErrorMappingTests.UnknownRoutesAndMethods_UseTheSameProblemContract(GET /v1/generate -> 405)
GenerateSuccessTests.ClientSuppliedCorrelationId_IsNotTrusted
GenerateSuccessTests.T01_SameInput_IsDeterministic_WithDistinctCorrelationIds
GenerateSuccessTests.T01_ValidRequest_ReturnsStableContractWithCorrelationId
MockModelProviderTests.AlreadyCancelledToken_IsHonouredWithoutLatency
MockModelProviderTests.Cancellation_DuringLatency_StopsTheCall
MockModelProviderTests.Latency_IsSimulatedWithInjectedClock
MockModelProviderTests.Output_IsBoundedByMaxOutputTokens
MockModelProviderTests.PermanentFailure_ThrowsPermanentException
MockModelProviderTests.Success_IsDeterministic_AndDependsOnInput
MockModelProviderTests.TransientFailure_ThrowsTransientException
MockModelProviderTests.UnknownProviderModel_IsPermanent
TenantKeyStoreTests.DuplicateKey_IsRejected_WithoutRevealingTheKey
TenantKeyStoreTests.IncompleteEntry_IsRejected(tenantId: "", apiKey: "key")
TenantKeyStoreTests.IncompleteEntry_IsRejected(tenantId: "tenant", apiKey: "")
TenantKeyStoreTests.ResolvesTenantByExactKey
ValidationTests.InvalidRequest_Returns400ProblemDetails_WithoutProviderCall × 11
  (null body, malformed JSON, unknown alias, prompt of wrong type, blank prompt, maxOutputTokens 0,
   maxOutputTokens 51 > max 50, prompt 101 > max 100 chars, unknown member "promt", missing prompt, missing model)
ValidationTests.InvalidValuesAreNotEchoedInErrors
ValidationTests.NonJsonContentType_Returns415
ValidationTests.PromptAtExactLimit_IsAccepted
```

</details>

### 4. Mutation checks (do the tests detect real defects?)

Each defect was injected temporarily, the suite was run, and the source was restored and rebuilt afterwards.

| Injected defect | Tests that failed |
| --- | --- |
| Concurrency lease never disposed (`using` removed) | 6: all three `NFR05_FailingProvider_ReleasesItsPermit` cases, both T-04 tests, NFR-06. The tests waiting for capacity failed at their 15 s deadline, so the suite did not hang. |
| Gate rejection ignored (`IsAcquired` not checked) | 2: both T-04 tests |
| Endpoint trusts an `X-Tenant-Id` header | 1: `FR03_TenantIsDerivedFromCredential_NotFromClientHeaders` |
| `RequireAuthorization()` removed from `/v1/generate` | 4: all `T02_MissingOrInvalidCredentials...` cases |

### 5. Manual run with curl (real Kestrel server, not the test host)

```bash
export AiCore__Tenants__0__TenantId=tenant-a
export AiCore__Tenants__0__ApiKey=dev-key-a-change-me
dotnet run --project src/AiCore.Api --urls http://127.0.0.1:5297
```

Observed:

| Request | Result |
| --- | --- |
| `GET /health/live` | `200`, body `Healthy` |
| `POST /v1/generate` `chat-default` with valid key | `200`, `X-Correlation-Id` header equals body `correlationId`; body `{"correlationId":"2c58…","model":"chat-default","provider":"mock","providerModel":"mock-echo-v1","output":"[mock-echo-v1] Simulated completion for a 41-character prompt (fingerprint e81229037f53).","latencyMs":27,"usage":{"inputTokens":11,"outputTokens":23,"totalTokens":34,"simulated":true}}` |
| No `X-Api-Key` | `401`, `WWW-Authenticate: ApiKey`, `application/problem+json`, `code: unauthenticated` |
| Empty prompt | `400`, `code: invalid_request`, `errors.prompt` |
| `sim-transient-failure` | `503`, `Retry-After: 1`, `code: provider_unavailable` |
| ~70 KB body (limit 64 KiB) | `413`, `code: request_too_large`. Kestrel only. The in-memory test host does not enforce this, so no automated test covers it. |
| 120 parallel curl calls to `chat-slow` (2 s latency; 8 permits + 16 queue) | `24 × 200`, `96 × 503` (= exactly permits + queue admitted) |

Server log for that run: one `ApiKey was challenged` info line and one provider transient-failure warning
containing the alias and correlation ID. No prompt text or API key appeared.

### 6. Not verified / incomplete in M1

- **Body-size limit (413)** was checked manually against Kestrel only. There is no automated test.
- ~~**Queued-request cancellation**~~ is now covered for client cancellation (see §7). Provider-call
  timeouts, and the rest of T-05, are still M2.
- **NuGet restore from an empty cache** wasn't repeated in this session (packages were cached).
- **Out of scope for M1 by plan:** T-03, T-05, T-06, T-07, T-09 (M2) and T-08 (M3). There is no
  provider timeout, no tenant quotas, no retries, no full config validation and no telemetry. See
  `docs/m1-design-notes.md`.
- No performance claims are made. The 100-request test demonstrates the in-flight bound only.

### 7. Review follow-up (31 October 2026)

A review of PR #1 found four issues. Each one was first reproduced with a throwaway test against `a0cdd3a`, then
fixed, and covered by a test that fails when the fix is reverted.

| Finding (reproduced on `a0cdd3a`) | Fix | Test(s) | Fails with the fix reverted |
| --- | --- | --- | --- |
| The README said prompts are not logged, but an unexpected provider exception whose message contained the prompt was logged in full by the framework exception handler (Error level, so above the `Warning` filter) | `GenerationService` maps unexpected provider exceptions to 500 itself and logs only type, alias and correlation ID. Client aborts are excluded by an exception filter. README security note reworded to state exactly what is and isn't guaranteed | `T10_UnexpectedProviderException_Returns500_WithoutLeakingExceptionDetailsToCallerOrLogs` (captures every log entry); `T10_MockProviderFailures_MapToDistinctSafeErrors` now also checks logs | Yes: removing the catch fails the T-10 test. Catching client aborts too fails `ClientCancellationDuringProviderCall_ReturnsThePermit` (asserts no Error log) |
| With `MaxOutputTokens = 50` and `maxOutputTokens` omitted, the provider received 256 (the default), so lowering the maximum did not bound responses | The default is capped: `Math.Min(DefaultOutputTokens, MaxOutputTokens)` | `ValidationTests.OmittedMaxOutputTokens_DefaultIsCappedAtConfiguredMaximum` | Yes |
| Problem Details `type` URIs pointed at `README.md#error-<code>` anchors that did not exist | Anchors added to the README error table for all 12 codes | `ErrorContractTests` (12 anchor cases, one per `ErrorCodes` constant via reflection, plus the `type` value of a live response) | Yes, by construction |
| Request parsing accepted `"maxOutputTokens":"5"`, `"MODEL"` and duplicate members (last wins) | `NumberHandling = Strict`, `PropertyNameCaseInsensitive = false`, `AllowDuplicateProperties = false` | 3 new `InvalidRequest_Returns400ProblemDetails_WithoutProviderCall` cases | Yes: all 3 fail |

Also added, closing a gap listed in §6: `ClientCancellationWhileQueued_LeavesTheQueue_AndNeverReachesTheProvider`
and `ClientCancellationDuringProviderCall_ReturnsThePermit`. Removed the unused `GenerationService.IsKnownAlias`.

```bash
dotnet build AiCore.slnx
```

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```bash
dotnet test AiCore.slnx
```

```
Passed!  - Failed:     0, Passed:    69, Skipped:     0, Total:    69
```

69 = the original 50, plus 3 strict-JSON cases, 1 default-cap test, 2 cancellation tests, 12 anchor cases and
1 `type` test. The suite was then run 10 more times in a row: 10/10 runs were `Passed: 69, Failed: 0, Skipped: 0`.
The original mutation check (lease never disposed) was repeated on `a0cdd3a` and failed the same 6 tests as in §4.

Manual run against Kestrel with `AiCore__Limits__MaxOutputTokens=10`:

| Request | Result |
| --- | --- |
| `chat-default`, `maxOutputTokens` omitted | `200`, `usage.outputTokens: 10` (default capped at the maximum) |
| `{"MODEL":"chat-default","prompt":"x"}` | `400 invalid_request`, detail `... at '$.MODEL'` |
| `{"model":"chat-default","prompt":"a","prompt":"b"}` | `400 invalid_request`, detail `... at '$.prompt'` |
| `"maxOutputTokens":"5"` | `400` |
| ~70 KB body | `413` (unchanged) |

The server log contained only host lifetime lines. No prompt text or API key appeared.

## M2 — Reliability and tenant controls

**Date:** 1 November 2026  
**Environment:** Claude Code cloud session, Ubuntu 24.04.5 LTS (x64), .NET SDK 10.0.114 from the Ubuntu
archive package `dotnet-sdk-10.0` (as for M1).  
**Commit verified:** `1be94ac` (M2 code and docs). This report was added in the following commit.

Everything below was observed in this session on `1be94ac`. Nothing is extrapolated.

### 1. Build (clean `bin/`/`obj/`)

```bash
dotnet build AiCore.slnx
```

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2. Tests

```bash
dotnet test AiCore.slnx --no-build
```

```
Passed!  - Failed:     0, Passed:   144, Skipped:     0, Total:   144, Duration: 2 s - AiCore.Tests.dll (net10.0)
```

0 failed, 0 skipped, exit code 0. 144 = the 69 M1 cases plus 75 new cases. The M1 cases are unchanged
except that `ConcurrencyTests.AssertNoLeakedCapacity` now also asserts that no tenant quota slot is left
held.

**Repeatability:** the suite was run 10 more times in a row (`dotnet test --no-build`). Result: 10/10 runs were
`Passed: 144, Failed: 0, Skipped: 0`.

**Fresh clone (NFR-01):** `git clone` of `1be94ac` into an empty directory, then `dotnet build` →
`0 Warning(s) 0 Error(s)`, and `dotnet test` → `Passed: 144, Failed: 0, Skipped: 0`. As in M1, NuGet packages
came from the machine cache. No new packages were added in M2.

**Determinism (NFR-02):** every timeout, queue-wait, backoff and quota-refill test runs on a `FakeTimeProvider`.
A deadline fires only when the test advances fake time. Waits on observable conditions (calls in flight,
queue depth, cancellation observed) have a 10–15 s failure deadline, so a defect fails the test rather than
hanging the suite (see M05 in §4).

### 3. Required M2 tests → test cases

| ID | Scenario | Test(s) | Result |
| --- | --- | --- | --- |
| T-03 | Tenant A reaches its quota; tenant B has capacity | `TenantQuotaTests.T03_TenantAtRateQuota_Gets429WithRetryAfter_WhileOtherTenantProceeds` (A: 3 × 200, then 429 `rate_quota_exceeded` with `Retry-After: 10`; B: 3 × 200; refill after exactly 10 s of fake time), `T03_TenantAtConcurrencyQuota_Gets429_WhileOtherTenantStillReachesTheProvider` (4 shared permits, per-tenant limit 2: A's third call gets 429 `concurrency_quota_exceeded` while B's two calls reach the provider), `T03_PerTenantOverride_AppliesOnlyToThatTenant`, `T03_RequestRejectedForGlobalCapacity_GetsItsQuotaTokenBack`, `T03_InvalidRequests_DoNotConsumeQuota`; unit: 7 `TenantQuotaServiceTests` (refill and burst cap, refund rules, idempotent release, independent tenants, overrides, 200 concurrent admissions never exceed limits) | Pass |
| T-05 | Provider call cancels or times out: proper error, no leaked capacity | `TimeoutAndCancellationTests.T05_ProviderTimeout_Returns504_IsNotRetried_AndReleasesCapacity`, `T05_AfterTimeout_PermitIsHeldUntilTheProviderActuallyStops`, `T05_ProviderIgnoringCancellation_IsAbandonedAfterGrace_SoItsPermitIsNotHeldIndefinitely`, `T05_QueueWaitTimeout_Returns503_FreesTheQueueSlot_AndRefundsTheQuotaToken`, `T05_ClientCancellationDuringProviderCall_StopsTheProvider_AndIsNotATimeout`; unit: `ProviderInvokerTests` (client cancellation waits for the provider to stop, zero grace, deadline during backoff, adapter's own cancellation is not a timeout) | Pass |
| T-06 | Provider fails permanently: safe error, no unlimited retries | `RetryPolicyTests.T06_PermanentFailure_IsNotRetried_AndReturnsASafe502` (exactly 1 call with `MaxAttempts = 3`; message and prompt not in the body), `T06_RepeatedPermanentFailures_NeverAccumulateRetries` (4 requests → 4 calls, all attempt 1) | Pass |
| T-07 | Retryable transient error: bounded attempts | `RetryPolicyTests.T07_NotProcessedFailure_IsRetried_AndTheRetryCanSucceed` (attempts 1, 2; same correlation ID), `T07_NotProcessedFailures_AreRetriedExactlyUpToMaxAttempts` (1, 2, 3 → exactly that many calls, then 503), `T07_AmbiguousTransientFailure_IsNotRetried`, `T07_UnexpectedException_IsNotRetried`, `T07_RetriesConsumeOneQuotaTokenPerRequest_NotPerAttempt`, `T07_MockThrottlingAliases_ExerciseTheRetryPolicyThroughThePublicApi` (2 cases, default config); unit: `ProviderInvokerTests.Backoff_UsesTheInjectedClock_BeforeTheNextAttempt`, `DeadlineDuringBackoff_IsReportedAsNotProcessedTransientFailure_NotAsTimeout` | Pass |
| T-09 | Invalid configuration rejected at startup | `ConfigurationValidationTests.T09_InvalidSetting_FailsHostStartup_WithAnActionableMessage` (24 cases through real host startup: zero/negative concurrency, queue, queue wait, timeout, grace, attempts, backoff, request-size, prompt/token limits, every quota field, override for an unknown tenant, mock latency, incomplete model route, incomplete tenant), `T09_AllProblemsAreReportedTogether`, `T09_MisspeltKey_FailsStartup_InsteadOfSilentlyUsingTheDefault`, `T09_NonNumericValue_FailsStartup_NamingTheKey`, `T09_DuplicateApiKey_IsRejected_WithoutRevealingIt`, `T09_ShippedConfiguration_IsValid`, plus 3 cases checking that valid edge values are accepted | Pass |

Also added: 5 mock provider tests (throttling is marked not-processed, throttled-once recovers on attempt 2,
plain transient failure is ambiguous, `HonourCancellation = false` ignores the token) and 3 `ErrorContractTests`
anchor cases for the new codes `rate_quota_exceeded`, `concurrency_quota_exceeded` and `provider_timeout`.

<details>
<summary>The 75 test cases added in M2 (all Passed)</summary>

```
ConfigurationValidationTests.T09_AllProblemsAreReportedTogether
ConfigurationValidationTests.T09_BackoffRule_OnlyAppliesWhenRetriesAreEnabled × 2
ConfigurationValidationTests.T09_DuplicateApiKey_IsRejected_WithoutRevealingIt
ConfigurationValidationTests.T09_InvalidSetting_FailsHostStartup_WithAnActionableMessage × 24
  (Concurrency: MaxConcurrentProviderCalls 0 and -4, MaxQueuedRequests -1, MaxQueueWaitMs 0;
   Resilience: ProviderTimeoutMs 0 and -30000, CancellationGraceMs -1, MaxAttempts 0 and 10, RetryBackoffMs -5,
   RetryBackoffMs 30000 = ProviderTimeoutMs; Limits: MaxPromptChars 0, MaxOutputTokens -1, DefaultOutputTokens 0,
   MaxRequestBodyBytes 0; Quotas: Default RequestsPerMinute 0, BurstRequests -2, MaxConcurrentRequests 0,
   tenant-a RequestsPerMinute 0, tenant-a MaxConcurrentRequests -1, unknown tenant "tenant-typo";
   MockProvider LatencyMs -1; Models route missing ProviderModel; Tenants entry missing ApiKey)
ConfigurationValidationTests.T09_MisspeltKey_FailsStartup_InsteadOfSilentlyUsingTheDefault
ConfigurationValidationTests.T09_NonNumericValue_FailsStartup_NamingTheKey
ConfigurationValidationTests.T09_ShippedConfiguration_IsValid
ConfigurationValidationTests.T09_ZeroQueueAndZeroGrace_AreValidChoices
ErrorContractTests.EveryErrorCode_HasTheReadmeAnchorItsTypeLinksTo × 3 (new codes)
MockModelProviderTests.HonourCancellationFalse_IgnoresTheToken_UntilLatencyElapses
MockModelProviderTests.ThrottledOnFirstAttempt_SucceedsOnTheRetry
MockModelProviderTests.Throttled_IsTransient_AndMarkedNotProcessed_OnEveryAttempt × 2
MockModelProviderTests.TransientFailure_IsAmbiguous_SoNotMarkedNotProcessed
ProviderInvokerTests.AdapterOwnCancellation_IsUnexpected_NotATimeoutOrClientAbort
ProviderInvokerTests.Backoff_UsesTheInjectedClock_BeforeTheNextAttempt
ProviderInvokerTests.ClientCancellation_PropagatesAsOperationCanceled_AfterTheProviderStops
ProviderInvokerTests.DeadlineDuringBackoff_IsReportedAsNotProcessedTransientFailure_NotAsTimeout
ProviderInvokerTests.SynchronouslyThrowingAdapter_IsHandledLikeAnAsyncFailure
ProviderInvokerTests.UnexpectedException_KeepsOnlyItsType
ProviderInvokerTests.ZeroGrace_AbandonsANonStoppingCallImmediately
RetryPolicyTests.T06_PermanentFailure_IsNotRetried_AndReturnsASafe502
RetryPolicyTests.T06_RepeatedPermanentFailures_NeverAccumulateRetries
RetryPolicyTests.T07_AmbiguousTransientFailure_IsNotRetried
RetryPolicyTests.T07_MockThrottlingAliases_ExerciseTheRetryPolicyThroughThePublicApi × 2 (sim-throttled-once → 200, sim-throttled → 503)
RetryPolicyTests.T07_NotProcessedFailure_IsRetried_AndTheRetryCanSucceed
RetryPolicyTests.T07_NotProcessedFailures_AreRetriedExactlyUpToMaxAttempts × 3 (1, 2, 3)
RetryPolicyTests.T07_RetriesConsumeOneQuotaTokenPerRequest_NotPerAttempt
RetryPolicyTests.T07_UnexpectedException_IsNotRetried
TenantQuotaServiceTests.Bucket_StartsFull_RefillsAtTheConfiguredRate_AndNeverExceedsBurst
TenantQuotaServiceTests.ConcurrencyLimit_CountsActiveLeases_AndDoesNotSpendATokenWhenRejecting
TenantQuotaServiceTests.ConcurrentAdmissions_NeverExceedTheLimits
TenantQuotaServiceTests.Lease_RefundsItsToken_OnlyIfTheProviderWasNeverCalled
TenantQuotaServiceTests.Override_ReplacesOnlyTheFieldsItSets
TenantQuotaServiceTests.Refund_IsCappedAtBurst
TenantQuotaServiceTests.Tenants_HaveIndependentState
TenantQuotaTests.T03_InvalidRequests_DoNotConsumeQuota
TenantQuotaTests.T03_PerTenantOverride_AppliesOnlyToThatTenant
TenantQuotaTests.T03_RequestRejectedForGlobalCapacity_GetsItsQuotaTokenBack
TenantQuotaTests.T03_TenantAtConcurrencyQuota_Gets429_WhileOtherTenantStillReachesTheProvider
TenantQuotaTests.T03_TenantAtRateQuota_Gets429WithRetryAfter_WhileOtherTenantProceeds
TimeoutAndCancellationTests.T05_AfterTimeout_PermitIsHeldUntilTheProviderActuallyStops
TimeoutAndCancellationTests.T05_ClientCancellationDuringProviderCall_StopsTheProvider_AndIsNotATimeout
TimeoutAndCancellationTests.T05_ProviderIgnoringCancellation_IsAbandonedAfterGrace_SoItsPermitIsNotHeldIndefinitely
TimeoutAndCancellationTests.T05_ProviderTimeout_Returns504_IsNotRetried_AndReleasesCapacity
TimeoutAndCancellationTests.T05_QueueWaitTimeout_Returns503_FreesTheQueueSlot_AndRefundsTheQuotaToken
```

</details>

### 4. Mutation checks (do the tests detect real defects?)

Each defect was injected into `1be94ac` by a script, then the solution was rebuilt and the full suite run.
The source was restored and the suite re-run afterwards (`Passed: 144`, clean `git status`).
Counts include theory cases.

| # | Injected defect | Failed | Examples of failing tests |
| --- | --- | --- | --- |
| M01 | Quota token never refunded | 3 | `T03_RequestRejectedForGlobalCapacity_GetsItsQuotaTokenBack`, `T05_QueueWaitTimeout_…_RefundsTheQuotaToken`, `Lease_RefundsItsToken_…` |
| M02 | Tenant concurrency slot never released | 22 | every `AssertNoLeakedCapacity` user, incl. the M1 T-04/NFR-05/NFR-06 tests |
| M03 | All tenants share one bucket | 9 | all 4 cross-tenant T-03 tests, `Tenants_HaveIndependentState` |
| M04 | `using` removed from the tenant lease | 20 | T-03 concurrency, T-05, T-06, T-07 and M1 concurrency tests |
| M05 | Provider deadline never fires | 5 | the 3 provider-timeout T-05 tests (fail at their 15 s deadline, no hang), 2 invoker tests |
| M06 | Ambiguous transient failures retried | 1 | `T07_AmbiguousTransientFailure_IsNotRetried` |
| M07 | Permanent failures retried up to `MaxAttempts` | 4 | both T-06 tests, `T10_MockProviderFailures_…`, an invoker test |
| M08 | Permit released without waiting for the provider to stop | 4 | `T05_AfterTimeout_PermitIsHeldUntilTheProviderActuallyStops`, abandonment tests |
| M09 | Timeout mapped to 503 instead of 504 | 3 | the three T-05 provider-timeout tests |
| M10 | Queue wait not bounded in time | 1 | `T05_QueueWaitTimeout_…` |
| M11 | Validator not registered | 26 | 24 `T09_InvalidSetting…` cases, `AllProblemsAreReportedTogether`, `DuplicateApiKey…` |
| M12 | Lenient binding (unknown keys ignored) | 1 | `T09_MisspeltKey_…` |
| M13 | Provider-call start never marked (every token refunded) | 4 | `T03_TenantAtRateQuota_…`, `T03_PerTenantOverride_…`, `T07_RetriesConsumeOneQuotaTokenPerRequest_…` |

### 5. Manual run against Kestrel (real server, not the test host)

Tenants `tenant-a` and `tenant-b` were configured through environment variables with throwaway keys. The
server was started from the `1be94ac` build with `dotnet bin/Debug/net10.0/AiCore.Api.dll --urls http://127.0.0.1:5297`
(equivalent to `dotnet run --project src/AiCore.Api`).

**Startup validation** (`dotnet run`, process exits with an unhandled exception, exit code 134):

| Environment | First line of output |
| --- | --- |
| `AiCore__Concurrency__MaxConcurrentProviderCalls=0`, `AiCore__Resilience__ProviderTimeoutMs=-1`, `AiCore__Quotas__Default__RequestsPerMinute=0` | `OptionsValidationException: AiCore:Concurrency:MaxConcurrentProviderCalls must be at least 1 but is 0 (the maximum simultaneous provider calls per process).; AiCore:Resilience:ProviderTimeoutMs must be at least 1 but is -1 (…).; AiCore:Quotas:Default:RequestsPerMinute must be at least 1 but is 0 (…).` |
| `AiCore__Concurrency__MaxConcurentProviderCalls=2` (typo) | `InvalidOperationException: 'ErrorOnUnknownConfiguration' was set on the provided BinderOptions, but the following properties were not found on the instance of AiCore.Api.Configuration.ConcurrencyOptions: 'MaxConcurentProviderCalls'` |
| `AiCore__Resilience__ProviderTimeoutMs=thirty` | `InvalidOperationException: Failed to convert configuration value 'thirty' at 'AiCore:Resilience:ProviderTimeoutMs' to type 'System.Int32'.` |

**Failure handling** with `AiCore__Resilience__ProviderTimeoutMs=2000`, other settings default:

| Request (tenant-a) | Status, time | Body `code`, `Retry-After` |
| --- | --- | --- |
| `chat-default` | 200, 0.11 s | — |
| `sim-throttled-once` | 200, 0.29 s (10 ms + 250 ms backoff + 10 ms) | — |
| `sim-throttled` | 503, 0.31 s (2 attempts) | `provider_unavailable`, 1 |
| `sim-transient-failure` | 503, 0.01 s (1 attempt) | `provider_unavailable`, 1 |
| `sim-permanent-failure` | 502, 0.02 s | `provider_error`, none |
| `sim-timeout` | 504, 2.01 s | `provider_timeout`, none |
| `sim-unresponsive` (ignores cancellation) | 504, 3.01 s (timeout + 1 s grace) | `provider_timeout`, none |

Server log for that run (host lifetime lines omitted), verbatim apart from line wrapping:

```
info: AiCore.Api.Execution.ProviderInvoker[0]
      Provider mock did not process attempt 1 of 2; retrying after 250 ms. Reason: Mock provider simulated throttling; the request was not processed. (correlation c76f6987d9dd4935a3f221975d0b222f)
info: AiCore.Api.Execution.ProviderInvoker[0]
      Provider mock did not process attempt 1 of 2; retrying after 250 ms. Reason: Mock provider simulated throttling; the request was not processed. (correlation 12035b7fd71b4ea08e0da5e2132ad55e)
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock transient failure for alias sim-throttled after 2 attempt(s) (request not processed: True). Reason: Mock provider simulated throttling; the request was not processed. (correlation 12035b7fd71b4ea08e0da5e2132ad55e)
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock transient failure for alias sim-transient-failure after 1 attempt(s) (request not processed: False). Reason: Mock provider simulated a transient failure. (correlation 18fadde2d2784e87b5453ce84c00e97c)
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock permanent failure for alias sim-permanent-failure: Mock provider simulated a permanent failure. (correlation 15e7a39efdf44668b4d08d58b1af6b75)
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock timed out after 2000 ms for alias sim-timeout on attempt 1 (correlation fffc07d3cf2e473cb841fa6231bcc6c7)
fail: AiCore.Api.Execution.ProviderInvoker[0]
      Provider mock did not stop within 1000 ms of cancellation (attempt 1, correlation 8c79bfbe69ef44bcbd84f702d4c19b52); the call is abandoned and its permit released. Abandoned calls still running: 1
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock timed out after 2000 ms for alias sim-unresponsive on attempt 1 (correlation 8c79bfbe69ef44bcbd84f702d4c19b52)
```

The prompt marker sent with every request (`MANUAL-PROMPT-MARKER`) and the API keys appear 0 times in the log.

**Quotas and capacity** (fresh server for each block, default quotas: 60/min, burst 20, 4 concurrent per tenant):

| Scenario | Result |
| --- | --- |
| 25 sequential `chat-default` calls, tenant-a | 20 × 200, then 5 × 429 `rate_quota_exceeded`, `Retry-After: 1` |
| tenant-b immediately afterwards | 200 |
| 10 parallel `chat-slow` (2 s) per tenant, 20 at once (8 shared permits) | each tenant: 4 × 200, 6 × 429 `concurrency_quota_exceeded`. Total 8 × 200 = the permit count, split evenly. |
| 1 permit, `MaxQueueWaitMs=500`: tenant-a holds the permit with `chat-slow`; tenant-b calls `chat-default` | tenant-b: 503 `capacity_exhausted` after 0.55 s; tenant-a: 200 |
| Same server: client gives up after 0.5 s on a 2 s call (`curl --max-time 0.5`, exit 28), then a new call | new call 200 (the single permit was returned) |

That last server logged nothing at Warning level or above. A client abort is not a service failure.

**Request body limit** (now read from the validated options instead of raw configuration):

| Server setting | Request | Result |
| --- | --- | --- |
| default (`MaxRequestBodyBytes` 65536) | ~70 KB body | 413 `request_too_large` |
| `AiCore__Limits__MaxRequestBodyBytes=2048` | ~3 KB body | 413 `request_too_large` |
| `AiCore__Limits__MaxRequestBodyBytes=2048` | small body | 200 |

### 6. Not verified / incomplete in M2

- **Multi-replica behaviour** is not tested. All enforcement is per process (`docs/m2-design-notes.md` §5).
- **Abandoned calls** are counted and logged, but nothing limits them or acts on the count. *(Addressed by the
  review follow-up in §7.)*
- **Body-size limit (413)** is still verified manually against Kestrel only (§5). The in-memory test host
  doesn't enforce it.
- **NuGet restore from an empty cache** was not repeated.
- **Out of scope for M2 by plan:** T-08 and structured telemetry/metrics (M3), circuit breaker (design only),
  distributed quotas, TPM quotas, idempotency keys, streaming.
- No performance claims are made. Timings above come from a mock with configured latencies.

### 7. Review follow-up (1 November 2026)

The automated review of PR #2 (`fe6a9bc`) left 10 comments. For each finding I first wrote a test against the
unchanged code, then fixed it, then confirmed the test fails when the fix is reverted. Fixes are in `be07102`.

| # | Finding | Reproduced on `fe6a9bc` | Fix | Test(s) | Fails with fix reverted |
| --- | --- | --- | --- | --- | --- |
| 1 | An abandoned call freed its tenant's concurrency slot as well as the global permit, and nothing capped abandoned calls | Yes: with `MaxConcurrentRequests=1`, tenant A's next request was admitted and reached the provider while A's abandoned call was still running (the test hit its 15 s deadline waiting for the 429) | The tenant slot follows the real call (`TenantQuotaLease.ReleaseWhenCompleted`). New setting `MaxAbandonedCallsPerProvider` (default 8): at the limit, new calls to that provider get a fast 503 `provider_unavailable` without using quota. The global permit is still released immediately (NFR-05). | `T05_AbandonedCall_KeepsItsTenantConcurrencySlot_UntilItReallyEnds`, `T05_TooManyAbandonedCalls_RejectNewCallsToThatProvider_UntilTheyEnd`, updated `T05_ProviderIgnoringCancellation_…` | Yes: R1a fails 2 tests, R1b fails 1 |
| 2 | An adapter that blocks before returning its `Task` defeated the deadline and grace period | Yes: `invocation` never completed after the deadline (`Condition not reached: deadline enforced`) | Attempts start via `Task.Run` | `ProviderInvokerTests.AdapterThatBlocksBeforeReturningATask_CannotHoldTheCallerPastDeadlineAndGrace` | Yes: R2 fails it (and 15 other tests that relied on sync-throw capture) |
| 3 | A 503 where no attempt was processed still charged a quota token | Behaviour confirmed | **Not changed (deliberate).** The quota limits provider calls and protects the upstream from client retry storms; a client honouring `Retry-After: 1` never drains its bucket on 503s. The contradicting doc sentences were corrected, and the rationale is in design notes §4.1. | `T07_NotProcessedFailure_StillChargesTheRequestToken` pins the decision | n/a (decision) |
| 4 | A success that arrived during the grace window was discarded and reported as 504 | Yes: `Expected: OK, Actual: GatewayTimeout` | After the grace wait, a completed call's real outcome is used: success → 200; "not processed" → 503; otherwise 504 | `T05_CompletionDuringGracePeriod_IsReturned_NotReportedAsATimeout` (also checks the Warning log) | Yes: R4 fails it; R4b (the not-processed branch) fails `DeadlineDuringBackoff_…` |
| 5 | Floating-point drift in the token bucket | Yes: after 0.7 s, `Retry-After` was 0.3000001 s, not 0.3 s. The sequence 0.7 + 0.2 + 0.1 s sums to 0.9999999999999999 tokens in IEEE doubles. | Integer credit: one token = `TicksPerMinute` units, refilled at `RequestsPerMinute` units per tick, overflow-safe | `TenantQuotaServiceTests.PartialRefills_AddUpToExactlyOneToken` | Reproduced against the original `double` code (not a one-line revert) |
| 6 | Capacity-holding demo aliases shipped in the base config | Yes: `sim-timeout` and `sim-unresponsive` were configured in the Testing and Production environments | **Partly as suggested:** those two aliases moved to `appsettings.Development.json`. The fast-failing `sim-*` aliases (milliseconds, no held capacity) stay in the base config, because the end-to-end tests and the README use them. | `FaultInjectionAliasesThatHoldCapacity_AreConfiguredOnlyInDevelopment` (3 environments) | Reproduced on `fe6a9bc` (Testing and Production failed) |
| 7 | When both limits were hit, the concurrency 429 had `Retry-After: 1` although the rate bucket was empty | Yes: `Expected: 00:01:00, Actual: 00:00:01` | `Retry-After` = max(1 s, time until the next token) | `ConcurrencyRejection_RetryAfter_AlsoCoversAnEmptyRateBucket`, `…_IsOneSecondWhenTokensRemain` | Yes: R7 fails it |
| 8 | An undefined numeric `MockBehavior` (e.g. `7`) passed validation and behaved as Success | Yes: no exception at startup | `Enum.IsDefined` check with the allowed names in the message | New `T09_InvalidSetting…` case (`Behavior = "7"`) | Yes: R8 fails it |
| 9 | The gate allocated the queue-timeout CTS and timer even when a permit was free | By reading the code (efficiency) | `AttemptAcquire` fast path. With `OldestFirst` it fails while waiters are queued, so FIFO order is kept. | Existing T-04/T-05 queue tests still pass | No: R9 passes all tests, as expected for a pure efficiency change. Disclosed, not claimed. |
| 10 | The contention test never exercised the rate limit | Coverage gap (the test passed on `fe6a9bc`) | Added a case bounded by the bucket, not by concurrency | `ConcurrentAdmissions_NeverExceedTheRateLimit` (200 racing admissions: exactly 20 admitted, 180 `RequestRate` rejections) | n/a (coverage) |

**Build and tests on `be07102`:**

```bash
dotnet build AiCore.slnx
```

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```bash
dotnet test AiCore.slnx
```

```
Passed!  - Failed:     0, Passed:   157, Skipped:     0, Total:   157, Duration: 2 s - AiCore.Tests.dll (net10.0)
```

157 = the 144 from §2 plus 13 new cases: 3 timeout/cancellation, 1 invoker, 4 quota-service, 1 retry-policy,
1 validation case and 3 environment cases. The suite was then run 10 more times in a row (10/10 green), and a fresh
clone of `be07102` built with 0 warnings and passed 157/157.

**Mutation checks repeated on `be07102`.** All 13 from §4 are still detected (M08 was updated to the changed
line it targets). Counts grew where new tests also catch them: M02 24, M03 13, M05 9, M08 8, M09 4, M11 27,
M13 5 failures. The review reverts R1a–R8 are in the table above.

**Manual run against Kestrel (`be07102` build):**

| Setup | Request | Result |
| --- | --- | --- |
| Production environment (no `ASPNETCORE_ENVIRONMENT`) | `sim-timeout` | 400 `unknown_model` (alias not configured) |
| Production environment | `sim-throttled-once` | 200 (fast-failing aliases still available) |
| Development, `ProviderTimeoutMs=2000`, `MaxAbandonedCallsPerProvider=1`, `mock-unresponsive-v1` latency 5 s | t=0: tenant-a `sim-unresponsive` | 504 after 3.1 s; the call is abandoned |
| same | t≈3 s: tenant-b `chat-default` (same `mock` provider) | 503 `provider_unavailable` after 8 ms, never reaching the provider |
| same | t≈5.5 s (abandoned call ended at 5 s): tenant-b `chat-default` | 200 |
| same | `sim-timeout` | 504 after 2.0 s |

Server log for the Development run:

```
fail: AiCore.Api.Execution.ProviderInvoker[0]
      Provider mock did not stop within 1000 ms of cancellation (attempt 1, correlation a72e2e8e5bee46d99d5507223bd106df); the call is abandoned and its permit released. Abandoned calls still running: 1 for this provider (limit 1), 1 in total
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock timed out after 2000 ms for alias sim-unresponsive on attempt 1 (correlation a72e2e8e5bee46d99d5507223bd106df)
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock has 1 abandoned calls still running; refusing alias chat-default until they end (correlation 044615378dd8433594b841fd587808e5)
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock timed out after 2000 ms for alias sim-timeout on attempt 1 (correlation 295d9b428f104f169dda77d8c18848e8)
```

The prompt marker and API keys appear 0 times in the log.

**Still not done after the review:** time-based recovery for a provider whose calls never return (it stays refused
until restart; design notes §3.3), and a thread-pool thread is still held by an adapter that blocks.

### 8. Second review follow-up (1 November 2026)

The follow-up review of `476166e` resolved 9 of the 10 first-round threads and accepted the rationale for the
tenth (token charging, design notes §4.1). It raised 6 new findings about the first-round fixes. Each was again
reproduced with a test on the unchanged code before it was fixed. Fixes are in `c2fef3d`.

| # | Finding | Reproduced on `476166e` | Fix | Test(s) | Fails with fix reverted |
| --- | --- | --- | --- | --- | --- |
| N1 | The abandoned-call limit was counted per provider, so one hanging model blocked every model of that provider | Yes: after one abandoned `hang` call, `chat-default` on the same `mock` provider got `ServiceUnavailable` instead of `OK` | Limit keyed per route (provider + provider model); setting renamed `MaxAbandonedCallsPerProvider` → `MaxAbandonedCallsPerRoute` | `T05_AbandonedCallLimit_IsPerRoute_OtherModelsOfTheProviderStayAvailable` | Yes: N1 fails 2 tests |
| N2 | An abandoned call held its tenant's slot with no time limit, giving a 429 that blamed the tenant and a lockout from healthy providers | Yes: after abandonment, `ActiveRequests` was 1 instead of 0 | **Different fix from the two suggested:** abandoned calls no longer hold the tenant's slot at all. They are bounded per route (N1, N3) and answered with a correctly attributed 503. Both suggestions would still have blocked the tenant from healthy providers. Design notes §3.3 explain why. | `T05_AbandonedCall_DoesNotHoldItsTenantSlot_SoTheTenantIsNeitherBlamedNorLockedOut`; updated `T05_ProviderIgnoringCancellation_…`; removed the first-round `T05_AbandonedCall_KeepsItsTenantConcurrencySlot_…`, which asserted the old design | Reproduced on `476166e` (the old code was removed, not one line) |
| N3 | Queued requests skipped the limit check, so the real cap was about 31 with the defaults | Yes: with 2 permits, 1 queued and a limit of 1, the queued request reached the hanging provider and got `GatewayTimeout` instead of `ServiceUnavailable` | The route limit is re-checked after a permit is acquired; on refusal the permit is returned and the token refunded | `T05_QueuedRequests_RecheckTheAbandonedCallLimit_AfterGettingAPermit` (2 calls, 2 orphans = limit + permits − 1) | Yes: N3 fails it |
| N4 | A permanent failure during the grace period was reported as 504 | Yes: `Expected: PermanentFailure, Actual: TimedOut` | 502 branch next to the success and not-processed ones | `ProviderInvokerTests.PermanentFailureDuringGrace_IsReportedAsPermanent_NotAsTimeout` | Yes: N4 fails it |
| N5 | Every refused request logged a Warning | Yes: 5 refusals gave 5 Warnings | Transitions are logged once each (reached: Warning, below: Information); refused requests at Debug | `T05_RefusalsAtTheAbandonedCallLimit_LogTheTransitions_NotEveryRequest` (no Warning added during refusals, whatever the text) | Yes: N5 fails it |
| N6 | The gate's fast path inflated the limiter's `TotalFailedLeases` | Yes: a throwaway assertion in the 100-request test showed `TotalFailedLeases` = 92 with zero rejections | Gate-owned counters `QueueFullRejections` and `QueueWaitTimeouts`, documented on `GetStatistics` | Assertions added to T-04 (10 rejections), NFR-06 (0 despite 92 fast-path misses) and the queue-wait T-05 test (1 timeout) | Yes: N6 fails T-04 |

Also found while re-running the mutations: the first-round revert R4b (a "not processed" failure during the grace
period must stay 503) was previously caught only by a timing-dependent path in `DeadlineDuringBackoff_…`, and on
this re-run it passed undetected. A deterministic test now covers it:
`NotProcessedFailureDuringGrace_StaysASafeTransientFailure_NotATimeout`.

**Build and tests on `c2fef3d`:** `dotnet build AiCore.slnx` → `0 Warning(s) 0 Error(s)`;
`dotnet test AiCore.slnx` →

```
Passed!  - Failed:     0, Passed:   162, Skipped:     0, Total:   162, Duration: 2 s - AiCore.Tests.dll (net10.0)
```

162 = the 157 from §7, minus 1 test superseded by N2, plus 6 new: N1, N2, N3, N5 integration tests and the N4
and R4b invoker tests. The suite was then run 10 more times in a row (10/10 green), and a fresh clone of
`c2fef3d` built with 0 warnings and passed 162/162.

**Full mutation run on `c2fef3d`** (25 mutations; counts include theory cases. An earlier version of this report
said 26: the table below always had 25 rows, and the total was miscounted after R1a was retired. See §12):

| Mutation | Failed | | Mutation | Failed |
| --- | --- | --- | --- | --- |
| M01 refund never given | 3 | | R1b no abandoned-call limit | 5 |
| M02 tenant slot never released | 25 | | R2 attempt started inline | 16 |
| M03 one shared bucket | 11 | | R4 grace success → 504 | 1 |
| M04 tenant lease not disposed | 23 | | R4b grace not-processed → 504 | 1 |
| M05 no deadline | 14 | | R7 Retry-After ignores bucket | 1 |
| M06 ambiguous failures retried | 1 | | R8 enum not validated | 1 |
| M07 permanent failures retried | 4 | | R9 gate fast path removed | **0** (efficiency only, as expected) |
| M08 permit released before stop | 13 | | N1 limit per provider | 2 |
| M09 timeout → 503 | 7 | | N3 no re-check after permit | 1 |
| M10 queue wait unbounded | 1 | | N4 grace permanent → 504 | 1 |
| M11 validator removed | 27 | | N5 refusals at Warning | 1 |
| M12 lenient binding | 1 | | N6 rejection counter not incremented | 1 |
| M13 token always refunded | 5 | | | |

The source was restored after each mutation, and the final suite passed 162/162 with a clean `git status`.

**Manual run against Kestrel (`c2fef3d` build).** Settings: `ASPNETCORE_ENVIRONMENT=Development`,
`ProviderTimeoutMs=2000`, `MaxAbandonedCallsPerRoute=1`, `MaxConcurrentRequests=1` per tenant, and
`mock-unresponsive-v1` latency 5 s:

| Time | Request | Result |
| --- | --- | --- |
| t=0 | tenant-a `sim-unresponsive` | 504 after 3.1 s; abandoned |
| t≈3 s | tenant-a `sim-unresponsive` (route at its limit) | 503 `provider_unavailable` after 8 ms, not 429 |
| t≈3 s | 4 more refused `sim-unresponsive` calls from tenant-b | all 503 |
| t≈3 s | tenant-a `chat-default` (same provider, other model; tenant limit 1) | 200: tenant not locked out, route not blocked |
| t≈5.5 s | tenant-b `sim-throttled-once` | 200 |

Server log (host lines omitted). There are 3 lines at Warning or above in total, and none for the five refusals:

```
fail: AiCore.Api.Execution.ProviderInvoker[0]
      Provider mock did not stop within 1000 ms of cancellation (route mock/mock-unresponsive-v1, attempt 1, correlation cbf5c15e80c84e42a6e280beb91bab35); the call is abandoned and its permit released. Abandoned calls still running: 1 on this route (limit 1), 1 in total
warn: AiCore.Api.Execution.ProviderInvoker[0]
      Route mock/mock-unresponsive-v1 reached its limit of 1 abandoned calls; refusing new calls to it with 503 until they end
warn: AiCore.Api.Execution.GenerationService[0]
      Provider mock timed out after 2000 ms for alias sim-unresponsive on attempt 1 (correlation cbf5c15e80c84e42a6e280beb91bab35)
info: AiCore.Api.Execution.ProviderInvoker[0]
      Route mock/mock-unresponsive-v1 is below its abandoned-call limit again (0 still running); accepting new calls
info: AiCore.Api.Execution.ProviderInvoker[0]
      Provider mock did not process attempt 1 of 2; retrying after 250 ms. Reason: Mock provider simulated throttling; the request was not processed. (correlation 0c1e15b59b204b959b78b67cad61f1c8)
```

The prompt marker and API keys appear 0 times in the log. Note that §7 refers to the setting by its earlier name,
`MaxAbandonedCallsPerProvider`, and describes tenant slots held by abandoned calls. Both are superseded by this
section.

### 9. Third review follow-up (1 November 2026)

The third review (of `a354b38`) confirmed and resolved all 6 round-2 findings and raised one new one. It was
reproduced on the unchanged code before it was fixed. The fix is in `16b2d7f`.

| # | Finding | Reproduced on `a354b38` | Fix | Test(s) | Fails with fix reverted |
| --- | --- | --- | --- | --- | --- |
| N7 | With abandoned calls releasing their tenant's slot immediately, one tenant could use up a route's shared abandoned-call budget alone. The cheapest way is to disconnect from its own requests: the route then closes for every tenant, within the attacker's own quota. | Yes: with `MaxConcurrentRequests=2` and `MaxAbandonedCallsPerRoute=3`, tenant A aborted 2 calls, then sent 2 more. Those were admitted, reached the route and were abandoned too (`Expected: ServiceUnavailable, Actual: GatewayTimeout`), using up the shared budget. | The reviewer's first option: a tenant's requests in progress plus its abandoned calls on a route never exceed its `MaxConcurrentRequests`, enforced in `TenantQuotaService` under the tenant's lock. Only that tenant, only on that route, gets 503. The route-wide limit stays as a backstop, and a startup warning flags `MaxConcurrentRequests` ≥ `MaxAbandonedCallsPerRoute`. The second option (exclude client aborts route-wide) was not taken; see design notes §3.3. | `T05_OneTenantAbortingItsOwnCalls_CannotUseUpARoutesAbandonedCallBudget`; unit tests `AbandonedCalls_CountAgainstTheTenant_OnlyOnTheirRoute_UntilTheyEnd` and `InProgressPlusAbandonedOnARoute_NeverExceedMaxConcurrentRequests` | Yes: N7a (check removed) fails 3 tests, N7b (abandonment not reported to the lease) fails 1, N7c (count never decremented) fails 2 |

**Build and tests on `16b2d7f`:** `dotnet build AiCore.slnx` → `0 Warning(s) 0 Error(s)`;
`dotnet test AiCore.slnx` →

```
Passed!  - Failed:     0, Passed:   165, Skipped:     0, Total:   165, Duration: 2 s - AiCore.Tests.dll (net10.0)
```

165 = the 162 from §8 plus 3 new tests (1 integration, 2 unit). The suite was then run 10 more times in a row
(10/10 green), and a fresh clone of `16b2d7f` built with 0 warnings and passed 165/165.

**Full mutation run on `16b2d7f`:** 28 mutations (previously misreported as 29; see §12), all detected except R9 (the efficiency-only gate fast path,
as before). New relative to §8: N7a 3, N7b 1 and N7c 2 failures. Counts that grew because the new test also
catches them: M02 28, M03 13, M04 24, M08 15, R1b 4, N1 3. The others are as in §8. The source was restored after
each mutation, and the final suite passed 165/165 with a clean `git status`.

**Manual run against Kestrel (`16b2d7f` build).** The reviewer's scenario, with the default limits
(`MaxConcurrentRequests` 4, `MaxAbandonedCallsPerRoute` 8, 1 s grace), `ASPNETCORE_ENVIRONMENT=Development`,
and `mock-unresponsive-v1` finishing after 20 s:

| Step | Request | Result |
| --- | --- | --- |
| 1 | tenant-a: 4 parallel `sim-unresponsive` calls, client gives up after 0.3 s | curl exit 28; after the 1 s grace, 4 calls abandoned (route count 1→4) |
| 2 | tenant-a: 4 more, same client timeout | **4 × 503** immediately (curl exit 0): refused on this route for tenant-a only |
| 3 | tenant-b: `sim-unresponsive`, client gives up after 1 s | reached the route (curl exit 28 after 1 s); abandoned as the 5th call. The route was **not** closed for other tenants |
| 4 | tenant-a: `chat-default` | 200: not locked out of other routes |

The server log has 5 "the call is abandoned" Error lines (route count 1 to 5, limit 8) and **0** "reached its
limit" Warnings. The prompt marker and API keys appear 0 times.

### 10. Self-review follow-up (1 November 2026)

A further review of the whole PR at `df2d570` (no new external review had arrived) found 8 issues. Each
correctness issue that could be reproduced deterministically was reproduced by a test on `df2d570` before it was
fixed. Fixes are in `0335b26`.

| # | Finding | Reproduced on `df2d570` | Fix | Test | Fails with fix reverted |
| --- | --- | --- | --- | --- | --- |
| S1 | A request whose client had already gone could still reach the provider: the gate's fast path ignores the client token | Yes: `RecordingProvider` received the request (`Assert.Empty() Failure`) | Check the client token before marking the provider call started (token refunded); the invoker also checks on entry | `GenerationServiceTests.ClientAlreadyGone_NeverReachesTheProvider_AndGetsItsTokenBack` | Yes (S1) |
| S4 | A retry could start after the deadline or a client abort (backoff finishing in the same instant), and retries skipped the route's abandoned-call limit | Yes for the route limit: the retry was sent and succeeded (`Expected: TransientFailure, Actual: Succeeded`). The deadline race is not deterministically reproducible and is fixed by the same check. | Before attempt ≥ 2: refuse if the token is cancelled or the route is at its limit. Every earlier attempt was "not processed", so the result is a safe 503. | `ProviderInvokerTests.Retry_IsNotSentToARouteThatReachedItsAbandonedCallLimit` | Yes (S4) |
| S2 | Abandoned-call refusals used the detail "The model provider reported a transient failure", although nothing was sent | Yes: detail assertion failed | `ApiProblem.ProviderPaused`: same code (`provider_unavailable`), detail says no request was sent | assertions in `T05_TooManyAbandonedCalls_…` and `T05_OneTenantAbortingItsOwnCalls_…` | Yes (S2, 2 tests) |
| S3 | A tenant refused on a route because of its own abandoned calls was visible only at Debug | Assertion added together with S2 | `TenantQuotaService` logs reaching (Warning) and leaving (Information) a tenant's share of a route, once each, without the tenant ID | assertion in `T05_OneTenantAbortingItsOwnCalls_…` | Yes (S3) |
| — | Design notes §2 diagram omitted the per-tenant abandoned-call check | n/a (docs) | Diagram updated, plus the client-gone and retry checks | n/a | n/a |
| — | Simplification: the route key was rebuilt several times per request; the `AbandonedCalls` `Retry-After` was unused | n/a | Invoker API takes the route key; the refusal uses the admission's `Retry-After` | existing tests | n/a |
| — | Startup warning for `MaxConcurrentRequests` ≥ `MaxAbandonedCallsPerRoute` was untested | n/a (coverage) | Test added | `StartupWarns_WhenOneTenantCouldReachARoutesAbandonedCallLimitAlone` (8 → warns, 4 → doesn't) | n/a |
| — | Altitude: abandoned-call accounting is split across `ProviderInvoker`, `TenantQuotaService` and `GenerationService` | n/a | **Not changed.** Consolidating into one tracker is a reasonable refactor, but it is design consolidation, not a defect. It would touch every abandonment path that three review rounds have just verified, so it is recorded as a follow-up rather than done late in this PR. | n/a | n/a |

**Build and tests on `0335b26`:** `dotnet build AiCore.slnx` → `0 Warning(s) 0 Error(s)`;
`dotnet test AiCore.slnx` → `Passed!  - Failed: 0, Passed: 169, Skipped: 0, Total: 169`. 169 = the 165 from §9
plus 4 new tests (one of them a theory with 2 cases). The suite was then run 10 more times in a row (10/10 green),
and a fresh clone of `0335b26` built with 0 warnings and passed 169/169.

**Full mutation run on `0335b26`:** 32 mutations (the 28 from §9 plus S1–S4; previously misreported as 33), all detected except R9 (efficiency
only). Counts for the new ones: S1 1, S2 2, S3 1, S4 1 failing test. Mutation targets M08, R1b, N3 and N7c were
updated to the changed source lines. The source was restored after each mutation, and the final suite passed 169/169.

**Manual run against Kestrel (`0335b26` build):** Development environment, `MaxConcurrentRequests=2`,
`mock-unresponsive-v1` latency 20 s. Tenant A sent 2 `sim-unresponsive` calls and disconnected after 0.3 s; both
were abandoned after the 1 s grace. A's next call got `503`, `Retry-After: 1`, detail "Calls to this model are
paused because earlier calls to it have not finished; no request was sent to the provider. Retry later." The log
showed one `TenantQuotaService` Warning ("A tenant's abandoned calls on route mock/mock-unresponsive-v1 reached its
MaxConcurrentRequests (2) …"). The prompt marker and API keys appeared 0 times.

### 11. Fourth review follow-up (1 November 2026)

The fourth review (of `e6d1ad9`) built and tested the PR locally (169/169), confirmed the N7 fix and resolved every
thread. It raised one small follow-up and supplied the fix as commit `a26f5e4` on `claude/jolly-dijkstra-syh2me`.
That commit's parent is `e6d1ad9`, so the PR branch was fast-forwarded to it unchanged.

| Finding | Fix (`a26f5e4`, by the reviewer) | Test | Fails with fix reverted |
| --- | --- | --- | --- |
| An abandoned call was added to its tenant's route count when the invoker abandoned it, but the tenant's slot was freed only later, when the lease was disposed. In between it counted twice (in `Active` and in `AbandonedOn(route)`), so `TryAdmit` could refuse the tenant on that route one call early, and the documented invariant ("without raising the sum") didn't hold during that window. | `CountAbandonedCall` frees the slot and raises the route count in one locked step (`MoveToAbandoned`); `Dispose` then has nothing left to release. No token is refunded, because the provider was called. | `TenantQuotaServiceTests.Abandonment_MovesTheRequestToTheRouteCount_WithoutCountingItTwice` (reviewer reports it fails on `e6d1ad9` with ActiveRequests 1, expected 0) | Yes: X1 (`releaseSlot = false`, restoring release-at-dispose) fails it |

I reviewed the commit before taking it. The slot is released exactly once (the shared `Interlocked.Exchange` on
`_released` makes `Dispose` a no-op afterwards). The abandonment path only runs after `MarkProviderCallStarted`, so
skipping the refund is correct. The route-count decrement and its logging are unchanged.

**Build and tests on `a26f5e4`:** `dotnet build AiCore.slnx` → `0 Warning(s) 0 Error(s)`;
`dotnet test AiCore.slnx` → `Passed!  - Failed: 0, Passed: 170, Skipped: 0, Total: 170` (169 + the new test).
10 more runs in a row were all green, and a fresh clone of `a26f5e4` built with 0 warnings and passed 170/170.

**Full mutation run on `a26f5e4`:** 33 mutations (the 32 from §10 plus X1; previously misreported as 34), all detected except R9 (efficiency
only). M02's target was narrowed to the dispose path, because `MoveToAbandoned` now also decrements `Active`.
The source was restored after each mutation, and the final suite passed 170/170.

### 12. Reproducible mutation check (1 November 2026)

The mutation runs in §4 and §7–§11 used a script that lived outside the repository, so the evidence couldn't be
re-run from a clone. It is now committed as `tools/mutation_check.py`. It holds the current list of mutations
(M01–M13, then the review reverts R*, N*, S* and X1, with the IDs used in the tables above) and, for each one:

1. checks first that the target text still occurs exactly once, and stops with exit code 2 if not;
2. builds and tests the unmodified source, which must be green;
3. injects the mutation, rebuilds, runs the suite and restores the source, also on Ctrl-C;
4. at the end, rebuilds the restored source.

It exits with code 1 if any mutation behaves unexpectedly. R9 (removing the gate's fast path) is marked as
expected to survive, because it changes efficiency, not behaviour.

```bash
python3 tools/mutation_check.py
```

Output on `65a59d3` (the code of `a26f5e4`), run from `/tmp` to confirm the script doesn't depend on the working
directory; the indented lines naming failing tests are omitted:

```
Baseline: 170 passed, 0 failed.

M01  quota token never refunded: detected, 4 failing
M02  tenant concurrency slot never released: detected, 25 failing
M03  all tenants share one quota bucket: detected, 14 failing
M04  quota lease not disposed by GenerationService: detected, 22 failing
M05  no provider deadline (timeout never fires): detected, 15 failing
M06  ambiguous transient failures are retried: detected, 1 failing
M07  permanent failures are retried: detected, 4 failing
M08  permit released without waiting for the provider to stop: detected, 15 failing
M09  timeout reported as transient 503 instead of 504: detected, 7 failing
M10  queue wait not bounded in time: detected, 1 failing
M11  configuration validator not registered: detected, 27 failing
M12  lenient binding (unknown keys ignored): detected, 1 failing
M13  provider-call start never marked (every token refunded): detected, 5 failing
R1b  no abandoned-call limit: detected, 4 failing
R2   attempt started inline (no Task.Run): detected, 17 failing
R4   success during the grace period reported as timeout: detected, 1 failing
R4b  not-processed failure during the grace period reported as timeout: detected, 1 failing
R7   concurrency Retry-After ignores an empty rate bucket: detected, 1 failing
R8   undefined enum value not validated: detected, 1 failing
R9   gate fast path removed (efficiency only: no behaviour change): NOT detected
N1   abandoned-call limit keyed per provider, not per route: detected, 3 failing
N3   no re-check of the route limit after acquiring a permit: detected, 1 failing
N4   permanent failure during the grace period reported as timeout: detected, 1 failing
N5   refused requests logged at Warning: detected, 1 failing
N6   gate rejection counter not incremented: detected, 1 failing
N7a  tenant's abandoned calls on a route not checked: detected, 3 failing
N7b  abandoned calls not reported to the tenant lease: detected, 1 failing
N7c  tenant's abandoned count never decremented: detected, 3 failing
S1   client-gone check before the provider call removed: detected, 1 failing
S2   local refusals use the provider-failure detail: detected, 2 failing
S3   tenant-share transition not logged: detected, 1 failing
S4   retry sent to a route at its abandoned-call limit: detected, 1 failing
X1   abandoned call keeps its slot until dispose (counted twice): detected, 1 failing

33 mutations: 32 detected, 1 not detected, 0 unexpected.
```

Exit code 0, and `git status` was clean afterwards apart from the new script.

**Hangs and crashed runs (after review of #4).** A test run that exceeds the timeout (900 s;
`MUTATION_TEST_TIMEOUT_SECONDS` overrides it) has its whole process tree killed and counts as detected, because
a hang is a failure the defect caused. A run that ends without a usable summary, such as a crashed test host, is
reported as `TEST RUN FAILED` with the end of its output, and always counts as unexpected (exit code 1), because it
says nothing about the tests. Both paths were exercised:

- **Timeout:** M06 with `MUTATION_TEST_TIMEOUT_SECONDS=5` gave `detected (test run hung past 5 s)`, and no
  `testhost` or `vstest` process was left running, immediately or 5 s later.
- **Crash:** a throwaway mutation calling `Environment.FailFast` gave `TEST RUN FAILED (no usable test summary)
  <-- unexpected`, with the stack tail ending in `Test Run Aborted.`, and exit code 1.

A full re-run afterwards gave the same verdicts for all 33 mutations: 32 detected, R9 not, 0 unexpected, exit
code 0. Failing-test counts can differ by one between runs for R4b, because one of the two tests that can catch it
depends on timing (§8). Its verdict rests on the deterministic `NotProcessedFailureDuringGrace_…` test.

**Runner cleanup and restoration checks.** Interrupted builds and test runs now kill and reap their process
tree before restoring the source. Restoration preserves the original file bytes, including line endings,
and also runs when the mutation write is interrupted. A failed final rebuild prints its diagnostics and
returns a failure instead of silently leaving mutated binaries behind. The runner's regression checks use
temporary source files and simulated build/test outcomes, without changing the API source:

```bash
python3 -B -m unittest discover -s tools -v
```

**Correction.** Committing the list exposed a counting error in this report. From §8 on, the mutation totals were
one too high (26, 29, 33 and 34 instead of 25, 28, 32 and 33). When R1a ("abandoned call releases its tenant slot
immediately") was retired after review round 2, because that behaviour became the design, the running total wasn't
reduced. Every per-mutation result reported was real; only the totals were wrong. §8–§11 now give the correct
numbers.

Not in the script: R1a, retired in round 2 because its behaviour became the design. Also round-1 findings 5 (float
drift in the bucket) and 6 (alias location), which were reproduced against the old code rather than as a one-line
mutation (§7), and the review findings fixed only by adding tests or docs.

