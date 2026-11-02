#!/usr/bin/env python3
"""Mutation check for the M2 reliability code.

Injects one known defect at a time into src/AiCore.Api, rebuilds, runs the test suite and records whether the
tests detect it (at least one test fails). The source is restored after every mutation, including on Ctrl-C.
These are the mutation tables in docs/verification.md (M01-M13 from M2, R/N/S/X from the review rounds).

Usage (from anywhere; needs Python 3.8+ and the .NET SDK):
    python3 tools/mutation_check.py            # run all mutations (about 15 minutes)
    python3 tools/mutation_check.py M05 N7     # run the mutations whose ID starts with M05 or N7
    python3 tools/mutation_check.py --list     # list the mutations

A mutation counts as detected when at least one test fails, or when the test run hangs past the timeout (the
whole process tree is then killed). A build failure, or a test run that ends without a summary (for example a
crashed test host), is reported on its own and always counts as unexpected: it says nothing about the tests.

Exit code 0 when every mutation behaves as expected: detected, or (for the few marked expected_detected=False,
such as a pure efficiency change) not detected. Exit code 1 otherwise, or 2 if a mutation's target text is no
longer in the source (the code changed; update the entry). Don't run it while editing src/AiCore.Api: each file
is rewritten temporarily.

Environment: MUTATION_TEST_TIMEOUT_SECONDS overrides the per-run test timeout (default 900).
"""

import os
import pathlib
import re
import signal
import subprocess
import sys
from typing import List, NamedTuple

ROOT = pathlib.Path(__file__).resolve().parent.parent
API = ROOT / "src" / "AiCore.Api"
SOLUTION = "AiCore.slnx"
TEST_TIMEOUT_SECONDS = int(os.environ.get("MUTATION_TEST_TIMEOUT_SECONDS", "900"))


class Mutation(NamedTuple):
    id: str
    description: str
    file: str  # relative to src/AiCore.Api
    original: str  # must occur exactly once in the file
    mutated: str
    expected_detected: bool = True


