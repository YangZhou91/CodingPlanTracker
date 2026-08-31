using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;
using PlanMeter.Core.Store;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// PROV-02 / SC#2 — the supervisor-isolation proof: one provider's poller throwing on
/// every poll, or rate-limiting itself with 429 + Retry-After, must NEVER delay, block,
/// or crash another provider's poller. The <see cref="FaultInjectionAdapter"/> (D-06)
/// is a test-project-only fixture — it never ships in <c>src/</c> (D-06; verified by the
/// plan's <c>grep -rn 'FaultInjectionAdapter' src/</c> returning nothing).
/// </summary>
/// <remarks>
/// The faulted provider degrades only its OWN row: <c>PollOnceAsync</c> catches the
/// thrown fault and publishes an Error reading (degrade-don't-throw), and a 429
/// Retry-After extends only ITS OWN next tick (REFRESH-03). The healthy provider's slot
/// keeps updating at its cadence throughout both fault modes — structural isolation
/// (per-instance pollers, per-slot store), not a discipline.
/// </remarks>
public sealed class FaultInjectionAdapterTests
{
    [Fact]
    public async Task SC2_faulted_poller_never_blocks_or_crashes_the_healthy_poller()
    {
        var fault = new FaultInjectionAdapter();
        var healthy = new HealthyAdapter();
        var store = new UsageStore();
        store.Register("healthy");
        store.Register("fault");
        using var cts = new CancellationTokenSource();

        // Both pollers share the SAME keyed store (distinct slots) and run concurrently
        // at a short tick so the test is fast and deterministic. Public tick-interval
        // ctor — same as the production App wiring, minus the config.
        var healthyPoller = new ProviderPoller(
            healthy, new NullCredentialSource(), store, new PollIntervalSource(TimeSpan.FromMilliseconds(200)));
        var faultPoller = new ProviderPoller(
            fault, new NullCredentialSource(), store, new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        // ── Phase 1 — Throw mode: the fault adapter throws on EVERY poll. ──
        fault.Mode = FaultInjectionAdapter.Behavior.Throw;
        await healthyPoller.StartAsync(cts.Token);
        await faultPoller.StartAsync(cts.Token);

        (await WaitUntilAsync(() => store.Current("healthy") is not null, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the healthy poller must publish a reading at its cadence");
        (await WaitUntilAsync(() => store.Current("fault") is not null, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the faulted poller must publish a reading (its own degraded slot)");

        store.Current("healthy")!.Status.Should().Be(
            ReadingStatus.Ok, "the healthy slot must show a live Ok reading");
        store.Current("fault")!.Status.Should().Be(
            ReadingStatus.Error, "the faulted slot must degrade to Error, never crash");

        // Let several throw-mode ticks run: the faulted loop must demonstrably stay
        // ALIVE (count climbs) rather than dying on the first fault.
        await Task.Delay(1000);
        healthy.FetchCount.Should().BeGreaterThan(0, "the healthy poller must keep ticking");
        fault.FetchCount.Should().BeGreaterThan(0, "even the faulted poller's loop survives and keeps degrading");

        healthyPoller.ExecuteTask!.IsFaulted.Should().BeFalse(
            "a faulting provider must never crash another provider's loop");
        faultPoller.ExecuteTask!.IsFaulted.Should().BeFalse(
            "degrade-don't-throw — the faulted poller's OWN loop must survive every injected fault");

        // ── Phase 2 — RateLimit mode: the fault adapter returns 429 + Retry-After(1h). ──
        // Record the baselines BEFORE the mode switch so any post-switch fetch (at most
        // the in-flight throw + the first 429) is counted against the freeze bound below.
        int faultAtSwitch = fault.FetchCount;
        int healthyAtSwitch = healthy.FetchCount;
        fault.Mode = FaultInjectionAdapter.Behavior.RateLimit;

        (await WaitUntilAsync(() => fault.FetchCount > faultAtSwitch, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the faulted poller must perform the 429 fetch that arms its 1h backoff");

        // Observe for a window longer than several healthy ticks.
        await Task.Delay(1500);

        // The faulted poller's 1h backoff extends ITS OWN next tick — at most the
        // transition fetch(s) may have fired (in-flight throw + first 429). A regression
        // that ignores Retry-After (T-02-31 retry-storm) would keep hammering every
        // 200ms and climb far past this bound.
        fault.FetchCount.Should().BeLessOrEqualTo(
            faultAtSwitch + 2,
            "after arming its 1h backoff the faulted poller must freeze — a Retry-After that is ignored would keep polling");

        // The healthy poller's cadence is untouched: it must climb well past the count
        // it had when the faulted poller went into backoff.
        healthy.FetchCount.Should().BeGreaterThan(
            healthyAtSwitch + 3,
            "the healthy provider must keep updating at its cadence while the faulted provider backs off");

        // Overall SC#2: across both fault modes the healthy provider ran materially more
        // fetches than the faulted one — one provider's pressure never hides another's
        // real usage and never blocks it.
        healthy.FetchCount.Should().BeGreaterThan(fault.FetchCount,
            "the healthy poller's total fetch count must exceed the faulted one's — its cadence was never blocked");

        await healthyPoller.StopAsync(cts.Token);
        await faultPoller.StopAsync(cts.Token);

        healthyPoller.ExecuteTask!.IsFaulted.Should().BeFalse("healthy loop must complete cleanly");
        faultPoller.ExecuteTask!.IsFaulted.Should().BeFalse("faulted loop must complete cleanly");
    }

    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses; returns whether it became true.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    /// <summary>
    /// D-06 — the injectable fault fixture. Three modes: <see cref="Behavior.Throw"/>
    /// (throws on every fetch — the faulted poller's catch-all must degrade, not crash),
    /// <see cref="Behavior.RateLimit"/> (returns an Error reading + <see cref="RetryAfter"/>
    /// — the poller must back off only its own next tick), and <see cref="Behavior.Succeed"/>.
    /// Test-project only — never referenced by <c>src/</c>.
    /// </summary>
    private sealed class FaultInjectionAdapter : IProviderAdapter
    {
        public enum Behavior
        {
            Succeed,
            Throw,
            RateLimit,
        }

        private readonly object _gate = new();
        private Behavior _mode;
        private int _fetchCount;

        public ProviderId Id => "fault";
        public string DisplayName => "Fault";
        public bool SupportsUsageApi => true;
        public bool RequiresManualKey => false;
        public bool ManualOnlyFetch => false;
        public bool SupportsOAuthLogin => false;
        public AuthFamily AuthFamily => AuthFamily.Key;
        public string? ConsoleUrl => null;
        public string? QualifierText => null;
        public string? UnsupportedReason => null;
        public string? FloorReason => null;
        public string? ReLoginGuidance => null;
        public bool Detect() => true;
        public int FetchCount => Volatile.Read(ref _fetchCount);

        /// <summary>The Retry-After the poller must honor in <see cref="Behavior.RateLimit"/> mode.</summary>
        public TimeSpan RetryAfter { get; set; } = TimeSpan.FromHours(1);

        /// <summary>The injectable fault behavior. Swapped by the test thread while the poll thread reads.</summary>
        public Behavior Mode
        {
            get { lock (_gate) { return _mode; } }
            set { lock (_gate) { _mode = value; } }
        }

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(OkReading());

        public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fetchCount);
            switch (Mode)
            {
                case Behavior.Throw:
                    throw new HttpRequestException("injected fault");
                case Behavior.RateLimit:
                    return Task.FromResult(new AdapterFetchResult(ErrorReading("rate limited"), RetryAfter));
                default:
                    return Task.FromResult(new AdapterFetchResult(OkReading(), null));
            }
        }

        public static UsageReading OkReading() => new(
            Provider: "Fault",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 30.0,
            RemainingPct: 70.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: null,
            ErrorMessage: null);

        public static UsageReading ErrorReading(string message) => new(
            Provider: "Fault",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: message);
    }

    /// <summary>The always-healthy partner fixture — its cadence must never be affected by the faulted poller.</summary>
    private sealed class HealthyAdapter : IProviderAdapter
    {
        private int _fetchCount;

        public ProviderId Id => "healthy";
        public string DisplayName => "Healthy";
        public bool SupportsUsageApi => true;
        public bool RequiresManualKey => false;
        public bool ManualOnlyFetch => false;
        public bool SupportsOAuthLogin => false;
        public AuthFamily AuthFamily => AuthFamily.Key;
        public string? ConsoleUrl => null;
        public string? QualifierText => null;
        public string? UnsupportedReason => null;
        public string? FloorReason => null;
        public string? ReLoginGuidance => null;
        public bool Detect() => true;
        public int FetchCount => Volatile.Read(ref _fetchCount);

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(OkReading());

        public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fetchCount);
            return Task.FromResult(new AdapterFetchResult(OkReading(), null));
        }

        public static UsageReading OkReading() => new(
            Provider: "Healthy",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 42.0,
            RemainingPct: 58.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: null,
            ErrorMessage: null);
    }
}
