using System;
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
/// D-05 (04-02 Task 3) — the poller SESSION-PARK: a Session-family adapter whose fetch
/// returns NotLoggedIn against a PRESENT credential (the credential existed but the
/// provider rejected it — an expired OAuth session) stops polling FOR THE SESSION.
/// Distinct from the config-backed disable in every dimension:
/// <list type="bullet">
///   <item><see cref="ProviderPoller.SessionParked"/> stays true while
///   <c>poller.IsEnabled</c> stays TRUE (the enable flag is untouched — CONF-02).</item>
///   <item>Never persisted: a NEW poller instance over the same adapter + store polls
///   again on construction (restart clears the park).</item>
///   <item>A refresh signal must NOT resume it (terminal for the session).</item>
///   <item>Key-family NotLoggedIn does NOT park (Z.ai semantics unchanged — NO KEY
///   keeps its timestamp because polls continue).</item>
/// </list>
/// </summary>
public sealed class ProviderPollerSessionParkTests
{
    /// <summary>Polls <paramref name="condition"/> until true or the timeout elapses.</summary>
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

    // Test 1 — the park engages: a Session adapter returning NotLoggedIn with a
    // PRESENT credential performs NO further scheduled polls. A 200ms tick source that
    // would fire several times inside the window yields exactly ONE fetch (startup).
    [Fact]
    public async Task Session_expired_credential_parks_the_poller_after_one_fetch()
    {
        var adapter = new SessionFakeAdapter { CredentialPresent = true };
        var store = new UsageStore();
        store.Register("sess");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);

        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the startup fetch must run (and return NotLoggedIn)");

        // Several tick windows elapse — a parked poller must not fetch again.
        await Task.Delay(1200);
        adapter.FetchCount.Should().Be(1,
            "after the session-park engages (NotLoggedIn from a Session-family fetch with a present credential), NO further scheduled polls may run for the session");
        poller.SessionParked.Should().BeTrue(
            "the park flag must be observable on the poller");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // Test 2 — the park is NOT the config disable: IsEnabled stays true, and the
    // config-backed disable path still works on a parked poller (distinct states).
    [Fact]
    public async Task Session_park_does_not_touch_the_config_backed_enable_flag()
    {
        var adapter = new SessionFakeAdapter { CredentialPresent = true };
        var store = new UsageStore();
        store.Register("sess");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => poller.SessionParked, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the session-park must engage");

        poller.IsEnabled.Should().BeTrue(
            "the session-park is DISTINCT from the config-backed disable — the enable flag is untouched (CONF-02)");
        poller.Id.Should().Be(new ProviderId("sess"));

        // The config-backed disable path still functions on a parked poller.
        poller.SetEnabled(false);
        poller.IsEnabled.Should().BeFalse("the config-backed disable still works");

        await poller.StopAsync(cts.Token);
    }