MUTATIONS = [
    # --- M2 (docs/verification.md M2 section 4)
    Mutation("M01", "quota token never refunded", "Execution/TenantQuotaService.cs",
             "_owner.Release(_bucket, _limits, refundToken: !_providerCallStarted);",
             "_owner.Release(_bucket, _limits, refundToken: false);"),
    Mutation("M02", "tenant concurrency slot never released", "Execution/TenantQuotaService.cs",
             "            bucket.Active--;\n            if (refundToken)",
             "            if (refundToken)"),
    Mutation("M03", "all tenants share one quota bucket", "Execution/TenantQuotaService.cs",
             "var bucket = _buckets.GetOrAdd(tenantId,",
             "var bucket = _buckets.GetOrAdd(\"shared\","),
    Mutation("M04", "quota lease not disposed by GenerationService", "Execution/GenerationService.cs",
             "using var tenantLease = admission.Lease!;",
             "var tenantLease = admission.Lease!;"),
    Mutation("M05", "no provider deadline (timeout never fires)", "Execution/ProviderInvoker.cs",
             "using var deadline = new CancellationTokenSource(_timeout, timeProvider);",
             "using var deadline = new CancellationTokenSource();"),
    Mutation("M06", "ambiguous transient failures are retried", "Execution/ProviderInvoker.cs",
             "when (ex.RequestNotProcessed && attempt < options.MaxAttempts)",
             "when (attempt < options.MaxAttempts)"),
    Mutation("M07", "permanent failures are retried", "Execution/ProviderInvoker.cs",
             "catch (ProviderPermanentException ex)\n            {\n                return",
             "catch (ProviderPermanentException ex) when (attempt >= options.MaxAttempts)\n            {\n                return"),
    Mutation("M08", "permit released without waiting for the provider to stop", "Execution/ProviderInvoker.cs",
             "var stopped = await LetCallStopAsync(call, provider, attemptRequest, onAbandoned);",
             "var stopped = true;"),
    Mutation("M09", "timeout reported as transient 503 instead of 504", "Execution/GenerationService.cs",
             "return GenerationOutcome.Fail(ApiProblem.ProviderTimeout());",
             "return GenerationOutcome.Fail(ApiProblem.ProviderUnavailable());"),
    Mutation("M10", "queue wait not bounded in time", "Execution/ProviderConcurrencyGate.cs",
             "using var queueTimeout = new CancellationTokenSource(_maxQueueWait, _timeProvider);",
             "using var queueTimeout = new CancellationTokenSource();"),
    Mutation("M11", "configuration validator not registered", "Program.cs",
             "builder.Services.AddSingleton<IValidateOptions<AiCoreOptions>, AiCoreOptionsValidator>();",
             ""),
    Mutation("M12", "lenient binding (unknown keys ignored)", "Program.cs",
             "binder => binder.ErrorOnUnknownConfiguration = true",
             "binder => binder.ErrorOnUnknownConfiguration = false"),
    Mutation("M13", "provider-call start never marked (every token refunded)", "Execution/GenerationService.cs",
             "        tenantLease.MarkProviderCallStarted();\n",
             "\n"),
    # --- Review round 1 reverts (section 7)
    Mutation("R1b", "no abandoned-call limit", "Execution/GenerationService.cs",
             "if (!invoker.HasTooManyAbandonedCalls(routeKey))",
             "if (true)"),
    Mutation("R2", "attempt started inline (no Task.Run)", "Execution/ProviderInvoker.cs",
             "Task.Run(() => provider.GenerateAsync(request, token), CancellationToken.None);",
             "provider.GenerateAsync(request, token);"),
    Mutation("R4", "success during the grace period reported as timeout", "Execution/ProviderInvoker.cs",
             "if (stopped && call.IsCompletedSuccessfully)",
             "if (false)"),
    Mutation("R4b", "not-processed failure during the grace period reported as timeout", "Execution/ProviderInvoker.cs",
             "if (stopped && call.Exception?.InnerException is ProviderTransientException { RequestNotProcessed: true } notProcessed)",
             "if (false && call.Exception?.InnerException is ProviderTransientException { RequestNotProcessed: true } notProcessed)"),
    Mutation("R7", "concurrency Retry-After ignores an empty rate bucket", "Execution/TenantQuotaService.cs",
             "var retryAfter = untilNextToken > ConcurrencyRetryAfter ? untilNextToken : ConcurrencyRetryAfter;",
             "var retryAfter = ConcurrencyRetryAfter;"),
    Mutation("R8", "undefined enum value not validated", "Configuration/AiCoreOptionsValidator.cs",
             "if (!Enum.IsDefined(mock.Behavior))",
             "if (false)"),
    Mutation("R9", "gate fast path removed (efficiency only: no behaviour change)", "Execution/ProviderConcurrencyGate.cs",
             "        if (immediate.IsAcquired)\n        {\n            return new GateAcquisition(immediate, QueueWaitTimedOut: false);\n        }\n",
             "        if (false)\n        {\n            return new GateAcquisition(immediate, QueueWaitTimedOut: false);\n        }\n",
             expected_detected=False),
    # --- Review round 2 reverts (section 8)
    Mutation("N1", "abandoned-call limit keyed per provider, not per route", "Execution/ProviderRoutes.cs",
             'public static string Key(string providerName, string providerModel) => $"{providerName}/{providerModel}";',
             "public static string Key(string providerName, string providerModel) => providerName;"),
    Mutation("N3", "no re-check of the route limit after acquiring a permit", "Execution/GenerationService.cs",
             "        // provider is not called, so the permit is returned and the tenant's token refunded.\n        if (RouteIsRefused(routeKey, command))",
             "        // provider is not called, so the permit is returned and the tenant's token refunded.\n        if (false)"),
    Mutation("N4", "permanent failure during the grace period reported as timeout", "Execution/ProviderInvoker.cs",
             "if (stopped && call.Exception?.InnerException is ProviderPermanentException permanent)",
             "if (false && call.Exception?.InnerException is ProviderPermanentException permanent)"),
    Mutation("N5", "refused requests logged at Warning", "Execution/GenerationService.cs",
             "        logger.LogDebug(\n            \"Refusing alias",
             "        logger.LogWarning(\n            \"Refusing alias"),
    Mutation("N6", "gate rejection counter not incremented", "Execution/ProviderConcurrencyGate.cs",
             "            Interlocked.Increment(ref _queueFullRejections);\n",
             ""),
    # --- Review round 3 reverts (section 9)
    Mutation("N7a", "tenant's abandoned calls on a route not checked", "Execution/TenantQuotaService.cs",
             "if (route is not null && bucket.Active + bucket.AbandonedOn(route) >= limits.MaxConcurrentRequests)",
             "if (false && bucket.Active + bucket.AbandonedOn(route) >= limits.MaxConcurrentRequests)"),
    Mutation("N7b", "abandoned calls not reported to the tenant lease", "Execution/GenerationService.cs",
             "            onAbandoned: tenantLease.CountAbandonedCall);",
             "            onAbandoned: null);"),
    Mutation("N7c", "tenant's abandoned count never decremented", "Execution/TenantQuotaService.cs",
             "                lease._owner.RemoveAbandoned(lease._bucket, lease._limits, lease._route!);",
             "                GC.KeepAlive(lease);"),
    # --- Self-review reverts (section 10)
    Mutation("S1", "client-gone check before the provider call removed", "Execution/GenerationService.cs",
             "        cancellationToken.ThrowIfCancellationRequested();\n\n        tenantLease.MarkProviderCallStarted();",
             "        tenantLease.MarkProviderCallStarted();"),
    Mutation("S2", "local refusals use the provider-failure detail", "Errors/ApiProblem.cs",
             '"Calls to this model are paused because earlier calls to it have not finished; no request was sent to the provider. Retry later."',
             '"The model provider reported a transient failure. Retry later."'),
    Mutation("S3", "tenant-share transition not logged", "Execution/TenantQuotaService.cs",
             "        if (count == limits.MaxConcurrentRequests)\n",
             "        if (false)\n"),
    Mutation("S4", "retry sent to a route at its abandoned-call limit", "Execution/ProviderInvoker.cs",
             "if (attempt > 1 && (token.IsCancellationRequested || HasTooManyAbandonedCalls(route)))",
             "if (attempt > 1 && token.IsCancellationRequested)"),
    # --- Review round 4 revert (section 11)
    Mutation("X1", "abandoned call keeps its slot until dispose (counted twice)", "Execution/TenantQuotaService.cs",
             "        var releaseSlot = Interlocked.Exchange(ref _released, 1) == 0;",
             "        var releaseSlot = false;"),
]


