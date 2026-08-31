using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Credentials;
using PlanMeter.Core.Http;
using PlanMeter.Core.Models;
using PlanMeter.Core.Store;

namespace PlanMeter.Core.Polling;

/// <summary>
/// PROV-02/D-15 — the generalized ONE-PER-PROVIDER poller. The proven Phase-1 loop from
/// ZaiPollingService, parameterized over the <see cref="IProviderAdapter"/>, its
/// <see cref="ICredentialSource"/>, and its <see cref="UsageStore"/> slot. Each provider
/// runs its OWN instance: own <see cref="PeriodicTimer"/>, own refresh signal, own
/// in-flight slot, own backoff state — isolation is structural, not a discipline (SC#2).
/// </summary>
/// <remarks>
/// The three load-bearing invariants are preserved VERBATIM from the Phase-1 poller:
/// <list type="bullet">
///   <item><b>Swappable-TCS refresh signal</b> (260811-l9h) — a counting-semaphore
///   WaitAsync enqueues a FIFO waiter that LEAKS when it loses Task.WhenAny to the tick.
///   An uncompleted Task registers nothing.</item>
///   <item><b>Per-iteration <see cref="PeriodicTimer"/> recreate</b> (260811-ohm / G-01-7) —
///   the nested <c>using</c> retires the pending WaitForNextTickAsync before the next
///   timer is created; at most one outstanding wait ever exists.</item>
///   <item><b><c>_inFlight</c> funneling</b> (R3) — a rapid double-RefreshNow collapses
///   into a single FetchUsageAsync via Interlocked.CompareExchange.</item>
/// </list>
/// Plan 02-04 adds the conservative refresh backbone (REFRESH-01/REFRESH-03):
/// <list type="bullet">
///   <item><b>Per-poll baseline jitter</b> (D-22) — every tick's delay is the base interval
///   scaled by a random factor in [0.6, 1.4], so N providers never fire synchronously.</item>
///   <item><b>429/Retry-After cross-tick backoff</b> (D-21) — a fetch whose
///   <see cref="AdapterFetchResult.RetryAfter"/> is set extends ONLY THIS poller's NEXT
///   tick to <c>now + min(RetryAfter, 2h ceiling)</c>. There is NO in-tick retry (Polly
///   stays &lt;=1 transient retry with no 429 retry). Backoff is per-instance
///   (<c>_backoffUntilUtc</c>), never cleared by a refresh-triggered poll, and cleared
///   ONLY by a later scheduled auto-poll returning no RetryAfter (REFRESH-03/idempotency).</item>
/// </list>
/// Generalization: the poller NO LONGER short-circuits an empty key to NotLoggedIn
/// itself — it ALWAYS calls the adapter, which classifies (Z.ai returns NotLoggedIn on
/// an empty key via its guard; Demo ignores the key). One poller serves both manual-key
/// and keyless adapters.
///
/// D-06 (Plan 03-03) — the enabled-flag park: a mutable <see cref="_enabled"/> flag the
/// loop checks at the top of EVERY iteration. While disabled the loop creates NO
/// <see cref="PeriodicTimer"/> and performs NO fetch — it parks on the swappable-TCS
/// refresh signal (or the host stop token) until re-enabled. Re-enable = set the flag +
/// <see cref="RefreshNowAsync"/> → the immediate first fetch is funneled through the
/// existing R3 path, NEVER a raw <see cref="BackgroundService"/> restart (Pitfall 2 — the
/// service is registered via the AddSingleton + AddHostedService double-register and is
/// not restart-safe). The three load-bearing invariants survive unchanged in the non-park
/// branch.
/// </remarks>
public sealed class ProviderPoller : BackgroundService
{
    private readonly IProviderAdapter _adapter;
    private readonly ICredentialSource _keySource;
    private readonly UsageStore _store;
    private readonly ILogger<ProviderPoller>? _logger;

