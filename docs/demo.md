# A 5–10 minute local demo

This walkthrough demonstrates model-invocation infrastructure using a deterministic mock. It needs
.NET SDK 10, two terminals, and an initial NuGet restore. Use PowerShell 7 with `curl.exe` on Windows,
or Bash with `curl`. No model account, cloud deployment or real credentials are required.

## 1. Build and start (before the presentation)

Run from the repository root:

```text
dotnet build AiCore.slnx --configuration Release
dotnet test AiCore.slnx --no-build --configuration Release
```

Expected: a successful build and 216 passing tests, none skipped. Allow restore time before presenting.
In terminal A, use **one** of the following setup blocks. These are disposable local keys.

PowerShell:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:AiCore__Tenants__0__TenantId = 'tenant-a'
$env:AiCore__Tenants__0__ApiKey = 'demo-key-a-local-only'
$env:AiCore__Tenants__1__TenantId = 'tenant-b'
$env:AiCore__Tenants__1__ApiKey = 'demo-key-b-local-only'
$env:AiCore__Resilience__ProviderTimeoutMs = '2000'
dotnet run --project src/AiCore.Api --configuration Release --no-build --no-launch-profile --urls http://127.0.0.1:5297
```

Bash:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export AiCore__Tenants__0__TenantId=tenant-a
export AiCore__Tenants__0__ApiKey=demo-key-a-local-only
export AiCore__Tenants__1__TenantId=tenant-b
export AiCore__Tenants__1__ApiKey=demo-key-b-local-only
export AiCore__Resilience__ProviderTimeoutMs=2000
dotnet run --project src/AiCore.Api --configuration Release --no-build --no-launch-profile --urls http://127.0.0.1:5297
```

The explicit environment enables the timeout simulator; the short deadline keeps the demo brief.
The server stays in the foreground. Stop this instance with Ctrl+C when finished.

## 2. Success and the trust boundary (2 minutes)

In terminal B define a request helper for your shell. Piping JSON avoids shell-specific quote escaping.

PowerShell:

```powershell
function Send-DemoRequest([string]$Model, [string]$Key = 'demo-key-a-local-only') {
    @{ model = $Model; prompt = 'demo-sensitive-marker' } | ConvertTo-Json -Compress |
        curl.exe -sS -i http://127.0.0.1:5297/v1/generate -H "X-Api-Key: $Key" -H 'Content-Type: application/json' --data-binary '@-'
}
curl.exe -sS -i http://127.0.0.1:5297/health/live
Send-DemoRequest 'chat-default'
Send-DemoRequest 'chat-default' 'invalid-demo-key'
```

Bash:

```bash
send_demo_request() {
  printf '{"model":"%s","prompt":"demo-sensitive-marker"}' "$1" |
    curl -sS -i http://127.0.0.1:5297/v1/generate \
      -H "X-Api-Key: ${2:-demo-key-a-local-only}" -H 'Content-Type: application/json' --data-binary @-
}
curl -sS -i http://127.0.0.1:5297/health/live
send_demo_request chat-default
send_demo_request chat-default invalid-demo-key
```

Expected: `200 Healthy`, a `200` completion containing `usage.simulated: true`, then
`401 unauthenticated`. The server derives the tenant from its key mapping; the caller cannot choose
a tenant in the JSON. Point out the correlation ID and logical model alias. The output is a simulation,
not an LLM answer. These helpers accept the fixed demo aliases, not arbitrary untrusted input.

## 3. Retry, failure and timeout (2 minutes)

PowerShell:

```powershell
Send-DemoRequest 'sim-throttled-once'
Send-DemoRequest 'sim-transient-failure'
Send-DemoRequest 'sim-permanent-failure'
Send-DemoRequest 'sim-timeout'
Send-DemoRequest 'chat-default' 'demo-key-b-local-only'
```

Bash:

```bash
send_demo_request sim-throttled-once
send_demo_request sim-transient-failure
send_demo_request sim-permanent-failure
send_demo_request sim-timeout
send_demo_request chat-default demo-key-b-local-only
```

| Call | Expected result | Explain |
| --- | --- | --- |
| Throttled once | 200; log shows one retry | Retry is allowed only when the provider says the request was not processed |
| Ambiguous transient failure | 503 `provider_unavailable`; no retry | Retrying ambiguous inference can duplicate generation and billing |
| Permanent failure | 502 `provider_error` | A permanent failure is not retried |
| Timeout | 504 `provider_timeout`, around 2 seconds | Deadline cancels cooperative work and capacity recovers |
| Tenant B success | 200 | Service remains usable after the failures |

In terminal A, show a JSON completion record with correlation ID, pseudonymous tenant, result and timing.
The prompt marker and API key must not appear in application telemetry. Traces/metrics have no external
exporter in this demo; their emission and redaction are exercised by listener-based tests below.

## 4. Tenant isolation and bounded concurrency (2 minutes)

Run this in terminal B; it uses separate in-memory test hosts and does not affect terminal A:

```text
dotnet test AiCore.slnx --no-build --configuration Release --filter "FullyQualifiedName~TenantQuotaTests|FullyQualifiedName~ConcurrencyTests|FullyQualifiedName~TelemetryTests|FullyQualifiedName~PrivacyRegressionTests"
```

Explain the tests: exhaust tenant A's quota and observe 429 while B succeeds; fill permits and queue,
observe 503 for overflow, release work and verify recovery. The 100-request synthetic scenario verifies
the cooperative concurrency bound. It is not a throughput benchmark. Privacy tests inspect structured
logs, traces and metric dimensions rather than relying on a visual log inspection alone.

## 5. Architecture discussion (1–2 minutes)

Open the [architecture diagram](architecture.md), then [ADR 003](decisions/003-backpressure.md) and
[ADR 004](decisions/004-retry-safety.md). Be ready to explain:

- Tenant quotas and global capacity serve different purposes; 429 and 503 distinguish them.
- Limits are per process. Replicas and restarts change enforcement; distributed admission is future work.
- Ignored cancellation creates an availability/resource-bound trade-off. Abandoned calls can continue
  after a permit is released, with separate accounting. Worker isolation is not implemented.
- Production identity, model entitlements, real-provider behavior and telemetry export need further work.

Independent architecture review is still pending. The [assessment](architecture-review.md) records open
risks; the demo does not establish production readiness.

## Troubleshooting and cleanup

| Symptom | Action |
| --- | --- |
| SDK/restore failure | Install a compatible .NET 10 SDK and complete restore before presenting |
| Port 5297 already in use | Choose another unused port in both server and request commands; do not stop an unrelated process |
| All requests return 401 | Set the tenant variables in the terminal that starts the server, then restart your own instance |
| `unknown_model` for `sim-timeout` | Check the explicit Development environment and `--no-launch-profile` |
| Timeout takes 30 seconds | Check the 2000 ms timeout override in terminal A and restart your own instance |
| 429 after repeated rehearsals | Wait for refill or restart your own demo instance to reset in-memory quotas |
| PowerShell invokes an alias for curl | Use `curl.exe` exactly as shown |

After Ctrl+C, close the demo terminals to discard their process-local environment variables.