# Kinds of test run.
TESTED = "tested"            # a summary line was printed
TIMED_OUT = "timed out"      # killed after TEST_TIMEOUT_SECONDS: a hang, counted as detected
RUN_FAILED = "run failed"    # no summary (e.g. the test host crashed): says nothing about the tests


class TestRun(NamedTuple):
    kind: str
    failed: int = 0
    passed: int = 0
    failing_names: List[str] = []
    output_tail: str = ""


class Outcome(NamedTuple):
    mutation: Mutation
    build_ok: bool
    run: TestRun

    @property
    def detected(self) -> bool:
        return self.build_ok and (self.run.kind == TIMED_OUT or (self.run.kind == TESTED and self.run.failed > 0))

    @property
    def as_expected(self) -> bool:
        # A broken build or a crashed test run is always unexpected: neither shows whether the tests catch the defect.
        return self.build_ok and self.run.kind != RUN_FAILED and self.detected == self.mutation.expected_detected


def build() -> subprocess.CompletedProcess:
    return run_command(["dotnet", "build", SOLUTION, "-nologo", "-v", "q"])


def kill_tree(process: subprocess.Popen) -> None:
    """Kills a command and its children before restoring or rebuilding the source."""
    if os.name == "posix":
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    else:
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(process.pid)], capture_output=True, check=True)


