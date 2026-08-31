using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;
using PlanMeter.Core.Store;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// DATA-03 stale-vs-ERROR predicate + R3 funneling tests, re-pointed in Phase 2 to the
/// generalized <see cref="ProviderPoller"/> and the per-provider keyed
/// <see cref="UsageStore"/> (the Phase-1 single-provider shape was the N=1 case).
/// </summary>
/// <remarks>
/// Three coverage areas:
/// <list type="bullet">
///   <item><see cref="UsageStore"/> retains <c>LastSuccessful</c> per slot across an Error update (STALE rendering source).</item>
///   <item>R3 funneling — calling <see cref="ProviderPoller.RefreshNowAsync"/> twice in rapid succession
///   results in exactly ONE <c>FetchUsageAsync</c> invocation on the mock adapter (loop invariants hold on the generalized class).</item>
///   <item>Per-slot isolation — updating slot A fires <c>Changed(A)</c> only; a second provider's prior
///   success never makes the first's error stale.</item>
/// </list>
/// </remarks>
public sealed class StaleReadingTests
{
    [Fact]
    public void UsageStore_retains_LastSuccessful_across_an_Error_update()
    {
        var store = new UsageStore();
        store.Register("zai");

        store.Update("zai", new UsageReading(
            Provider: "Z.ai",
            FetchedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-5),
            Status: ReadingStatus.Ok,
            UsedPct: 30.0,
            RemainingPct: 70.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: Array.Empty<WindowReading>(),
            ErrorMessage: null));

        store.Update("zai", new UsageReading(
            Provider: "Z.ai",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: "Couldn't reach Z.ai."));