    /// <summary>
    /// CONF-03/D-13/D-14 (03-04) — the shared, single-global interval source
    /// <c>ComputeNextDelay()</c> re-reads EVERY tick. Replaces the Phase-2 ctor-frozen
    /// tick interval: a settings-window change to the shared source applies on the
    /// NEXT scheduled poll, with NO restart and NO immediate fetch (SC#3). Defaults to
    /// <see cref="PollingDefaults.DefaultInterval"/> (10 min, D-17) on the convenience
    /// constructor. The interval-source constructor is PUBLIC (not internal) because
    /// PlanMeter.Core grants InternalsVisibleTo ONLY to PlanMeter.Core.Tests — an internal
    /// ctor would fail <c>dotnet build PlanMeter.sln</c> with CS0122 (PlanMeter.App calls
    /// this ctor directly with the configured interval source).
    /// </summary>
    private readonly PollIntervalSource _intervalSource;

    /// <summary>
    /// D-22 — the RNG behind the per-poll baseline jitter. Injected through the
    /// tick-interval constructor (tests pass a fixed-seed <see cref="Random"/> for
    /// deterministic jitter assertions); production defaults to <see cref="Random.Shared"/>.
    /// </summary>
    private readonly Random _random;

    /// <summary>
    /// D-13 — the machine identity: the key of the store / registry / poller slot. The
    /// settings-window save path (Plan 03-02) resolves the SINGLE refresh target by this
    /// id — <c>_pollers.FirstOrDefault(p => p.Id == id)</c> — and 03-03's enable/disable
    /// toggle resolves the poller the same way. Defined HERE (wave 2) so this wave compiles
    /// on its own; 03-03 must NOT re-add it — the member exists exactly once in the phase.
    /// </summary>
    public ProviderId Id => _adapter.Id;

    /// <summary>
    /// D-06 — the mutable enabled flag. Defaults to TRUE; the App DI factory reconciles it
    /// with the registry's startup enabled-set (a provider disabled in config.json must not
    /// poll after restart, D-05). Read on the poll loop task; written by the settings-window
    /// toggle handler (UI thread). <see cref="SetEnabled(bool)"/> is the only writer.
    /// </summary>
    private volatile bool _enabled = true;

    /// <summary>D-06 — whether this poller is currently enabled (parks while false).</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// D-05 (04-02, 10-03) — the SESSION-PARK flag: set when a Session-family fetch returns a
    /// NotLoggedIn reading against a PRESENT credential (the credential existed but the
    /// provider rejected it — an expired OAuth session), or by <see cref="ParkSession"/>
    /// (Grok logout). A session-parked poller performs no further fetches until
    /// <see cref="ClearSessionPark"/> or a process restart. A refresh signal alone does
    /// NOT resume it (Codex terminal park — repeated 401s every 10 min into risk-control
    /// are the hazard this prevents, T-04-06). Grok login calls ClearSessionPark so the
    /// PlanMeter-owned credential can revive the row without a restart.
    ///
    /// DISTINCT from the config-backed <see cref="_enabled"/> park in every dimension:
    /// never persisted (a restart constructs a fresh poller that polls again), never
    /// toggled by <see cref="SetEnabled"/> (the enable flag is untouched — CONF-02), and
    /// only reachable for <see cref="AuthFamily.Session"/> adapters (a Key-family
    /// NotLoggedIn keeps polling: the NO KEY state must pick up a later key save).
    /// </summary>
    private volatile bool _sessionParked;

    /// <summary>
    /// D-05 — whether this poller is session-parked (an expired OAuth session stopped it
    /// for the session). The named F4 render discriminator's poller-side source; the row
    /// itself classifies from the terminal reading shape + <see cref="IProviderAdapter.Detect"/>.
    /// </summary>
    public bool SessionParked => _sessionParked;

