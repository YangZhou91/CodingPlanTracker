using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;
using PlanMeter.Core.Store;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// The generalized <see cref="ProviderPoller"/> loop — the three load-bearing invariants
/// (l9h swappable-TCS signal, ohm/G-01-7 per-iteration timer recreate, R3 in-flight
/// funneling) pinned against the generalized class, plus the empty-key delegation
/// contract (the poller always calls the adapter, which classifies).
/// </summary>
public sealed class ProviderPollerTests
{
    [Fact]
    public async Task R3_double_RefreshNow_results_in_a_single_FetchUsageAsync_call()
    {
        // R3 load-bearing invariant on the generalized poller: a rapid double-click on
        // Refresh-now must NOT spawn two concurrent fetches. The mock adapter (asserting
        // the named "zai" client) records exactly one FetchUsageAsync invocation.
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"code\":200,\"msg\":\"ok\",\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"number\":5,\"window\":18000,\"used\":100,\"limit\":1000}]},\"success\":true}",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));
        using var keyStore = TempKeyStore.Create();
        keyStore.Protect("test-key");

        var store = new UsageStore();
        store.Register("zai");
        using var cts = new CancellationTokenSource();
        // Default interval (10 min) — no auto-tick fires inside the test window, so the
        // only fetches are the startup fetch + the funneled refresh.
        var poller = new ProviderPoller(adapter, keyStore, store);

        await poller.StartAsync(cts.Token);
        await Task.Delay(300);

        int fetchCountAfterStartup = adapter.FetchCount;

        Task<UsageReading> first = poller.RefreshNowAsync();
        Task<UsageReading> second = poller.RefreshNowAsync();
        await Task.WhenAll(first, second);

        await Task.Delay(300);

        await poller.StopAsync(cts.Token);

        int refreshTriggeredFetches = adapter.FetchCount - fetchCountAfterStartup;
        refreshTriggeredFetches.Should().BeLessOrEqualTo(1,
            "R3: double-RefreshNow must NOT spawn two concurrent FetchUsageAsync calls — it should reuse the single in-flight fetch");
    }

    // G-01-7 regression against ProviderPoller — PeriodicTimer single-outstanding-wait.
    // The per-iteration timer recreate MUST hold on the generalized loop: a manual refresh
    // that wins the WhenAny race must not leave an outstanding WaitForNextTickAsync that
    // faults the next iteration's timer (InvalidOperationException → poller fault).
    [Fact]
    public async Task RefreshNow_during_outstanding_tick_does_not_crash_and_loop_survives()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"code\":200,\"msg\":\"ok\",\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"number\":5,\"window\":18000,\"used\":100,\"limit\":1000}]},\"success\":true}",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));
        using var keyStore = TempKeyStore.Create();
        keyStore.Protect("test-key");

        var store = new UsageStore();
        store.Register("zai");
        using var cts = new CancellationTokenSource();
        // Short tick so the WhenAny race is exercised repeatedly inside a fast test.
        var poller = new ProviderPoller(adapter, keyStore, store, new PollIntervalSource(TimeSpan.FromMilliseconds(400)));

        await poller.StartAsync(cts.Token);
        await Task.Delay(300);

        int fetchCountAfterStartup = adapter.FetchCount;

        Task<UsageReading> refresh = poller.RefreshNowAsync(cts.Token);
        await refresh;

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        poller.ExecuteTask!.IsFaulted.Should().BeFalse(
            "G-01-7: poll loop must NOT fault after a manual refresh during an outstanding PeriodicTimer tick");

        adapter.FetchCount.Should().BeGreaterThan(fetchCountAfterStartup + 1,
            "the loop must still be alive — at least the refresh fetch AND one subsequent tick fetch must have fired");

        await poller.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Poller_delegates_empty_key_classification_to_the_adapter()
    {
        // The generalization: the poller does NOT short-circuit an empty key to
        // NotLoggedIn itself — it ALWAYS calls the adapter, which classifies.
        //  (a) Z.ai + a null key source -> the adapter's guard returns NotLoggedIn.
        var zaiHandler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var zaiAdapter = new ZaiAdapter(new StubFactory(zaiHandler));
        var zaiStore = new UsageStore();
        zaiStore.Register("zai");
        using var zaiCts = new CancellationTokenSource();
        var zaiPoller = new ProviderPoller(zaiAdapter, new NullCredentialSource(), zaiStore, new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await zaiPoller.StartAsync(zaiCts.Token);
        UsageReading zaiReading = await zaiPoller.RefreshNowAsync(zaiCts.Token);
        await zaiPoller.StopAsync(zaiCts.Token);

        zaiReading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "a null key must be delegated to the Z.ai adapter, whose empty-key guard classifies NotLoggedIn");
        zaiStore.Current("zai")!.Status.Should().Be(ReadingStatus.NotLoggedIn);
        zaiHandler.CallCount.Should().Be(0,
            "the Z.ai empty-key guard short-circuits before HTTP — the point is the POLLER still called the adapter");

        //  (b) A keyless stub adapter + a null key source -> the adapter ignores the
        //      key and returns its fixed Ok reading.
        var stubStore = new UsageStore();
        stubStore.Register("stub");
        using var stubCts = new CancellationTokenSource();
        var stubPoller = new ProviderPoller(new KeylessStubAdapter(), new NullCredentialSource(), stubStore, new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await stubPoller.StartAsync(stubCts.Token);
        UsageReading stubReading = await stubPoller.RefreshNowAsync(stubCts.Token);
        await stubPoller.StopAsync(stubCts.Token);

        stubReading.Status.Should().Be(ReadingStatus.Ok,
            "a keyless adapter must still be fetched with a null key and classify Ok — one poller serves both manual-key and keyless adapters");
        stubStore.Current("stub")!.UsedPct.Should().Be(50.0);
    }

    /// <summary>
    /// A minimal keyless fake (id "stub"): always detected, ignores the key, returns a
    /// fixed Ok reading — the local stand-in for the retired dev-only adapter in the
    /// empty-key delegation test above.
    /// </summary>
    private sealed class KeylessStubAdapter : IProviderAdapter
    {
        public ProviderId Id => "stub";
        public string DisplayName => "Stub";
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

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(FixedReading());

        public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(new AdapterFetchResult(FixedReading(), null));

        private static UsageReading FixedReading() => new(
            Provider: "Stub",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 50.0,
            RemainingPct: 50.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: null,
            ErrorMessage: null);
    }

    // WR-02 — the clear-key mechanism the Settings window's Clear handler relies on: after
    // the DPAPI blob is cleared, the poller's next refresh publishes NotLoggedIn (which the
    // strip row renders as the NO KEY badge) WITHOUT hitting HTTP — the adapter's empty-key
    // guard short-circuits before the network. This is what makes "Clear → the strip row
    // shows NO KEY" immediate instead of waiting for the next scheduled poll (up to 10 min).
    [Fact]
    public async Task Refresh_after_key_store_clear_publishes_NotLoggedIn_without_http()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"code\":200,\"msg\":\"ok\",\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"number\":5,\"window\":18000,\"used\":100,\"limit\":1000}]},\"success\":true}",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var adapter = new ZaiAdapter(new StubFactory(handler));
        using var keyStore = TempKeyStore.Create();
        keyStore.Protect("test-key");

        var store = new UsageStore();
        store.Register("zai");
        using var cts = new CancellationTokenSource();
        // Default 10-min interval — no scheduled tick fires inside the test window, so the
        // only fetches are the startup fetch (with the key → HTTP) and the clear-refresh.
        var poller = new ProviderPoller(adapter, keyStore, store);

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => store.Current("zai")?.Status == ReadingStatus.Ok, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("with a stored key the startup fetch must publish Ok");
        int callsWithKey = handler.CallCount;
        callsWithKey.Should().BeGreaterThanOrEqualTo(1, "the startup fetch with a stored key must hit HTTP");

        // Clear the key blob → the next refresh reads a null key → NotLoggedIn, no HTTP.
        keyStore.Clear();
        UsageReading reading = await poller.RefreshNowAsync(cts.Token);
        await poller.StopAsync(cts.Token);

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "after the key store is cleared a refresh must publish NotLoggedIn (the NO KEY badge signal)");
        store.Current("zai")!.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "the store slot must reflect NotLoggedIn so the row renders NO KEY, not a bare dash");
        handler.CallCount.Should().Be(callsWithKey,
            "the empty-key guard must short-circuit BEFORE HTTP — a cleared key triggers no network call (rate-safe)");
    }

    // D-21/REFRESH-03 — a fetch whose AdapterFetchResult carries Retry-After extends THIS
    // poller's NEXT tick to now + min(RetryAfter, ceiling). No in-tick retry. A manual
    // refresh during the backoff may fetch now but never clears the outstanding backoff.
    [Fact]
    public async Task RetryAfter_extends_next_tick_with_no_intick_retry_and_manual_refresh_does_not_clear()
    {
        var adapter = new ControllableAdapter { RetryAfter = TimeSpan.FromSeconds(2) };
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(adapter, new NullCredentialSource(), store, new PollIntervalSource(TimeSpan.FromMilliseconds(300)));

        await poller.StartAsync(cts.Token);

        // Startup fetch (429) sets the backoff; the first scheduled auto-poll is pushed
        // out to the end of the backoff window and ALSO returns 429 (re-arms it).
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("startup fetch must run");
        (await WaitUntilAsync(() => adapter.FetchCount >= 2, TimeSpan.FromSeconds(4)))
            .Should().BeTrue("the scheduled auto-poll after the 429 must still run (it re-arms the backoff)");
        int countAtBackoffStart = adapter.FetchCount;

        // The backoff governs the NEXT scheduled tick: no fetch fires within the window
        // (this is ALSO the no-in-tick-retry assertion — the 429 is not retried).
        await Task.Delay(1200);
        adapter.FetchCount.Should().Be(countAtBackoffStart,
            "the 429 backoff must extend the NEXT tick — no scheduled fetch and no in-tick retry within the window");

        // A manual refresh during backoff is allowed to fetch NOW (still 429 here), and
        // re-arms the backoff — it does not clear it and does not advance the schedule.
        UsageReading manual = await poller.RefreshNowAsync(cts.Token);
        manual.Status.Should().Be(ReadingStatus.Error, "the manual fetch sees the same rate-limited Error reading");
        int countAfterManual = adapter.FetchCount;
        countAfterManual.Should().Be(countAtBackoffStart + 1,
            "the manual refresh must fetch exactly once more");

        await Task.Delay(1200);
        adapter.FetchCount.Should().Be(countAfterManual,
            "the backoff still governs the next scheduled auto-poll after the manual refresh");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // REFRESH-03/idempotency — the backoff is cleared ONLY by a later SCHEDULED auto-poll
    // returning no RetryAfter. A refresh-triggered poll that SUCCEEDS still does not clear it.
    [Fact]
    public async Task Backoff_is_cleared_only_by_a_scheduled_auto_poll_with_no_RetryAfter()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(adapter, new NullCredentialSource(), store, new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("startup fetch must run (Ok)");

        // Turn on rate limiting; the next scheduled auto-poll returns 429 + 3s -> backoff.
        adapter.RetryAfter = TimeSpan.FromSeconds(3);
        (await WaitUntilAsync(() => adapter.FetchCount >= 2, TimeSpan.FromSeconds(4)))
            .Should().BeTrue("the scheduled auto-poll must hit the 429 and arm the backoff");
        int countAtBackoff = adapter.FetchCount;

        // Manual refresh during backoff, now returning SUCCESS with no RetryAfter.
        adapter.RetryAfter = null;
        UsageReading manual = await poller.RefreshNowAsync(cts.Token);
        manual.Status.Should().Be(ReadingStatus.Ok);
        adapter.FetchCount.Should().Be(countAtBackoff + 1);

        // The successful manual refresh must NOT clear the outstanding backoff: no
        // scheduled auto-poll fires until the backoff window (3s from arming) elapses.
        await Task.Delay(1500);
        adapter.FetchCount.Should().Be(countAtBackoff + 1,
            "a refresh-triggered poll with a SUCCESSFUL fetch must never clear an outstanding backoff");

        // The NEXT scheduled auto-poll fires at the end of the window, returns no
        // RetryAfter -> clears the backoff, then the base cadence resumes.
        (await WaitUntilAsync(() => adapter.FetchCount >= countAtBackoff + 2, TimeSpan.FromSeconds(5)))
            .Should().BeTrue("the scheduled auto-poll after the backoff window must run and clear the backoff");
        int afterClear = adapter.FetchCount;

        (await WaitUntilAsync(() => adapter.FetchCount >= afterClear + 1, TimeSpan.FromSeconds(2)))
            .Should().BeTrue("after the backoff is cleared the base tick cadence resumes");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // D-06 — the enabled-flag park. A DISABLED poller parks: no startup fetch, no scheduled
    // tick fetch, and no manual RefreshNow fetch while the flag stays false (PollOnceAsync
    // early-returns; the park consumes + swaps the signal so the loop parks on a fresh,
    // uncompleted TCS instead of busy-spinning on a completed one).
    [Fact]
    public async Task Disabled_poller_parks_no_fetch_runs_while_disabled()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        // Short tick — a parked poller must NOT tick at any cadence.
        var poller = new ProviderPoller(adapter, new NullCredentialSource(), store, new PollIntervalSource(TimeSpan.FromMilliseconds(100)));

        // Disable BEFORE StartAsync → the startup fetch gate (if (_enabled)) also skips.
        poller.SetEnabled(false);
        await poller.StartAsync(cts.Token);

        await Task.Delay(400);
        adapter.FetchCount.Should().Be(0, "a disabled poller must not fetch at startup or on any scheduled tick");

        // A manual refresh while disabled must NOT spawn a fetch (the park consumes the
        // signal and swaps in a fresh TCS; PollOnceAsync's `if (!_enabled) return` guards
        // any in-flight entry). Not awaited — a parked poller never claims the R3 in-flight
        // slot, so the refresh handshake times out (harmless; the poller stays parked).
        _ = poller.RefreshNowAsync();
        await Task.Delay(300);
        adapter.FetchCount.Should().Be(0, "a manual refresh while disabled must not fetch");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // D-06 — re-enable triggers EXACTLY ONE immediate first fetch, funneled through the
    // existing R3 path (never a BackgroundService restart — Pitfall 2). A default 10-min
    // interval isolates the immediate fetch: no scheduled tick can fire inside the window,
    // so the count proving the transition is deterministic.
    [Fact]
    public async Task Re_enable_triggers_exactly_one_immediate_first_fetch()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        // Default 10-min interval — the re-enable immediate fetch is the ONLY fetch possible
        // inside the test window (a scheduled tick would need ~10 min).
        var poller = new ProviderPoller(adapter, new NullCredentialSource(), store);

        poller.SetEnabled(false);
        await poller.StartAsync(cts.Token);
        await Task.Delay(300);
        adapter.FetchCount.Should().Be(0, "precondition: a parked poller must not fetch");

        poller.SetEnabled(true);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("re-enabling must trigger an immediate first fetch (D-06)");
        int afterReenable = adapter.FetchCount;

        // With a 10-min interval no scheduled tick fires here → the count must stay EXACTLY
        // one: the immediate fetch, funneled through R3, not a leak and not a storm.
        await Task.Delay(400);
        adapter.FetchCount.Should().Be(afterReenable,
            "the re-enable transition must produce exactly one fetch — no leak, no storm");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // D-06 — the load-bearing no-fault pin: ExecuteTask is NOT faulted across a
    // disable→enable cycle (the enabled-flag park never tears the loop — T-03-08).
    [Fact]
    public async Task Disable_enable_cycle_does_not_fault_the_poll_loop()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(adapter, new NullCredentialSource(), store, new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("startup fetch must run");

        // disable → park → enable → immediate fetch resumes.
        poller.SetEnabled(false);
        await Task.Delay(300);
        poller.SetEnabled(true);
        (await WaitUntilAsync(() => adapter.FetchCount >= 2, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("re-enable must resume polling with an immediate first fetch");
        await Task.Delay(300);

        poller.ExecuteTask!.IsFaulted.Should().BeFalse(
            "the enabled-flag park must never tear the poll loop across a disable→enable cycle");
        await poller.StopAsync(cts.Token);
    }

    // D-22 — every tick's base delay is multiplied by a jitter factor in [0.6, 1.4].
    // The fixed-seed Random seam makes the sequence deterministic across runs.
    [Fact]
    public async Task Every_tick_delay_falls_within_the_jitter_range()
    {
        var adapter = new ControllableAdapter(); // always Ok, never a RetryAfter
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        TimeSpan baseInterval = TimeSpan.FromMilliseconds(400);
        var poller = new ProviderPoller(
            adapter, new NullCredentialSource(), store, new PollIntervalSource(baseInterval),
            logger: null, random: new Random(42));

        // Record the FetchedAtUtc of every published reading (one per fetch).
        var timestamps = new List<DateTimeOffset>();
        void OnChanged(object? sender, ProviderReadingChangedEventArgs args)
        {
            if (args.Reading is not null)
            {
                lock (timestamps) timestamps.Add(args.Reading.FetchedAtUtc);
            }
        }

        store.Changed += OnChanged;
        await poller.StartAsync(cts.Token);

        // ~6-12 ticks at 400ms jittered to [240ms, 560ms].
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        await poller.StopAsync(cts.Token);
        store.Changed -= OnChanged;
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();

        DateTimeOffset[] observed;
        lock (timestamps) observed = timestamps.Distinct().OrderBy(t => t).ToArray();
        observed.Length.Should().BeGreaterThan(4, "the poller must complete several ticks");

        var gaps = new List<TimeSpan>();
        for (int i = 1; i < observed.Length; i++)
        {
            gaps.Add(observed[i] - observed[i - 1]);
        }

        // Jitter bounds on the nominal period, with a scheduling-tolerance slack so a
        // loaded CI box does not flake. Without jitter every gap would sit at ~400ms.
        TimeSpan slack = TimeSpan.FromMilliseconds(150);
        foreach (TimeSpan gap in gaps)
        {
            gap.Should().BeGreaterOrEqualTo(baseInterval * 0.6 - slack,
                "every observed tick delay must stay above the lower jitter bound");
            gap.Should().BeLessOrEqualTo(baseInterval * 1.4 + slack,
                "every observed tick delay must stay below the upper jitter bound");
        }

        // The range assertion alone cannot distinguish jitter from a fixed base tick —
        // prove the jitter is genuinely applied: at least one gap deviates more than
        // 10% from the nominal period.
        bool jittered = gaps.Any(g => g < baseInterval * 0.9 || g > baseInterval * 1.1);
        jittered.Should().BeTrue("the ±40% baseline jitter must be observable in the tick delays");
    }

    // CONF-03/D-13 — LIVE-APPLY: the poller re-reads PollIntervalSource.Current per tick, so
    // a SetInterval is picked up on the NEXT scheduled poll. No restart, and NO immediate
    // fetch at the moment of the change (the change only alters the NEXT tick's delay).
    // Count-based discriminator (robust against scheduling noise): the SAME window yields
    // FAR fewer ticks at the new slow cadence than it did at the old fast one.
    [Fact]
    public async Task SetInterval_is_picked_up_on_the_next_tick_without_an_immediate_fetch()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        // A short initial cadence (300ms base) so Phase A produces several ticks quickly;
        // the new cadence (1500ms base) is 5x slower with cleanly separable tick counts.
        var intervalSource = new PollIntervalSource(TimeSpan.FromMilliseconds(300));
        var poller = new ProviderPoller(adapter, new NullCredentialSource(), store, intervalSource);

        await poller.StartAsync(cts.Token);

        // Phase A: at a 300ms base, several ticks fire in 2s (jittered to [180,420]ms + the
        // adapter's 100ms async fetch window => observed gaps ~[280,520]ms).
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        int phaseACount = adapter.FetchCount;
        phaseACount.Should().BeGreaterThanOrEqualTo(3,
            "Phase A (300ms base) must fire several ticks including the startup fetch");

        // Live-apply the new interval. SetInterval is a pure volatile write — it MUST NOT
        // itself trigger a fetch (D-13: no immediate fetch on change). At most one in-flight
        // OLD-cadence tick may land inside the short settle window.
        intervalSource.SetInterval(TimeSpan.FromMilliseconds(1500));
        await Task.Delay(TimeSpan.FromMilliseconds(400), cts.Token);
        int immediatelyAfterSwitch = adapter.FetchCount;
        immediatelyAfterSwitch.Should().BeLessThanOrEqualTo(phaseACount + 1,
            "SetInterval must not spawn a fetch — at most one in-flight old-cadence tick may land (D-13)");

        // Phase B: the same 2s window at a 1500ms base (jittered to [900,2100]ms + 100ms
        // fetch => observed gaps ~[1000,2200]ms) fits AT MOST ~2 ticks. If the poller had
        // kept a ctor-frozen 300ms cadence it would fire 6+ — the discriminator.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        int phaseBFetches = adapter.FetchCount - immediatelyAfterSwitch;
        phaseBFetches.Should().BeLessThanOrEqualTo(3,
            "the new 1500ms cadence must fire at most ~2 ticks in a 2s window — the poller re-read the live source");
        phaseBFetches.Should().BeGreaterThanOrEqualTo(1,
            "the poller must stay alive and keep scheduling ticks at the new cadence");
        phaseBFetches.Should().BeLessThan(phaseACount,
            "the cadence must observably slow after SetInterval — a ctor-frozen interval would keep Phase A's rate");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // G-04-4 / 04-06 Task 2 (TDD) — the park-consume regression pin (Test A): a poller
    // constructed disabled (the DI seeding shape: config.json excludes the provider
    // from enabledProviders), started, then re-enabled via SetEnabled(true) performs
    // EXACTLY ONE adapter fetch (the park-consume handoff at ProviderPoller.cs L328-351)
    // and the store holds the published reading. Uses a SHORT interval so the fetch
    // settles fast but the exact-one assertion is deterministic (no scheduled tick
    // fires inside the bounded wait window).
    [Fact]
    public async Task Park_consume_handoff_produces_exactly_one_fetch_on_reenable()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        // Short interval — re-enable must produce EXACTLY one fetch before any
        // scheduled tick fires (the handoff fetch is the D-06 immediate first fetch).
        var poller = new ProviderPoller(
            adapter, new NullCredentialSource(), store,
            new PollIntervalSource(TimeSpan.FromSeconds(10)));

        // Disable BEFORE StartAsync (the DI seeding shape).
        poller.SetEnabled(false);
        await poller.StartAsync(cts.Token);

        // Park settles — no fetch runs while disabled.
        await Task.Delay(300);
        adapter.FetchCount.Should().Be(0, "precondition: a disabled poller must not fetch");

        // Re-enable — SetEnabled(true) fires RefreshNowAsync internally.
        poller.SetEnabled(true);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("re-enable must trigger exactly one park-consume fetch");

        int afterReenable = adapter.FetchCount;

        // With a 10s interval, no scheduled tick can fire inside the wait window.
        await Task.Delay(500);
        adapter.FetchCount.Should().Be(afterReenable,
            "the park-consume handoff must produce EXACTLY one fetch — not zero, not two");

        // The store must hold the published reading from the single fetch.
        store.Current("ctrl")!.Status.Should().Be(ReadingStatus.Ok,
            "the store must hold the reading from the single park-consume fetch");
        store.Current("ctrl")!.UsedPct.Should().Be(30.0,
            "the reading must be the ControllableAdapter's fixed Ok reading");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // G-04-4 / 04-06 Task 2 (TDD) — the park-consume regression pin (Test B): a
    // parked poller that receives a refresh signal (the global-menu fan-out shape —
    // its signal lands while disabled) must NOT fetch while disabled. The park
    // branch's signal swap prevents a busy-spin. After re-enabling, exactly ONE
    // fetch runs (not the accumulated signals).
    [Fact]
    public async Task Parked_poller_ignores_refresh_signals_until_reenabled_then_fetches_once()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new NullCredentialSource(), store,
            new PollIntervalSource(TimeSpan.FromSeconds(10)));

        // Disable BEFORE StartAsync (the DI seeding shape).
        poller.SetEnabled(false);
        await poller.StartAsync(cts.Token);
        await Task.Delay(300);

        // Fire multiple refresh signals while disabled (the global-menu fan-out
        // shape — MainWindow fans out RefreshNowAsync to every poller, disabled
        // ones included). The park consumes + swaps each signal; no fetch runs.
        for (int i = 0; i < 3; i++)
        {
            _ = poller.RefreshNowAsync();
            await Task.Delay(100);
        }

        adapter.FetchCount.Should().Be(0,
            "a parked poller must ignore all refresh signals — no fetch runs while disabled");

        // Re-enable — the accumulated signals are swapped away; exactly ONE fetch
        // runs (the re-enable's own RefreshNowAsync signal, consumed by the
        // non-park branch as the D-06 immediate first fetch).
        poller.SetEnabled(true);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("re-enable must trigger exactly one fetch after signal accumulation");

        int afterReenable = adapter.FetchCount;

        await Task.Delay(500);
        adapter.FetchCount.Should().Be(afterReenable,
            "the park-consume handoff must produce EXACTLY one fetch — accumulated signals must not cause multiple fetches");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // G-04-4 liveness (04-06 Task 1) — every poller loop transition emits ONE sanitized
    // line through the injected ILogger. The test asserts the two lines that make a
    // silent poller detectable in the app log: "loop entered" (a never-started loop is
    // visible by its ABSENCE) and "fetch start" (a loop that runs but never dispatches
    // is visible by fetch-start lines never appearing). One short-interval poll cycle
    // produces the loop-entered line, the startup fetch's start+done pair, and at least
    // one scheduled tick's pair.
    [Fact]
    public async Task Poller_emits_sanitized_liveness_lines_per_loop_transition()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        var logger = new RecordingLogger();
        var poller = new ProviderPoller(
            adapter, new NullCredentialSource(), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)),
            logger: logger);

        await poller.StartAsync(cts.Token);
        // Startup fetch + at least one scheduled tick inside the window.
        (await WaitUntilAsync(() => adapter.FetchCount >= 2, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the poller must complete the startup fetch and one tick");
        await poller.StopAsync(cts.Token);

        string[] lines;
        lock (logger.Lines) lines = logger.Lines.ToArray();

        // The loop-entered line names the provider id and the enabled flag — the repro
        // instrument for the UAT minimax silence (a missing line = the loop never ran).
        lines.Should().Contain(l =>
            l.Contains("poller loop entered") && l.Contains("ctrl") && l.Contains("enabled=True"),
            "the loop-entered liveness line must appear for every started poller (the G-04-4 repro instrument)");

        // Fetch start/done pairs: scheduled startup fetch is isScheduledAutoPoll=false,
        // the tick fetch is true. Both shapes must be observable.
        lines.Should().Contain(l =>
            l.Contains("poller fetch start") && l.Contains("scheduled=False"),
            "the startup fetch's start line must carry scheduled=False");
        lines.Should().Contain(l =>
            l.Contains("poller fetch start") && l.Contains("scheduled=True"),
            "a scheduled tick's fetch-start line must carry scheduled=True");
        lines.Should().Contain(l =>
            l.Contains("poller fetch done") && l.Contains("status=Ok"),
            "a completed Ok fetch must emit its fetch-done status line");

        // SEC-03 — the liveness lines carry ONLY the id/event/flag/status tokens; the
        // redaction sink stays the single place a URL/header/body could enter, but the
        // poller's own lines must never embed one. Assert no line contains URL/key
        // vocabulary (defensive pin on the new surface this plan adds).
        lines.Should().NotContain(l =>
            l.Contains("http") || l.Contains("sk-") || l.Contains("Bearer") || l.Contains("Authorization"),
            "SEC-03: no liveness line may contain a URL, key prefix, or auth-header vocabulary");
    }

    /// <summary>
    /// G-04-4 liveness — park entry/exit lines. A poller disabled at StartAsync parks
    /// (parked line); re-enabling emits the unparked line as the loop leaves the park.
    /// </summary>
    [Fact]
    public async Task Poller_emits_park_and_unpark_liveness_lines()
    {
        var adapter = new ControllableAdapter();
        var store = new UsageStore();
        store.Register("ctrl");
        using var cts = new CancellationTokenSource();
        var logger = new RecordingLogger();
        // Default 10-min interval — no scheduled tick can fire; the only fetch is the
        // re-enable handoff's immediate one.
        var poller = new ProviderPoller(adapter, new NullCredentialSource(), store, logger: logger);

        poller.SetEnabled(false);
        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() =>
        {
            lock (logger.Lines) return logger.Lines.Any(l => l.Contains("poller parked"));
        }, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("a poller parked at startup must emit the parked liveness line");

        poller.SetEnabled(true);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("re-enable must run the immediate first fetch");
        (await WaitUntilAsync(() =>
        {
            lock (logger.Lines) return logger.Lines.Any(l => l.Contains("poller unparked"));
        }, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("leaving the park must emit the unparked liveness line");

        await poller.StopAsync(cts.Token);
    }

    /// <summary>
    /// G-04-4 (04-06 Task 1) — a minimal hand-rolled recording fake of
    /// <c>ILogger&lt;ProviderPoller&gt;</c>. The test project has no logging package
    /// reference; the type surfaces transitively via PlanMeter.Core's
    /// Microsoft.Extensions.Hosting reference. Stores the FORMATTED message strings
    /// (state interpolated) into a synchronized list; LogLevel/method/exception are
    /// accepted and ignored — the assertions only need the rendered line text.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ProviderPoller>
    {
        public readonly List<string> Lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string line = formatter(state, exception);
            lock (Lines) Lines.Add(line);
        }
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public int CallCount;

        public StubHandler(HttpResponseMessage response) { _response = response; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(_response);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name)
        {
            // SEC-02 contract: the adapter must request the named "zai" client.
            name.Should().Be(HttpExtensions.ZaiClientName,
                "ZaiAdapter must only ever create the named 'zai' HttpClient (so the SEC-02 + SEC-03 handlers run)");
            return new HttpClient(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri(HttpExtensions.ZaiBaseUrl),
                Timeout = HttpExtensions.ZaiTimeout,
            };
        }
    }

    private sealed class TempKeyStore : DpapiKeyStore, IDisposable
    {
        private readonly string _tempDir;
        private TempKeyStore(string blobPath, string tempDir) : base(blobPath) { _tempDir = tempDir; }

        public static TempKeyStore Create()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "planmeter-poller-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string blobPath = Path.Combine(tempDir, "zai.key.bin");
            return new TempKeyStore(blobPath, tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// A controllable <see cref="IProviderAdapter"/> for the D-21/REFRESH-03 backoff and
    /// D-22 jitter tests: always "present", returns an Ok reading by default, and when
    /// <see cref="RetryAfter"/> is non-null returns an Error reading carrying that
    /// duration. Thread-safe swap (the test thread toggles it while the poll thread reads).
    /// </summary>
    private sealed class ControllableAdapter : IProviderAdapter
    {
        private readonly object _gate = new();
        private TimeSpan? _retryAfter;
        private int _fetchCount;

        public ProviderId Id { get; } = "ctrl";
        public string DisplayName => "Ctrl";
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

        public TimeSpan? RetryAfter
        {
            get { lock (_gate) { return _retryAfter; } }
            set { lock (_gate) { _retryAfter = value; } }
        }

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(OkReading());

        public async Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        {
            TimeSpan? ra = RetryAfter;

            // The R3 in-flight slot must stay observable to RefreshNowAsync's 50ms
            // busy-poll for the refresh handshake to work. A fully synchronous
            // Task.FromResult sets-and-clears the slot within microseconds, so
            // RefreshNowAsync times out (15s) and falls back to store.Current — which a
            // real adapter (real HTTP latency) never does. The small async delay models
            // the adapter's genuine I/O so the handshake observes the in-flight fetch,
            // matching the realism of the existing ZaiAdapter/StubHandler tests.
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);

            // Count COMPLETED fetches (not initiations): the backoff tests wait on
            // `FetchCount >= N` to mean "fetch N has finished and its reading is
            // published / backoff re-armed". An increment-at-top counter would satisfy
            // the wait while the fetch is still in its async window, making the next
            // RefreshNowAsync funnel (R3) into a still-running 429 fetch.
            Interlocked.Increment(ref _fetchCount);

            return ra is not null
                ? new AdapterFetchResult(ErrorReading("rate limited"), ra)
                : new AdapterFetchResult(OkReading(), null);
        }

        public static UsageReading OkReading() => new(
            Provider: "Ctrl",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 30.0,
            RemainingPct: 70.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: null,
            ErrorMessage: null);

        public static UsageReading ErrorReading(string message) => new(
            Provider: "Ctrl",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: message);
    }
}