        store.LastSuccessful("zai").Should().NotBeNull("a prior Ok reading must survive a subsequent Error");
        store.LastSuccessful("zai")!.Status.Should().Be(ReadingStatus.Ok);
        store.LastSuccessful("zai")!.UsedPct.Should().Be(30.0);
        store.Current("zai")!.Status.Should().Be(ReadingStatus.Error);
    }

    [Fact]
    public void UsageStore_Clear_resets_LastSuccessful_and_Current()
    {
        var store = new UsageStore();
        store.Register("zai");

        store.Update("zai", new UsageReading(
            Provider: "Z.ai",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 30.0,
            RemainingPct: 70.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: Array.Empty<WindowReading>(),
            ErrorMessage: null));

        store.Clear("zai");

        store.Current("zai").Should().BeNull();
        store.LastSuccessful("zai").Should().BeNull();
    }

    [Fact]
    public async Task R3_double_RefreshNow_results_in_a_single_FetchUsageAsync_call()
    {
        // The R3 load-bearing invariant, now on the generalized ProviderPoller: a rapid
        // double-click on Refresh-now must NOT spawn two concurrent Z.ai fetches. Both
        // calls funnel through the single poller; the mock adapter records exactly one
        // FetchUsageAsync invocation.
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
        // Re-pointed to the generalized poller. Default 10-min interval (NO auto-tick
        // fires inside the test window) so the assertion stays the exact Phase-1 one:
        // double-RefreshNow must not spawn two concurrent fetches. (A short tick would
        // pile up auto-tick fetches during the verbatim 15s RefreshNow wait — the fast
        // mocked fetch window is usually missed by the wait loop's 50ms poll — breaking
        // the ≤1 assertion. The G-01-7 test below uses the short tick for the crash branch.)
        var poller = new ProviderPoller(adapter, keyStore, store);

        // Start the poller (it does a first fetch on startup; we wait for it to clear
        // before issuing our double-RefreshNow).
        await poller.StartAsync(cts.Token);

        // Give the startup fetch time to complete so the in-flight slot is idle.
        await Task.Delay(300);

        // Record the adapter fetch count AFTER the startup fetch completes.
        int fetchCountAfterStartup = adapter.FetchCount;

        // R3: fire two RefreshNowAsync calls back-to-back. Both should funnel.
        Task<UsageReading> first = poller.RefreshNowAsync();
        Task<UsageReading> second = poller.RefreshNowAsync();

        // Await both — they MUST complete (the poller's signal-driven loop services them).
        await Task.WhenAll(first, second);

        // Wait a moment for the fetch to land in the store.
        await Task.Delay(300);

        await poller.StopAsync(cts.Token);

        // The R3 funneling truth: double-RefreshNow results in AT MOST ONE additional
        // FetchUsageAsync invocation over the startup fetch.
        int refreshTriggeredFetches = adapter.FetchCount - fetchCountAfterStartup;
        refreshTriggeredFetches.Should().BeLessOrEqualTo(1,
            "R3: double-RefreshNow must NOT spawn two concurrent FetchUsageAsync calls — it should reuse the single in-flight fetch");
    }

    // R3 — single-click contract (post-manual-refresh-leak-fix), on the generalized poller.
    [Fact]
    public async Task RefreshNow_fires_exactly_one_fetch_when_idle()
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
        // Default 10-min interval — the single-click contract must hold on an idle
        // poller with no auto-tick interference (same reasoning as the R3 double test).
        var poller = new ProviderPoller(adapter, keyStore, store);

        // Start the poller (startup fetch), then let it go idle.
        await poller.StartAsync(cts.Token);
        await Task.Delay(300);

        int fetchCountAfterStartup = adapter.FetchCount;

        // Act: a SINGLE manual refresh (not a double-click).
        await poller.RefreshNowAsync();

        await poller.StopAsync(cts.Token);

        // Post-fix truth: one click → exactly one fetch. If the TCS swap regressed (or
        // RefreshNowAsync hung and fell through to the 15s store-Current fallback),
        // this is 0 (or the test takes 15s) — either way the assertion fails.
        int refreshTriggeredFetches = adapter.FetchCount - fetchCountAfterStartup;
        refreshTriggeredFetches.Should().Be(1,
            "post-fix: a single RefreshNow on an idle poller fires exactly ONE fetch");
    }

    // G-01-7 regression — PeriodicTimer single-outstanding-wait, re-pointed to the
    // generalized ProviderPoller (public tick-interval ctor; 400ms interval ⇒ ~7 ticks
    // in the 3s observation window — the RefreshNow click is statistically certain to
    // win the WhenAny race on at least one iteration, hitting the G-01-7 branch).
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
        // PUBLIC tick-interval ctor (the generalized poller's ctors are both public) —
        // short tick so the WhenAny race between the click and the tick is exercised
        // repeatedly inside a fast test.
        var poller = new ProviderPoller(adapter, keyStore, store, new PollIntervalSource(TimeSpan.FromMilliseconds(400)));

        // Start the poller (startup fetch), then let it go idle so the loop is blocked
        // in WaitForNextTickAsync when we fire the click below.
        await poller.StartAsync(cts.Token);
        await Task.Delay(300);

        int fetchCountAfterStartup = adapter.FetchCount;

        // Fire ONE manual refresh WHILE a tick-wait is outstanding. At a 400ms interval
        // the loop is almost always blocked in WaitForNextTickAsync, so the click races
        // the tick — and crucially may WIN, exercising the G-01-7 crash branch.
        Task<UsageReading> refresh = poller.RefreshNowAsync(cts.Token);

        // The click must complete WITHOUT throwing (no InvalidOperationException surfaced
        // to the caller; R3 funneling still works under the new timer lifecycle).
        await refresh;

        // Let at least one (several) short tick(s) fire AND let any faulted continuation
        // surface. At 400ms interval, ~7 ticks fire in 3s. Under the regression, the
        // loop faulted within ~1 tick of the click — ExecuteTask.IsFaulted flips true
        // here and the first assertion below fails.
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        // Observe any fault that surfaced on the background loop's continuation. The
        // 3s delay above gave the threadpool continuation ample time to run. Reading
        // IsFaulted directly (no blocking Wait — xUnit1031). ExecuteTask is non-null
        // once StartAsync has completed (awaited above); the `!` asserts that here.
        poller.ExecuteTask!.IsFaulted.Should().BeFalse(
            "G-01-7: poll loop must NOT fault after a manual refresh during an outstanding PeriodicTimer tick");

        // Survival: the loop must STILL be firing fetches. The refresh fetch itself is
        // +1; subsequent tick fetches prove the loop is alive. Use BeGreaterThan (not Be)
        // because the exact count depends on thread-pool timing and is intentionally
        // non-deterministic — what matters is the loop is STILL firing after the click.
        adapter.FetchCount.Should().BeGreaterThan(fetchCountAfterStartup + 1,
            "the loop must still be alive — at least the refresh fetch AND one subsequent tick fetch must have fired");

        await poller.StopAsync(cts.Token);
    }

    [Fact]
    public async Task D04_on_401_Save_blob_file_does_not_exist()
    {
        // D-04 mandatory test-fetch on save: a 401-returning key is NOT stored. Assert
        // the blob file does NOT exist after the adapter's TestFetchAsync returns
        // NotLoggedIn (which the Save handler branches on).
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var adapter = new ZaiAdapter(new StubFactory(handler));
        using var keyStore = TempKeyStore.Create();

        UsageReading reading = await adapter.TestFetchAsync("candidate-bad-key", CancellationToken.None);

        reading.Status.Should().Be(ReadingStatus.NotLoggedIn,
            "401 → NotLoggedIn (RE-LOGIN); the Save handler MUST NOT call DpapiKeyStore.Protect in this state");
        keyStore.BlobPathExists().Should().BeFalse(
            "the blob file must NOT exist when a 401-returned candidate was rejected (D-04)");
    }

    // WR-04 STALE-vs-ERROR age gate — pure-predicate coverage (UsageStore.IsStale),
    // re-pointed to the per-provider keyed store.
    //
    // nowUtc is injected so the tests are deterministic and fast (no real-time waits).
    // UsageStore.Interval == PollingDefaults.DefaultInterval (10 min); pinned as a local so
    // the assertions stay correct if the interval constant ever changes.
    private static readonly TimeSpan Interval = UsageStore.Interval;

    private static UsageReading OkReading(DateTimeOffset fetchedAt, double usedPct = 30.0) => new(
        Provider: "Z.ai",
        FetchedAtUtc: fetchedAt,
        Status: ReadingStatus.Ok,
        UsedPct: usedPct,
        RemainingPct: 100.0 - usedPct,
        MostBindingWindow: WindowKind.FiveHour,
        AllWindows: Array.Empty<WindowReading>(),
        ErrorMessage: null);

    private static UsageReading ErrorReading(DateTimeOffset fetchedAt) => new(
        Provider: "Z.ai",
        FetchedAtUtc: fetchedAt,
        Status: ReadingStatus.Error,
        UsedPct: null,
        RemainingPct: null,
        MostBindingWindow: default,
        AllWindows: null,
        ErrorMessage: "Couldn't reach Z.ai.");

    [Fact]
    public void WR04_fresh_error_with_prior_success_is_hard_ERROR_not_STALE()
    {
        // The misleading pre-WR-04 behavior: a fresh error immediately rendered STALE.
        // Truth: errorAge ≈ 0 → IsStale is false (the fetch just failed; data not stale).
        var now = DateTimeOffset.UtcNow;
        var store = new UsageStore();
        store.Register("zai");
        store.Update("zai", OkReading(now.AddMinutes(-5)));
        var freshError = ErrorReading(now);

        store.IsStale("zai", freshError, now).Should().BeFalse(
            "a fresh error (errorAge ≈ 0) must render as a hard ERROR, not STALE — " +
            "the data is not stale yet, the fetch just failed");
    }

    [Fact]
    public void WR04_aged_error_past_interval_with_prior_success_is_STALE()
    {
        // Truth: a prior success exists AND errorAge > one interval → STALE.
        var now = DateTimeOffset.UtcNow;
        var store = new UsageStore();
        store.Register("zai");
        store.Update("zai", OkReading(now.AddMinutes(-25)));
        var agedError = ErrorReading(now.AddMinutes(-12)); // errorAge 12 min > 10 min interval

        store.IsStale("zai", agedError, now).Should().BeTrue(
            "an error aged past one interval, with a prior successful reading, must " +
            "render STALE — last-known figure @ 50% opacity");
    }

    [Fact]
    public void WR04_aged_error_without_prior_success_is_hard_ERROR()
    {
        // No prior Ok reading → there is nothing to decay from → never STALE (hard ERROR).
        var now = DateTimeOffset.UtcNow;
        var store = new UsageStore();
        store.Register("zai");
        var agedError = ErrorReading(now.AddMinutes(-30));

        store.IsStale("zai", agedError, now).Should().BeFalse(
            "an aged error with NO prior successful reading must remain a hard ERROR — " +
            "there is no last-known figure to show at 50% opacity");
    }

    [Fact]
    public void WR04_error_exactly_one_interval_old_is_hard_ERROR_strict_inequality()
    {
        // Boundary: errorAge == Interval. The predicate uses strict > (not >=), so an
        // error exactly one interval old is still a hard ERROR — the next scheduled
        // poll may be landing momentarily. Pins the strict-vs-non-strict decision.
        var now = DateTimeOffset.UtcNow;
        var store = new UsageStore();
        store.Register("zai");
        store.Update("zai", OkReading(now.AddMinutes(-20)));
        var boundaryError = ErrorReading(now - Interval); // errorAge exactly == Interval

        store.IsStale("zai", boundaryError, now).Should().BeFalse(
            "errorAge == Interval is NOT stale (strict >): the next poll may be landing now");
    }

    [Fact]
    public void WR04_non_error_reading_is_never_stale()
    {
        // IsStale only applies to Error readings. An Ok/NearLimit/NotLoggedIn/Unsupported
        // reading must never be classified stale regardless of age or prior success.
        var now = DateTimeOffset.UtcNow;
        var store = new UsageStore();
        store.Register("zai");
        store.Update("zai", OkReading(now.AddMinutes(-60)));

        store.IsStale("zai", OkReading(now), now).Should().BeFalse("an Ok reading is never stale");
        // Even an old Ok reading is not stale — staleness is an Error-only state.
        store.IsStale("zai", OkReading(now.AddMinutes(-60)), now).Should().BeFalse();
    }

    [Fact]
    public void WR04_null_reading_is_never_stale()
    {
        var store = new UsageStore();
        store.Register("zai");
        store.Update("zai", OkReading(DateTimeOffset.UtcNow));

        store.IsStale("zai", null).Should().BeFalse("a null reading is never stale");
    }

    // ── Per-slot isolation (D-13/D-14) ─────────────────────────────────────────────

    [Fact]
    public void Per_slot_Changed_fires_only_for_the_updated_slot()
    {
        var store = new UsageStore();
        store.Register("zai");
        store.Register("stub");

        var changedIds = new List<ProviderId>();
        store.Changed += (_, e) => changedIds.Add(e.Id);

        store.Update("zai", OkReading(DateTimeOffset.UtcNow));

        changedIds.Should().Equal(new[] { new ProviderId("zai") },
            "updating slot A must fire Changed(A) only — slot B's subscribers are not invoked");
        store.Current("stub").Should().BeNull("stub's slot Current must be untouched");
        store.LastSuccessful("stub").Should().BeNull("stub's slot LastSuccessful must be untouched");
        store.Current("zai").Should().NotBeNull();
        store.LastSuccessful("zai").Should().NotBeNull();
    }

    [Fact]
    public void Per_slot_IsStale_ignores_another_providers_prior_success()
    {
        // WR-04 per-slot: a second provider's prior success must NOT make the first's
        // error stale — each slot's LastSuccessful is independent.
        var now = DateTimeOffset.UtcNow;
        var store = new UsageStore();
        store.Register("zai");
        store.Register("stub");

        // Only the stub has a prior success; zai has none.
        store.Update("stub", new UsageReading(
            Provider: "Stub",
            FetchedAtUtc: now.AddMinutes(-5),
            Status: ReadingStatus.Ok,
            UsedPct: 50.0,
            RemainingPct: 50.0,
            MostBindingWindow: WindowKind.FiveHour,
            AllWindows: Array.Empty<WindowReading>(),
            ErrorMessage: null));

        var agedError = ErrorReading(now.AddMinutes(-12)); // errorAge 12 min > 10 min interval
        store.IsStale("zai", agedError, now).Should().BeFalse(
            "the stub's prior success must NOT make zai's error stale — per-slot LastSuccessful only");

        // With its own prior success, the same error IS stale.
        store.Update("zai", OkReading(now.AddMinutes(-25)));
        store.IsStale("zai", agedError, now).Should().BeTrue(
            "once zai has its own prior success, its aged error is stale (per-slot gate)");
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
            name.Should().Be(HttpExtensions.ZaiClientName);
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
            string tempDir = Path.Combine(Path.GetTempPath(), "planmeter-stale-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string blobPath = Path.Combine(tempDir, "zai.key.bin");
            return new TempKeyStore(blobPath, tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