    // Test 3 — restart-cleared: a NEW poller over the same adapter + store polls again
    // (the park is per-instance, never persisted anywhere).
    [Fact]
    public async Task Session_park_is_cleared_by_restart()
    {
        var adapter = new SessionFakeAdapter { CredentialPresent = true };
        var store = new UsageStore();
        store.Register("sess");
        using var cts = new CancellationTokenSource();

        var first = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));
        await first.StartAsync(cts.Token);
        (await WaitUntilAsync(() => first.SessionParked, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the first instance parks");
        int countAfterFirst = adapter.FetchCount;
        await first.StopAsync(cts.Token);

        // A NEW instance over the SAME adapter + store polls again on construction —
        // the park lives on the poller instance, not in config.json or the store.
        var second = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));
        await second.StartAsync(cts.Token);
        (await WaitUntilAsync(() => adapter.FetchCount > countAfterFirst, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("a NEW poller instance must poll again — the session-park is never persisted");
        second.SessionParked.Should().BeTrue("the new instance parks again after its own expired-session fetch");
        await second.StopAsync(cts.Token);
    }

    // Test 4 — Key-family NotLoggedIn does NOT park: Z.ai semantics unchanged (NO KEY
    // keeps polling on schedule — a later key save must be picked up without restart).
    [Fact]
    public async Task Key_family_NotLoggedIn_keeps_polling_on_schedule()
    {
        var adapter = new KeyFakeNotLoggedInAdapter();
        var store = new UsageStore();
        store.Register("keyfam");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new NullCredentialSource(), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);

        (await WaitUntilAsync(() => adapter.FetchCount >= 3, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("a Key-family adapter returning NotLoggedIn must KEEP polling (the NO KEY state is not terminal)");
        poller.SessionParked.Should().BeFalse(
            "the session-park condition is (NotLoggedIn AND Session-family AND credential-present) — a Key-family fetch never parks");

        await poller.StopAsync(cts.Token);
    }

    // Test 5 — the parked reading carries the discriminator: SessionParked transitions
    // to true ONLY on (NotLoggedIn AND Session-family AND credential-present). The two
    // failure combinations must NOT park.
    [Fact]
    public async Task Session_family_without_credential_does_not_park()
    {
        // Session family + NotLoggedIn + NO credential (NullCredentialSource) — the
        // never-detected shape: the poller keeps polling (no park; the row renders
        // NO LOGIN, and detection landing later is picked up).
        var adapter = new SessionFakeAdapter { CredentialPresent = false };
        var store = new UsageStore();
        store.Register("sess");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new NullCredentialSource(), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);

        (await WaitUntilAsync(() => adapter.FetchCount >= 3, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("a session adapter with NO credential present must keep polling (NO LOGIN is not terminal)");
        poller.SessionParked.Should().BeFalse(
            "SessionParked must transition true ONLY on (NotLoggedIn AND Session-family AND credential-present) — no credential, no park");

        // The store holds a NotLoggedIn reading either way (the render reads the
        // reading shape + Detect(), not the park flag itself).
        store.Current("sess")!.Status.Should().Be(ReadingStatus.NotLoggedIn);

        await poller.StopAsync(cts.Token);
    }

    // D-05 un-park (10-03) — login revives a parked poller without a process restart.
    [Fact]
    public async Task ClearSessionPark_revives_a_parked_poller_without_restart()
    {
        var adapter = new SessionFakeAdapter { CredentialPresent = true };
        var store = new UsageStore();
        store.Register("sess");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => poller.SessionParked, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the session-park must engage before un-park");
        int countAfterPark = adapter.FetchCount;
        countAfterPark.Should().BeGreaterThanOrEqualTo(1);

        poller.ClearSessionPark();

        (await WaitUntilAsync(() => adapter.FetchCount > countAfterPark, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("ClearSessionPark must revive the parked poller and perform a fetch with no restart");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // D-05 pin — a refresh signal alone must NOT revive (Codex terminal park).
    [Fact]
    public async Task Refresh_signal_alone_does_NOT_revive_a_parked_poller()
    {
        var adapter = new SessionFakeAdapter { CredentialPresent = true };
        var store = new UsageStore();
        store.Register("sess");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => poller.SessionParked, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("the session-park must engage");
        int countAfterPark = adapter.FetchCount;

        // RefreshNowAsync completes the refresh signal. A parked poller must re-arm,
        // not fetch. The call times out waiting for an in-flight slot that never appears.
        using var refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await poller.RefreshNowAsync(refreshCts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected — the park does not claim the in-flight slot
        }

        await Task.Delay(400);
        adapter.FetchCount.Should().Be(countAfterPark,
            "a refresh signal alone must NOT revive a session-parked poller (Codex terminal semantics)");
        poller.SessionParked.Should().BeTrue();

        await poller.StopAsync(cts.Token);
    }

    // D-05 — ParkSession stops scheduled fetches; ClearSessionPark resumes them.
    [Fact]
    public async Task ParkSession_parks_immediately_and_is_terminal_until_unpark()
    {
        var adapter = new SessionOkAdapter();
        var store = new UsageStore();
        store.Register("sessok");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMilliseconds(200)));

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("startup fetch must run");

        poller.ParkSession();
        poller.SessionParked.Should().BeTrue();
        int countAfterPark = adapter.FetchCount;

        await Task.Delay(1200);
        adapter.FetchCount.Should().Be(countAfterPark,
            "ParkSession must stop scheduled fetches until ClearSessionPark");

        poller.ClearSessionPark();
        (await WaitUntilAsync(() => adapter.FetchCount > countAfterPark, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("ClearSessionPark must resume fetches after ParkSession");

        await poller.StopAsync(cts.Token);
        poller.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    // D-05 — ClearSessionPark on a running unparked poller is a silent no-op.
    [Fact]
    public async Task ClearSessionPark_is_idempotent_on_an_unparked_poller()
    {
        var adapter = new SessionOkAdapter();
        var store = new UsageStore();
        store.Register("sessok");
        using var cts = new CancellationTokenSource();
        var poller = new ProviderPoller(
            adapter, new FixedCredentialSource("session-token"), store,
            new PollIntervalSource(TimeSpan.FromMinutes(10)));

        await poller.StartAsync(cts.Token);
        (await WaitUntilAsync(() => adapter.FetchCount >= 1, TimeSpan.FromSeconds(3)))
            .Should().BeTrue("startup fetch must run");
        int countAfterStartup = adapter.FetchCount;

        poller.ClearSessionPark();
        poller.ClearSessionPark();

        await Task.Delay(400);
        adapter.FetchCount.Should().Be(countAfterStartup,
            "ClearSessionPark on an unparked poller is a silent no-op — no extra fetch");
        poller.SessionParked.Should().BeFalse();

        await poller.StopAsync(cts.Token);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Fixtures
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A Session-family fake whose fetch classifies from the credential-present signal:
    /// credential present + rejected-by-provider shape → NotLoggedIn (the expired-session
    /// reading that parks the poller); no credential → NotLoggedIn (never-detected —
    /// does NOT park because the source returns null).
    /// </summary>
    private sealed class SessionFakeAdapter : IProviderAdapter
    {
        private int _fetchCount;

        public ProviderId Id => "sess";
        public string DisplayName => "Session";
        public bool SupportsUsageApi => true;
        public bool RequiresManualKey => false;
        public bool ManualOnlyFetch => false;
        public bool SupportsOAuthLogin => false;
        public AuthFamily AuthFamily => AuthFamily.Session;
        public string? ConsoleUrl => null;
        public string? QualifierText => null;
        public string? UnsupportedReason => null;
        public string? FloorReason => null;
        public string? ReLoginGuidance => null;
        public bool Detect() => CredentialPresent;
        public int FetchCount => Volatile.Read(ref _fetchCount);

        /// <summary>Whether the local session credential is present (Detect()'s answer).</summary>
        public volatile bool CredentialPresent;

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(Classify(null));

        public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fetchCount);
            return Task.FromResult(new AdapterFetchResult(Classify(apiKey), null));
        }

        private UsageReading Classify(string? credential) => new(
            Provider: "Session",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.NotLoggedIn,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: null);
    }

    /// <summary>
    /// A Session-family fake that returns Ok — used to exercise ParkSession / ClearSessionPark
    /// without the auto-park that a NotLoggedIn+credential-present fetch would trigger.
    /// </summary>
    private sealed class SessionOkAdapter : IProviderAdapter
    {
        private int _fetchCount;

        public ProviderId Id => "sessok";
        public string DisplayName => "SessionOk";
        public bool SupportsUsageApi => true;
        public bool RequiresManualKey => false;
        public bool ManualOnlyFetch => false;
        public bool SupportsOAuthLogin => false;
        public AuthFamily AuthFamily => AuthFamily.Session;
        public string? ConsoleUrl => null;
        public string? QualifierText => null;
        public string? UnsupportedReason => null;
        public string? FloorReason => null;
        public string? ReLoginGuidance => null;
        public bool Detect() => true;
        public int FetchCount => Volatile.Read(ref _fetchCount);

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(Ok());

        public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fetchCount);
            return Task.FromResult(new AdapterFetchResult(Ok(), null));
        }

        private static UsageReading Ok() => new(
            Provider: "SessionOk",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Ok,
            UsedPct: 10,
            RemainingPct: 90,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: null);
    }

    /// <summary>A Key-family fake that always returns NotLoggedIn (Z.ai's NO KEY shape).</summary>
    private sealed class KeyFakeNotLoggedInAdapter : IProviderAdapter
    {
        private int _fetchCount;

        public ProviderId Id => "keyfam";
        public string DisplayName => "KeyFam";
        public bool SupportsUsageApi => true;
        public bool RequiresManualKey => true;
        public bool ManualOnlyFetch => false;
        public bool SupportsOAuthLogin => false;
        public AuthFamily AuthFamily => AuthFamily.Key;
        public string? ConsoleUrl => null;
        public string? QualifierText => null;
        public string? UnsupportedReason => null;
        public string? FloorReason => null;
        public string? ReLoginGuidance => null;
        public bool Detect() => false;
        public int FetchCount => Volatile.Read(ref _fetchCount);

        public Task<UsageReading> TestFetchAsync(string? apiKey, CancellationToken ct = default)
            => Task.FromResult(NotLoggedIn());

        public Task<AdapterFetchResult> FetchUsageAsync(string? apiKey, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fetchCount);
            return Task.FromResult(new AdapterFetchResult(NotLoggedIn(), null));
        }

        private static UsageReading NotLoggedIn() => new(
            Provider: "KeyFam",
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.NotLoggedIn,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: null);
    }

    /// <summary>An in-memory credential source returning a fixed token (or null).</summary>
    private sealed class FixedCredentialSource : ICredentialSource
    {
        private readonly string? _token;
        public FixedCredentialSource(string? token) { _token = token; }

        public Task<string?> ReadFreshAsync(CancellationToken ct = default)
            => Task.FromResult(_token);
    }
}
