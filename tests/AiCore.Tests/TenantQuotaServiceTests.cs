using AiCore.Api.Configuration;
using AiCore.Api.Execution;
using Microsoft.Extensions.Time.Testing;

namespace AiCore.Tests;

/// <summary>Unit tests of the token bucket and concurrency accounting behind FR-04, on a fake clock.</summary>
public sealed class TenantQuotaServiceTests
{
    private readonly FakeTimeProvider _time = new();

    private TenantQuotaService Create(int rpm = 60, int burst = 2, int concurrent = 10) =>
        new(new QuotaOptions
        {
            Default = new TenantQuotaOptions { RequestsPerMinute = rpm, BurstRequests = burst, MaxConcurrentRequests = concurrent },
        }, _time);

    [Fact]
    public void Bucket_StartsFull_RefillsAtTheConfiguredRate_AndNeverExceedsBurst()
    {
        var quotas = Create(rpm: 60, burst: 2);

        Spend(quotas);
        Spend(quotas);
        var rejected = quotas.TryAdmit("t");
        Assert.Equal(QuotaRejection.RequestRate, rejected.Rejection);
        Assert.Equal(TimeSpan.FromSeconds(1), rejected.RetryAfter);

        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), quotas.TryAdmit("t").RetryAfter);

        _time.Advance(TimeSpan.FromHours(1)); // idle for a long time: capped at the burst size
        Assert.Equal(2, quotas.GetSnapshot("t").AvailableRequestTokens, precision: 9);
    }

    [Fact]
    public void ConcurrencyLimit_CountsActiveLeases_AndDoesNotSpendATokenWhenRejecting()
    {
        var quotas = Create(burst: 5, concurrent: 2);

        using var first = Admit(quotas);
        using var second = Admit(quotas);
        var rejected = quotas.TryAdmit("t");

        Assert.Equal(QuotaRejection.Concurrency, rejected.Rejection);
        Assert.Equal(2, quotas.GetSnapshot("t").ActiveRequests);
        Assert.Equal(3, quotas.GetSnapshot("t").AvailableRequestTokens, precision: 9);
    }

    [Fact]
    public void Lease_RefundsItsToken_OnlyIfTheProviderWasNeverCalled()
    {
        var quotas = Create(rpm: 1, burst: 2);

        var notStarted = Admit(quotas);
        var started = Admit(quotas);
        started.MarkProviderCallStarted();
        Assert.Equal(0, quotas.GetSnapshot("t").AvailableRequestTokens, precision: 9);

        notStarted.Dispose();
        started.Dispose();
        notStarted.Dispose(); // idempotent: no double refund or double release

        var snapshot = quotas.GetSnapshot("t");
        Assert.Equal(1, snapshot.AvailableRequestTokens, precision: 9);
        Assert.Equal(0, snapshot.ActiveRequests);
    }

    [Fact]
    public void Refund_IsCappedAtBurst()
    {
        var quotas = Create(rpm: 60, burst: 1);

        var lease = Admit(quotas);
        _time.Advance(TimeSpan.FromSeconds(5)); // bucket refills to 1 while the lease is held
        lease.Dispose();

        Assert.Equal(1, quotas.GetSnapshot("t").AvailableRequestTokens, precision: 9);
    }

    [Fact]
    public void Tenants_HaveIndependentState()
    {
        var quotas = Create(burst: 1, concurrent: 1);

        using var a = Admit(quotas, "a");
        Assert.False(quotas.TryAdmit("a").IsAdmitted);

        using var b = Admit(quotas, "b");
        Assert.Equal(1, quotas.GetSnapshot("b").ActiveRequests);
    }

    [Fact]
    public void Override_ReplacesOnlyTheFieldsItSets()
    {
        var quotas = new TenantQuotaService(new QuotaOptions
        {
            Default = new TenantQuotaOptions { RequestsPerMinute = 10, BurstRequests = 5, MaxConcurrentRequests = 2 },
            Tenants = { ["vip"] = new TenantQuotaOverrideOptions { MaxConcurrentRequests = 8 } },
        }, _time);

        var vip = quotas.LimitsFor("vip");
        Assert.Equal((10, 5, 8), (vip.RequestsPerMinute, vip.BurstRequests, vip.MaxConcurrentRequests));
        Assert.Equal(2, quotas.LimitsFor("VIP").MaxConcurrentRequests); // tenant IDs are case-sensitive
    }

    [Fact]
    public async Task ConcurrentAdmissions_NeverExceedTheLimits()
    {
        var quotas = Create(rpm: 1, burst: 50, concurrent: 20);
        var admitted = new System.Collections.Concurrent.ConcurrentBag<TenantQuotaLease>();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            var admission = quotas.TryAdmit("t");
            if (admission.IsAdmitted)
            {
                admitted.Add(admission.Lease!);
            }
        })));

        Assert.Equal(20, admitted.Count);
        Assert.Equal(20, quotas.GetSnapshot("t").ActiveRequests);
        foreach (var lease in admitted)
        {
            lease.MarkProviderCallStarted();
            lease.Dispose();
        }

        Assert.Equal(0, quotas.GetSnapshot("t").ActiveRequests);
        Assert.Equal(30, quotas.GetSnapshot("t").AvailableRequestTokens, precision: 9);
    }

    [Fact]
    public void PartialRefills_AddUpToExactlyOneToken()
    {
        // 60/min = one token per second. Rejected attempts at 0.7 s and 0.9 s each trigger a partial refill;
        // at 1.0 s exactly one whole token must be available (no floating-point shortfall).
        var quotas = Create(rpm: 60, burst: 1);
        Spend(quotas);

        _time.Advance(TimeSpan.FromMilliseconds(700));
        Assert.Equal(TimeSpan.FromMilliseconds(300), quotas.TryAdmit("t").RetryAfter);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(TimeSpan.FromMilliseconds(100), quotas.TryAdmit("t").RetryAfter);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(quotas.TryAdmit("t").IsAdmitted);
    }

    [Fact]
    public void ConcurrencyRejection_RetryAfter_AlsoCoversAnEmptyRateBucket()
    {
        var quotas = Create(rpm: 1, burst: 1, concurrent: 1);
        using var holding = Admit(quotas); // uses the only slot and the only token

        var rejected = quotas.TryAdmit("t");

        Assert.Equal(QuotaRejection.Concurrency, rejected.Rejection);
        Assert.Equal(TimeSpan.FromMinutes(1), rejected.RetryAfter); // not 1 s: no token for another minute
    }

    [Fact]
    public void ConcurrencyRejection_RetryAfter_IsOneSecondWhenTokensRemain()
    {
        var quotas = Create(rpm: 1, burst: 5, concurrent: 1);
        using var holding = Admit(quotas);

        Assert.Equal(TimeSpan.FromSeconds(1), quotas.TryAdmit("t").RetryAfter);
    }

    [Fact]
    public async Task ConcurrentAdmissions_NeverExceedTheRateLimit()
    {
        var quotas = Create(rpm: 1, burst: 20, concurrent: 1_000);
        var admitted = new System.Collections.Concurrent.ConcurrentBag<TenantQuotaLease>();
        var rejections = new System.Collections.Concurrent.ConcurrentBag<QuotaRejection>();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            var admission = quotas.TryAdmit("t");
            if (admission.IsAdmitted)
            {
                admitted.Add(admission.Lease!);
            }
            else
            {
                rejections.Add(admission.Rejection);
            }
        })));

        Assert.Equal(20, admitted.Count);
        Assert.Equal(180, rejections.Count);
        Assert.All(rejections, r => Assert.Equal(QuotaRejection.RequestRate, r));
        Assert.Equal(0, quotas.GetSnapshot("t").AvailableRequestTokens, precision: 9);
    }

    [Fact]
    public void AbandonedCalls_CountAgainstTheTenant_OnlyOnTheirRoute_UntilTheyEnd()
    {
        var quotas = Create(burst: 100, concurrent: 2);
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        AbandonOn(quotas, "r1", first.Task);
        AbandonOn(quotas, "r1", second.Task);

        Assert.Equal(0, quotas.GetSnapshot("t").ActiveRequests); // the slots themselves were released
        Assert.Equal(2, quotas.AbandonedCallsFor("t", "r1"));

        var refused = quotas.TryAdmit("t", "r1");
        Assert.Equal(QuotaRejection.AbandonedCalls, refused.Rejection);
        Assert.Equal(TimeSpan.FromSeconds(1), refused.RetryAfter);

        using (var otherRoute = Admit(quotas, "t", "r2")) // other routes stay open to the tenant
        {
        }

        using (var otherTenant = Admit(quotas, "u", "r1")) // and the route stays open to other tenants
        {
        }

        first.SetResult(); // one abandoned call finally ends
        Assert.Equal(1, quotas.AbandonedCallsFor("t", "r1"));
        using var again = Admit(quotas, "t", "r1");
    }

    [Fact]
    public void InProgressPlusAbandonedOnARoute_NeverExceedMaxConcurrentRequests()
    {
        var quotas = Create(burst: 100, concurrent: 3);
        var hanging = new TaskCompletionSource();
        AbandonOn(quotas, "r1", hanging.Task);
        AbandonOn(quotas, "r1", hanging.Task);

        using var elsewhere = Admit(quotas, "t", "r2"); // one request in progress on another route

        // 1 in progress + 2 abandoned on r1 = 3 = MaxConcurrentRequests.
        Assert.Equal(QuotaRejection.AbandonedCalls, quotas.TryAdmit("t", "r1").Rejection);
        Assert.True(quotas.TryAdmit("t", "r3").IsAdmitted);

        hanging.SetResult();
        Assert.Equal(0, quotas.AbandonedCallsFor("t", "r1"));
    }

    [Fact]
    public void Abandonment_MovesTheRequestToTheRouteCount_WithoutCountingItTwice()
    {
        // The invoker reports an abandoned call before the caller disposes the lease. In between, the request must
        // not count both as "in progress" and as "abandoned", or the tenant is refused on the route one call early.
        var quotas = Create(burst: 100, concurrent: 2);
        var hanging = new TaskCompletionSource();
        var lease = Admit(quotas, "t", "r1");
        lease.MarkProviderCallStarted();

        lease.CountAbandonedCall(hanging.Task);

        // One call on the route in total (abandoned), so a second one is still within MaxConcurrentRequests = 2.
        Assert.Equal(0, quotas.GetSnapshot("t").ActiveRequests);
        Assert.Equal(1, quotas.AbandonedCallsFor("t", "r1"));
        using var second = Admit(quotas, "t", "r1");

        lease.Dispose(); // nothing left to release: the slot moved to the route's abandoned count
        Assert.Equal(1, quotas.GetSnapshot("t").ActiveRequests); // only "second"

        hanging.SetResult();
        Assert.Equal(0, quotas.AbandonedCallsFor("t", "r1"));
    }

    /// <summary>A call admitted on a route that reached the provider, was abandoned, and keeps running until the task ends.</summary>
    private static void AbandonOn(TenantQuotaService quotas, string route, Task stillRunning)
    {
        using var lease = Admit(quotas, "t", route);
        lease.MarkProviderCallStarted();
        lease.CountAbandonedCall(stillRunning);
    }

    /// <summary>A request that reached the provider and completed: its token is spent.</summary>
    private static void Spend(TenantQuotaService quotas)
    {
        using var lease = Admit(quotas);
        lease.MarkProviderCallStarted();
    }

    private static TenantQuotaLease Admit(TenantQuotaService quotas, string tenant = "t", string? route = null)
    {
        var admission = quotas.TryAdmit(tenant, route);
        Assert.True(admission.IsAdmitted, $"Expected admission, got {admission.Rejection}");
        return admission.Lease!;
    }
}