    /// <summary>
    /// D-06 — set the enabled flag. A false→true transition ALSO triggers the immediate
    /// first fetch via <see cref="RefreshNowAsync"/> — funneled through the existing R3
    /// in-flight path, never a raw <see cref="ExecuteAsync"/> restart (Pitfall 2). While
    /// disabled the loop parks (no timer, no fetch) and <see cref="PollOnceAsync"/> early-
    /// returns. An idempotent set (enabled→true or disabled→false) is a no-op — it does
    /// NOT trigger a fetch.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        bool wasEnabled = _enabled;
        _enabled = enabled;
        if (enabled && !wasEnabled)
        {
            _ = RefreshNowAsync();
        }
    }

    /// <summary>
    /// D-05 (10-03) — clear a session-park and trigger the immediate fetch. Mirrors
    /// <see cref="SetEnabled"/>: the flag flips FIRST, then <see cref="RefreshNowAsync"/>
    /// completes the refresh signal so the park-branch wait wakes. A false→false call
    /// (already unparked) is a silent no-op — no extra fetch. A refresh signal alone
    /// must NOT revive a parked poller (Codex terminal park stays intact).
    /// </summary>
    public void ClearSessionPark()
    {
        bool wasParked = _sessionParked;
        _sessionParked = false;
        if (wasParked)
        {
            _ = RefreshNowAsync();
        }
    }

    /// <summary>
    /// D-05 (10-03) — park this poller live (Grok logout). Terminal until
    /// <see cref="ClearSessionPark"/> or a process restart. Does not touch
    /// <see cref="_enabled"/> (CONF-02).
    /// </summary>
    public void ParkSession()
    {
        _sessionParked = true;
    }

    /// <summary>
    /// D-21/REFRESH-03 — when a fetch returned 429/<c>Retry-After</c>, the UTC instant
    /// before which this poller's NEXT scheduled auto-poll must NOT fire. PER-INSTANCE
    /// (never static) so one provider's backoff never leaks into another provider's
    /// cadence (REFRESH-03/concurrency). Read/written only on the poll loop task.
    /// Reset to null ONLY by a later scheduled auto-poll that returns no RetryAfter —
    /// a refresh-triggered poll NEVER clears it (REFRESH-03/idempotency).
    /// </summary>
    private DateTimeOffset? _backoffUntilUtc;

    /// <summary>
    /// R3 funneling state. When a fetch is in-flight, <c>_inFlight</c> holds the
    /// TaskCompletionSource that both the timer tick AND any manual RefreshNow caller
    /// await — a rapid double-click reuses the SAME task, never spawns a second fetch.
    /// </summary>
    private TaskCompletionSource<UsageReading>? _inFlight;

    /// <summary>
    /// The refresh-now signal as a SWAPPABLE <see cref="TaskCompletionSource{TResult}"/>.
    /// Completed by <see cref="RefreshNowAsync"/> via idempotent <c>TrySetResult</c>;
    /// awaited alongside the <see cref="PeriodicTimer"/> so the poller wakes immediately
    /// on a manual refresh. See the class doc for why a swappable TCS and not a
    /// counting-semaphore primitive (260811-l9h).
    /// </summary>
    private TaskCompletionSource<bool> _refreshSignal = CreateRefreshSignal();

    /// <summary>
    /// Factory for the refresh-now signal. <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>
    /// ensures <see cref="RefreshNowAsync"/>'s caller (the UI thread, typically) does
    /// NOT run the poll loop's continuation synchronously under its own stack when it
    /// calls <see cref="TaskCompletionSource{TResult}.TrySetResult"/>.
    /// </summary>
    private static TaskCompletionSource<bool> CreateRefreshSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Convenience constructor: default interval source
    /// (<see cref="PollingDefaults.DefaultInterval"/>, 10 min).
    /// </summary>
    public ProviderPoller(
        IProviderAdapter adapter,
        ICredentialSource keySource,
        UsageStore store,
        ILogger<ProviderPoller>? logger = null)
        : this(adapter, keySource, store, new PollIntervalSource(PollingDefaults.DefaultInterval), logger)
    {
    }

    /// <summary>
    /// The production AND test-injection constructor (both are public — see the
    /// <c>_intervalSource</c> doc for why). Production (App.xaml.cs) passes the shared
    /// <see cref="PollIntervalSource"/> singleton (D-14 — ALL pollers share ONE instance);
    /// the G-01-7 regression test passes a short-interval source so the WhenAny-race crash
    /// branch is exercised without a 10-min wait. <c>ComputeNextDelay()</c> re-reads
    /// <c>intervalSource.Current</c> per tick (D-13), so the value may change live.
    /// </summary>
    public ProviderPoller(
        IProviderAdapter adapter,
        ICredentialSource keySource,
        UsageStore store,
        PollIntervalSource intervalSource,
        ILogger<ProviderPoller>? logger = null,
        Random? random = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _keySource = keySource ?? throw new ArgumentNullException(nameof(keySource));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _intervalSource = intervalSource ?? throw new ArgumentNullException(nameof(intervalSource));
        _logger = logger;
        _random = random ?? Random.Shared;
    }

    /// <summary>
    /// R3 — manual refresh. If a fetch is already in-flight, this returns the SAME
    /// in-flight task (no parallel request is spawned). Otherwise it signals the poll
    /// loop to wake up immediately and perform ONE fetch (collapsing any rapid
    /// double-click into a single request).
    ///
    /// There is no 60-second global rate-limit in this plan (REFRESH-02's gate is
    /// Plan 02-03). The funneling IS required so a rapid double-click does not produce
    /// two concurrent provider requests.
    /// </summary>
    public async Task<UsageReading> RefreshNowAsync(CancellationToken ct = default)
    {
        // R3: if a fetch is currently in-flight, hand the caller the same task. We MUST
        // NOT spawn a second FetchUsageAsync on a concurrent trigger.
        var existing = Volatile.Read(ref _inFlight);
        if (existing is not null)
        {
            return await existing.Task.ConfigureAwait(false);
        }

        // Otherwise, signal the poll loop to wake up immediately and perform a fetch.
        // The poll loop creates the TaskCompletionSource before invoking the adapter,
        // so any subsequent RefreshNowAsync calls during the fetch will reuse it.
        // TrySetResult is IDEMPOTENT: a rapid double-click on the SAME TCS collapses
        // to a single completion (R3 funneling — no drain loop needed; TCS has no count).
        Volatile.Read(ref _refreshSignal).TrySetResult(true);

        // Wait briefly for the poll loop to claim the in-flight slot. If the poll loop
        // is busy with a timer-tick fetch, that fetch becomes the in-flight task and we
        // reuse it; if it's idle, it claims the slot and runs the fetch now.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(RefreshWaitTimeoutSeconds));
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var inflight = Volatile.Read(ref _inFlight);
                if (inflight is not null)
                {
                    return await inflight.Task.ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout waiting for the poll loop to claim the slot — return whatever the
            // store currently holds (defensive; the poll loop will still publish when it
            // completes, so the UI will update anyway via UsageStore.Changed).
            return _store.Current(_adapter.Id) ?? LoadingReading(_adapter.DisplayName);
        }
    }

    /// <summary>How long RefreshNowAsync waits for the poll loop to claim the in-flight slot (mirrors the named-client timeout).</summary>
    private const int RefreshWaitTimeoutSeconds = 15;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // G-04-4 liveness (04-06) — the loop-entered line is the REPRO INSTRUMENT: a
        // poller whose loop never runs (the UAT minimax silence) is detectable in the
        // app log by this line's ABSENCE. SEC-03: provider id + event + flag only —
        // never a URL, header, body, or key fragment.
        _logger?.LogInformation(
            "poller loop entered (provider={ProviderId}, enabled={Enabled})", Id, _enabled);

        // First fetch on startup — if a key is available, fetch immediately so the
        // user sees a fresh reading on launch (D-04 success → RefreshNowAsync is also
        // called by the Save handler, but a launch-fetch covers the case where the
        // key was already stored from a prior session). Neither scheduled nor manual:
        // it never clears an outstanding backoff (there is none at startup).
        // D-06 — a poller seeded disabled (config.json enabledProviders) skips the
        // startup fetch entirely.
        if (_enabled && !_adapter.ManualOnlyFetch)
        {
            await PollOnceAsync(isScheduledAutoPoll: false, stoppingToken).ConfigureAwait(false);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // D-05 session-park (04-02 / 10-03) — TERMINAL for the session unless
                // ClearSessionPark flipped the flag. Mirrors the disabled-park idiom:
                // wait on the refresh signal AND the host stop token. If the SIGNAL won
                // and _sessionParked is STILL true → swap the signal and continue
                // (re-park — a global Refresh fan-out must not revive). If the signal
                // won and ClearSessionPark already cleared the flag → leave the
                // completed signal for the normal branch to consume as the immediate
                // fetch (D-08). Stop-token win → break, unchanged.
                if (_sessionParked)
                {
                    // G-04-4 liveness — the terminal session-park is visible in the log
                    // (SEC-03: id + event only).
                    _logger?.LogInformation("poller session-parked (provider={ProviderId})", Id);

                    TaskCompletionSource<bool> sessionParkSignal = Volatile.Read(ref _refreshSignal);
                    Task sessionParkSignalTask = sessionParkSignal.Task;
                    var sessionStopSource = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    using (stoppingToken.Register(() => sessionStopSource.TrySetResult(true)))
                    {
                        Task sessionParkWinner = await Task.WhenAny(sessionParkSignalTask, sessionStopSource.Task)
                            .ConfigureAwait(false);

                        if (sessionParkWinner == sessionStopSource.Task)
                        {
                            break;
                        }

                        if (_sessionParked)
                        {
                            Interlocked.CompareExchange(ref _refreshSignal, CreateRefreshSignal(), sessionParkSignal);
                        }
                    }

                    continue;
                }

                // GRND-02/D-01 — ManualOnlyFetch park: a manual-only provider never
                // creates a PeriodicTimer and never fetches on schedule. It parks
                // on the refresh signal (explicit RefreshNowAsync) and the stop token.
                // On signal-won it performs ONE fetch then re-parks. On stop-token it
                // breaks (clean shutdown). This branch sits AFTER session-park and
                // BEFORE the disabled-park so that D-03 flag composition holds:
                // enabled=false still parks harder, _sessionParked is still terminal.
                if (_adapter.ManualOnlyFetch)
                {
                    _logger?.LogInformation("poller manual-only park (provider={ProviderId})", Id);

                    TaskCompletionSource<bool> parkSignal = Volatile.Read(ref _refreshSignal);
                    Task parkSignalTask = parkSignal.Task;
                    var stopSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (stoppingToken.Register(() => stopSource.TrySetResult(true)))
                    {
                        Task parkWinner = await Task.WhenAny(parkSignalTask, stopSource.Task).ConfigureAwait(false);

                        // Signal-swap discipline (prevents CPU busy-spin): if the
                        // signal won and we're still manual-only, swap in a fresh TCS.
                        if (parkWinner != stopSource.Task && _adapter.ManualOnlyFetch)
                        {
                            Interlocked.CompareExchange(ref _refreshSignal, CreateRefreshSignal(), parkSignal);
                        }

                        if (parkWinner == stopSource.Task)
                        {
                            break;
                        }

                        // Signal-won: perform exactly one fetch on the explicit user
                        // signal, then re-park (continue re-enters the loop top).
                        await PollOnceAsync(isScheduledAutoPoll: false, stoppingToken).ConfigureAwait(false);
                    }

                    continue;
                }

                // D-06 — the enabled-flag park. While disabled the loop creates NO
                // PeriodicTimer and performs NO fetch: it parks on the swappable-TCS
                // refresh signal (completed by a re-enable's RefreshNowAsync → the loop
                // wakes, `continue` re-checks _enabled, and the signal-won branch runs the
                // immediate first fetch, D-06) OR the host stop token (so a parked poller
                // still exits cleanly on shutdown — the park is not cancellation-blind).
                // `continue` re-evaluates _enabled (now true) and the normal branch runs.
                if (!_enabled)
                {
                    // G-04-4 liveness — park entry visible in the log (SEC-03: id + event).
                    _logger?.LogInformation("poller parked (provider={ProviderId})", Id);

                    // CS0136 note: the locals are named parkSignal/parkSignalTask (not
                    // signal/signalTask) because the non-park branch below declares the
                    // loop-body `signal` + `signalTask` locals in the SAME while-body
                    // scope — reusing those names here would be a shadowing error.
                    TaskCompletionSource<bool> parkSignal = Volatile.Read(ref _refreshSignal);
                    Task parkSignalTask = parkSignal.Task;
                    var stopSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (stoppingToken.Register(() => stopSource.TrySetResult(true)))
                    {
                        Task parkWinner = await Task.WhenAny(parkSignalTask, stopSource.Task).ConfigureAwait(false);

                        // The signal completing while STILL disabled is a refresh that cannot
                        // run (PollOnceAsync early-returns). Swap in a fresh TCS (only the
                        // loop swaps — the established discipline) so the NEXT park iteration
                        // parks on an uncompleted signal instead of a completed one: without
                        // the swap, WhenAny returns instantly forever → a CPU busy-spin (the
                        // widget-global Refresh now fans out to every poller, disabled ones
                        // included). A re-enable's SetEnabled(true) sets _enabled BEFORE
                        // signaling, so the park observes _enabled == true here and does NOT
                        // swap — the completed signal is left for the non-park signal-won
                        // branch to consume as the D-06 immediate first fetch.
                        if (parkWinner != stopSource.Task && !_enabled)
                        {
                            Interlocked.CompareExchange(ref _refreshSignal, CreateRefreshSignal(), parkSignal);
                        }
                    }

                    if (_enabled)
                    {
                        // G-04-4 liveness — park EXIT via re-enable (the flag flipped while
                        // parked; the next iteration's non-park branch consumes the signal).
                        _logger?.LogInformation("poller unparked (provider={ProviderId})", Id);
                    }

                    continue;
                }

                // G-01-7 fix: recreate the PeriodicTimer EACH iteration via a nested `using`.
                // Why per-iteration (not one long-lived timer above the loop):
                //  - Provable single-outstanding-wait. PeriodicTimer allows ONLY ONE
                //    outstanding WaitForNextTickAsync at a time. On the signal-won branch
                //    below we deliberately do NOT await tickTask (that would block the
                //    RefreshNow click for up to the remaining tick window). The prior
                //    iteration's `using var timer` goes out of scope at the closing brace
                //    of THIS loop body, which Disposes the prior PeriodicTimer and reaps
                //    its pending WaitForNextTickAsync task (completes as canceled/disposed)
                //    BEFORE the new timer is created on the next iteration. At most one
                //    timer, hence at most one outstanding wait, ever exists. This is the
                //    load-bearing single-outstanding-wait guarantee.
                //  - Symmetric with the TCS-swap below (the signal is also re-read each
                //    iteration via Volatile.Read). The recreate-per-iteration structure is
                //    the established idiom here.
                //  - Cheap: PeriodicTimer is a small managed object; one alloc per tick
                //    is negligible for an always-on widget.
                //
                // Plan 02-04 (D-21/D-22): the per-iteration PERIOD is now a computed delay
                // — the base tick scaled by the human jitter factor (EVERY tick) and
                // extended to at least the outstanding 429 backoff remaining (the backoff
                // dominates the jittered baseline, so the NEXT tick is pushed out — never
                // retried in-tick). The single-outstanding-wait guarantee is unchanged.
                using var timer = new PeriodicTimer(ComputeNextDelay());

                // Await EITHER the next tick OR a manual refresh-now signal. The manual
                // signal is funneled through this poller (R3) — it never spawns a
                // parallel FetchUsageAsync.
                TaskCompletionSource<bool> signal = Volatile.Read(ref _refreshSignal);
                Task tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                Task signalTask = signal.Task;

                Task winner = await Task.WhenAny(tickTask, signalTask).ConfigureAwait(false);

                bool isScheduledAutoPoll;
                if (winner == signalTask)
                {
                    isScheduledAutoPoll = false;
                    // The manual-refresh signal won — swap in a fresh TCS for the next
                    // click. ONLY the loop swaps; RefreshNowAsync only TrySetResults, so
                    // the CAS is race-safe (a concurrent RefreshNow either completes this
                    // TCS — already observed — or hits the new one; either way exactly
                    // one fetch runs because of _inFlight).
                    //
                    // We do NOT await tickTask here — the loop's next iteration disposes
                    // THIS iteration's PeriodicTimer (the `using var timer` above), which
                    // retires the pending WaitForNextTickAsync task. That disposal is the
                    // load-bearing single-outstanding-wait guarantee (G-01-7). Awaiting
                    // tickTask here would block RefreshNow for up to the remaining tick
                    // window.
                    Interlocked.CompareExchange(ref _refreshSignal, CreateRefreshSignal(), signal);
                }
                else
                {
                    isScheduledAutoPoll = true;
                    // tickTask won — observe it (it's already complete). The SAME TCS is
                    // re-read next loop iteration; abandoning its uncompleted Task leaks
                    // NOTHING (unlike a semaphore's queued waiter).
                    try { await tickTask.ConfigureAwait(false); } catch { /* cancelled or disposed */ }
                }

                await PollOnceAsync(isScheduledAutoPoll, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>
    /// D-13/D-22/D-21 — the per-iteration delay for the NEXT tick. The base interval comes
    /// from the LIVE shared <see cref="_intervalSource"/> (<see cref="PollIntervalSource.Current"/>,
    /// re-read every tick — a settings-window change applies on the next scheduled poll, no
    /// restart and no immediate fetch), scaled by the human jitter factor (in
    /// [<see cref="PollingDefaults.JitterMin"/>, <see cref="PollingDefaults.JitterMax"/>] =
    /// [0.6, 1.4]); when an outstanding 429 backoff exists and has not yet elapsed, the delay
    /// is extended to AT LEAST the remaining backoff (capped at
    /// <see cref="PollingDefaults.BackoffCeiling"/> when the backoff was set — REFRESH-03/ceiling).
    /// </summary>
    private TimeSpan ComputeNextDelay()
    {
        TimeSpan delay = _intervalSource.Current * JitterFactor();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_backoffUntilUtc is { } backoffUntil && now < backoffUntil)
        {
            TimeSpan remaining = backoffUntil - now;
            if (delay < remaining)
            {
                delay = remaining;
            }
        }

        return delay;
    }

    /// <summary>
    /// D-22 — the per-poll baseline jitter factor: a uniform draw in
    /// [<see cref="PollingDefaults.JitterMin"/>, <see cref="PollingDefaults.JitterMax"/>]
    /// (±40%). <see cref="_random"/> is the injected seam — tests pass a fixed-seed
    /// instance for deterministic range assertions; production uses <see cref="Random.Shared"/>.
    /// </summary>
    private double JitterFactor()
    {
        double span = PollingDefaults.JitterMax - PollingDefaults.JitterMin;
        return PollingDefaults.JitterMin + _random.NextDouble() * span;
    }

    /// <summary>
    /// One fetch cycle: claim the in-flight slot, read the key fresh, call the adapter,
    /// publish to the store. Degrades to an Error reading — NEVER rethrows out of the
    /// loop (a faulted poller degrades only its own row; it cannot cascade to another
    /// provider or crash the host — PROV-02/isolation).
    /// </summary>
    /// <param name="isScheduledAutoPoll">
    /// True when this fetch was triggered by a timer tick (the scheduled auto-poll),
    /// false when triggered by a manual refresh or the startup fetch. Drives the
    /// REFRESH-03 backoff-clear rule: ONLY a scheduled auto-poll with no RetryAfter
    /// clears an outstanding backoff — a refresh-triggered poll NEVER clears it.
    /// </param>
    /// <param name="ct">Cancellation for the fetch.</param>
    private async Task PollOnceAsync(bool isScheduledAutoPoll, CancellationToken ct)
    {
        // D-06 — a disabled poller NEVER starts a new fetch: a PollOnceAsync entered after
        // disable returns immediately (no fetch is spawned). A fetch already in flight when
        // the user disables may publish one final reading; NO new scheduled or manual fetch
        // runs while the poller is parked (T-03-09).
        // D-05 (04-02) — a SESSION-PARKED poller likewise never starts a new fetch: the
        // park is terminal for the session (a refresh signal that races the park's loop
        // entry lands here and is consumed as a no-op).
        if (!_enabled || _sessionParked)
        {
            return;
        }

        // R3: claim the in-flight slot BEFORE invoking the adapter. Any RefreshNowAsync
        // call during the fetch reuses this TaskCompletionSource.
        var existing = Volatile.Read(ref _inFlight);
        if (existing is not null)
        {
            // A concurrent caller is already running the fetch — let them.
            await existing.Task.ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource<UsageReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _inFlight, tcs, null) is not null)
        {
            // Lost the race — another caller claimed the slot; await their result.
            var winner = Volatile.Read(ref _inFlight);
            if (winner is not null)
            {
                await winner.Task.ConfigureAwait(false);
            }

            return;
        }

        try
        {
            // G-04-4 liveness — every dispatched fetch is visible BEFORE it runs, so a
            // silent poller (no fetch-start lines) is distinguishable from a slow one
            // (fetch-start without fetch-done). SEC-03: id + scheduled flag only.
            _logger?.LogInformation(
                "poller fetch start (provider={ProviderId}, scheduled={IsScheduled})",
                _adapter.Id, isScheduledAutoPoll);

            string? key = await _keySource.ReadFreshAsync(ct).ConfigureAwait(false);

            // Generalization: NO empty-key short-circuit here. The poller ALWAYS calls
            // the adapter, which classifies — Z.ai returns NotLoggedIn on an empty key
            // (its guard); a keyless adapter (Demo) ignores the key and returns Ok. This
            // is what lets one poller serve both manual-key and keyless adapters.
            AdapterFetchResult result = await _adapter.FetchUsageAsync(key, ct).ConfigureAwait(false);

            // D-05 session-park (04-02): a SESSION-family fetch that returns NotLoggedIn
            // against a PRESENT credential = the provider rejected an existing session
            // (expired OAuth). Stamp the park AFTER the reading is published (below) so
            // the store holds the terminal NotLoggedIn reading the row freezes on. The
            // sentinel-free discriminator: (NotLoggedIn AND Session-family AND
            // credential-present) — a Key-family NotLoggedIn (NO KEY) never satisfies it,
            // and a session adapter with NO credential never satisfies it either.
            bool sessionExpired =
                result.Reading.Status == ReadingStatus.NotLoggedIn
                && _adapter.AuthFamily == AuthFamily.Session
                && key is not null;

            // D-21/REFRESH-03 — the 429/Retry-After backoff state machine (cross-tick,
            // NOT in-tick; Polly stays <=1 transient retry with no 429 retry):
            //  - ANY fetch path that returns a RetryAfter extends THIS poller's next tick
            //    to now + min(RetryAfter, BackoffCeiling=2h). The ceiling caps a hostile
            //    or misconfigured Retry-After (T-02-33) — it cannot pin the poller forever.
            //  - A SCHEDULED auto-poll returning NO RetryAfter clears an outstanding
            //    backoff — the cadence resumes.
            //  - A refresh-triggered poll NEVER clears an outstanding backoff, even when
            //    the fetch succeeds (REFRESH-03/idempotency): a manual refresh does not
            //    reset the rate-limiter's schedule; only a later scheduled auto-poll can.
            if (result.RetryAfter is TimeSpan retryAfter && !_adapter.ManualOnlyFetch)
            {
                _backoffUntilUtc = DateTimeOffset.UtcNow
                    + TimeSpan.FromTicks(Math.Min(retryAfter.Ticks, PollingDefaults.BackoffCeiling.Ticks));
            }
            else if (isScheduledAutoPoll)
            {
                _backoffUntilUtc = null;
            }

            _store.Update(_adapter.Id, result.Reading);
            tcs.SetResult(result.Reading);

            // G-04-4 liveness — the fetch outcome line: status is the ReadingStatus enum
            // NAME only (Ok/NearLimit/Error/NotLoggedIn/Unsupported). SEC-03: no URL, no
            // header, no body, no exception text (the Error path's redacted message lives
            // in the store reading, not here).
            _logger?.LogInformation(
                "poller fetch done (provider={ProviderId}, status={Status})",
                _adapter.Id, result.Reading.Status);

            if (sessionExpired)
            {
                _sessionParked = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            tcs.TrySetCanceled(ct);
            throw;
        }
        catch (Exception ex)
        {
            // Defensive: degrade to Error instead of crashing the poll loop. Per-provider
            // DisplayName so the Error row names the right provider.
            var reading = ErrorReading(_adapter.DisplayName, $"Couldn't reach {_adapter.DisplayName}.", ex);
            _store.Update(_adapter.Id, reading);
            tcs.SetResult(reading);

            // G-04-4 liveness — a thrown fetch is ALSO a fetch outcome (Error). The
            // exception MESSAGE is deliberately NOT logged here (SEC-03); it is already
            // carried redacted inside the Error reading above.
            _logger?.LogInformation(
                "poller fetch done (provider={ProviderId}, status={Status})",
                _adapter.Id, reading.Status);
        }
        finally
        {
            Interlocked.CompareExchange(ref _inFlight, null, tcs);
        }
    }

    private static UsageReading LoadingReading(string provider) => new(
        Provider: provider,
        FetchedAtUtc: DateTimeOffset.UtcNow,
        Status: ReadingStatus.Ok,
        UsedPct: null,
        RemainingPct: null,
        MostBindingWindow: default,
        AllWindows: null,
        ErrorMessage: null);

    private static UsageReading ErrorReading(string provider, string message, Exception? inner)
    {
        // WR-02: include a redacted inner.Message in parentheses so the eventual UI / log
        // line carries the actionable root cause without leaking a token. SEC-03: the
        // inner message is passed through TokenRedactor.Redact before composition.
        string safe = string.IsNullOrEmpty(inner?.Message)
            ? message
            : $"{message} ({TokenRedactor.Redact(inner.Message)})";
        return new UsageReading(
            Provider: provider,
            FetchedAtUtc: DateTimeOffset.UtcNow,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: safe);
    }
}