def run_command(args: List[str], timeout=None) -> subprocess.CompletedProcess:
    process = subprocess.Popen(
        args, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        start_new_session=(os.name == "posix"),
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except BaseException:
        # Includes Ctrl-C and timeouts. Reap the command before main's source restoration and rebuild.
        kill_tree(process)
        process.communicate()
        raise
    return subprocess.CompletedProcess(args, process.returncode, stdout, stderr)


def test() -> TestRun:
    try:
        result = run_command(["dotnet", "test", SOLUTION, "--no-build"], timeout=TEST_TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        return TestRun(TIMED_OUT, failing_names=[f"<test run exceeded {TEST_TIMEOUT_SECONDS} s and was killed>"])

    stdout, stderr = result.stdout, result.stderr
    tail = "\n".join((stdout + stderr).strip().splitlines()[-15:])
    summary = re.search(r"Failed:\s+(\d+), Passed:\s+(\d+)", stdout)
    if summary is None:
        return TestRun(RUN_FAILED, output_tail=tail)
    failed, passed = int(summary.group(1)), int(summary.group(2))
    if result.returncode != 0 and failed == 0:
        return TestRun(RUN_FAILED, failed, passed, output_tail=tail)  # e.g. a crash after the summary
    names = sorted(set(re.findall(r"^\s+Failed (\S+?)(?:\(| \[)", stdout, re.M)))
    return TestRun(TESTED, failed, passed, [n.replace("AiCore.Tests.", "") for n in names])


def describe(outcome: Outcome) -> str:
    if not outcome.build_ok:
        return "BUILD FAILED"
    if outcome.run.kind == RUN_FAILED:
        return "TEST RUN FAILED (no usable test summary)"
    if outcome.run.kind == TIMED_OUT:
        return f"detected (test run hung past {TEST_TIMEOUT_SECONDS} s)"
    return f"detected, {outcome.run.failed} failing" if outcome.detected else "NOT detected"


def selected(prefixes: List[str]) -> List[Mutation]:
    if not prefixes:
        return list(MUTATIONS)
    return [m for m in MUTATIONS if any(m.id.startswith(p) for p in prefixes)]


def main(argv: List[str]) -> int:
    if "--list" in argv:
        for m in MUTATIONS:
            note = "" if m.expected_detected else "  (expected to survive)"
            print(f"{m.id:4} {m.description} [{m.file}]{note}")
        return 0

    mutations = selected(argv)
    if not mutations:
        print(f"No mutation ID starts with {argv}. Use --list.")
        return 2

    stale = [m for m in mutations if (API / m.file).read_text(encoding="utf-8").count(m.original) != 1]
    if stale:
        for m in stale:
            print(f"STALE {m.id}: its target text no longer occurs exactly once in src/AiCore.Api/{m.file}")
        return 2

    print("Baseline: building and testing the unmodified source...")
    if build().returncode != 0:
        print("Baseline build failed; fix the build first.")
        return 1
    baseline = test()
    if baseline.kind != TESTED or baseline.failed != 0:
        print(f"Baseline tests are not green ({baseline.kind}, {baseline.failed} failed); fix them first.")
        if baseline.output_tail:
            print(baseline.output_tail)
        return 1
    print(f"Baseline: {baseline.passed} passed, 0 failed.\n")

    outcomes: List[Outcome] = []
    try:
        for m in mutations:
            path = API / m.file
            original_bytes = path.read_bytes()
            original_source = original_bytes.decode("utf-8").replace("\r\n", "\n")
            try:
                path.write_text(original_source.replace(m.original, m.mutated), encoding="utf-8")
                built = build().returncode == 0
                outcome = Outcome(m, built, test() if built else TestRun(RUN_FAILED))
            finally:
                path.write_bytes(original_bytes)
            outcomes.append(outcome)
            flag = "" if outcome.as_expected else "   <-- unexpected"
            print(f"{m.id:4} {m.description}: {describe(outcome)}{flag}")
            for name in outcome.run.failing_names[:3]:
                print(f"       {name}")
            if outcome.build_ok and outcome.run.kind == RUN_FAILED and outcome.run.output_tail:
                print("\n".join("       | " + line for line in outcome.run.output_tail.splitlines()))
    finally:
        restored_build = build()  # leave binaries that match the restored source
        if restored_build.returncode != 0:
            print("Restored-source build failed; binaries may still contain a mutation.")
            print(restored_build.stdout + restored_build.stderr)

    unexpected = [o for o in outcomes if not o.as_expected]
    detected = sum(o.detected for o in outcomes)
    print(f"\n{len(outcomes)} mutations: {detected} detected, {len(outcomes) - detected} not detected, "
          f"{len(unexpected)} unexpected.")
    return 1 if unexpected or restored_build.returncode != 0 else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
