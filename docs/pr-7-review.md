# PR #7 iterative review

Scope: changes introduced by M3 relative to `7ba0640`, including their interaction with existing admission,
provider execution, error handling, tests and documentation. Review and fixes are by the implementation
agent; this is not an independent reviewer sign-off or proof that the system has no possible bugs.

Stopping criterion requested by the user: two consecutive review passes with no new actionable PR bugs.
A pass finding a bug resets the count. Existing enterprise-scale design limitations remain documented in
[architecture-review.md](architecture-review.md); they are not silently treated as implemented features.

## Pass 1 — Failure isolation and HTTP completion

Reviewed synchronous instrumentation callbacks, permit ownership, response completion and exception
middleware ordering. Added focused tests before changing implementation.

Findings (fixed):

- A metric listener throwing after gate acquisition leaked the acquired permit. Throwing measurement or
  activity callbacks at request/queue/provider boundaries could fail or truncate a successful request.
- A completion logger exception could escape after a successful response.
- An exception escaping the pipeline was reported as `success` with the default 200 status.
- The exception handler cleared routing metadata, changing a known endpoint's completion label to `unmatched`.

Before fixes, the focused resilience/privacy run had **16 failures, 4 passes**. The queue-listener regression
observed a lost permit; the callback matrix also covered sampler, start and stop failures for all three
activity names. Fixes contain emissions at telemetry boundaries, restore activity parents, retain a local
failure count, capture endpoint metadata before invoking downstream middleware, and classify escaped errors.
The full suite after these fixes passed **200/200**. Clean-pass count: **0**.

## Pass 2 — Cancellation and trace lifetime

Reviewed the fix, parent activity restoration, status after response headers, cancellation classification,
privacy boundaries and documented usage semantics. GitHub had no posted review or comment findings.

Found one additional bug: a disconnected response write throwing `IOException` produced `Result=cancelled`
but set the span to Error. The new focused run had **1 failure, 18 passes**. Fixed error-status assignment
to exclude cancellation. Additional passing cases verify parent restoration after start/stop exceptions
and preservation of wire status plus an error span for a genuine failure after headers.
Clarified that usage measures provider results available to the endpoint even if later response transfer
fails; it is not confirmation of client receipt. Clean-pass count: **0**.

The full Release suite after the second fix passed **204/204**.

## Pass 3 — Corrected-code review

Reviewed the corrected telemetry code and its call sites again: acquisition-to-disposal ordering, all
activity lifecycle callbacks, the no-listener path, non-recursive emission failure accounting, endpoint
capture, ErrorCode propagation, partial responses and cancellation precedence. Rechecked the strict parser,
provider result mapping and test log capture against the privacy claims. The full Release suite passed
**204/204**, including the original admission/retry/timeout tests and the new adversarial tests.

No new actionable PR bugs found. Clean-pass count: **1**.

## Pass 4 — Full PR and delivery review

Reviewed the complete M3 diff against the base, including DI/middleware order, quota and invoker call sites,
privacy changes, configuration boundaries and isolated test-host state. Rechecked documentation against
instrument names, units, emitted fields and failure semantics; clarified the usage table wording for late
response-transfer failures. Checked all 33 existing mutation anchors and all local Markdown file links;
both checks passed, as did `git diff --check`. No extra production capability or independent sign-off is
claimed. No new actionable implementation bugs found. Clean-pass count: **2**.

GitHub's Ubuntu `build-and-test` check passed for implementation commit `77e8d0c`:
CI run in the private development repository (historical run ID `41364372913`, job `125021303710`).
No implementation changes were made between the two clean passes. The final follow-up commit only records
the review and verification evidence and clarifies documentation.

## Verification and stopping result

```text
dotnet test AiCore.slnx --no-restore --disable-build-servers --configuration Release

Passed!  - Failed:     0, Passed:   204, Skipped:     0, Total:   204, Duration: 1 s - AiCore.Tests.dll (net10.0)
```

The test command built the Release solution successfully before running the tests. Nineteen new cases in
`TelemetryResilienceTests` and a stronger existing privacy assertion cover the review findings. The full
mutation campaign was not rerun; source-anchor compatibility was checked without mutating the source.

Four review passes completed: two with fixes, followed by two without new actionable implementation bugs.
The requested stopping criterion is met within the PR scope. Existing documented production design risks
and independent human/second-agent architecture sign-off remain separate; this review does not assert
that the service is production-ready or mathematically bug-free.

Direct local test process IDs were recorded: 38568 (pass-1 reproductions), 30172 (200-test corrected suite),
19368 (pass-2 reproduction) and 45356 (204-test Release suite). They exited normally; no pre-existing user
process was stopped or restarted. Shell, Git and API-helper process IDs are in the execution transcript.
