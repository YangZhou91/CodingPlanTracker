using System;

namespace PlanMeter.Core.Refresh;

/// <summary>
/// The SC#4 global manual-refresh throttle (D-02/D-03/D-20): at most ONE "Refresh now"
/// trigger per <see cref="Window"/> across ALL providers. The widget-global 'Refresh now'
/// handler consults <see cref="TryAcquire"/> before fanning out to every enabled poller.
/// </summary>
/// <remarks>
/// <para>
/// This component bounds the <b>user action</b> — one manual-refresh demand per 60s,
/// globally — NOT the per-provider request count (one click legitimately fans out to N
/// fetches per D-01/D-02). It is a DISTINCT concern from:
/// </para>
/// <list type="bullet">
///   <item>per-provider in-flight funneling (R3, lives in <c>ProviderPoller</c>), and</item>
///   <item>per-provider 429 / <c>Retry-After</c> cross-tick backoff (Plan 02-04).</item>
/// </list>
/// <para>
/// A rejected acquire (second click inside the window) is a SILENT NO-OP (D-03): the caller
/// returns before any fetch fan-out, no UI feedback, no cooldown label, no disabled state.
/// The save-key path (D-04) BYPASSES the gate entirely — a key save is a config action, not
/// a manual refresh.
/// </para>
/// <para>
/// The boundary is INCLUSIVE: <c>now - lastAcquired &gt;= Window</c> grants a new acquire
/// (the 60th-second boundary opens a new window). <see cref="TryAcquire"/> is clock-injectable
/// via the <c>nowUtc</c> seam (mirroring <c>UsageStore.IsStale</c>) so the boundary is
/// unit-testable without real time; <see cref="DateTimeOffset.UtcNow"/> is the production clock.
/// </para>
/// </remarks>
public sealed class GlobalRefreshGate
{
    /// <summary>The SC#4 global gate window — one manual-refresh trigger per 60 seconds.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private DateTimeOffset? _lastAcquiredUtc;

    /// <summary>
    /// Attempts to acquire the single manual-refresh slot for the current window.
    /// </summary>
    /// <param name="nowUtc">Clock-injection seam for tests; production uses <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <returns>
    /// <c>true</c> when this is the first acquire in the window (or the previous acquire is
    /// at least one full <see cref="Window"/> old — the 60th-second boundary is inclusive);
    /// <c>false</c> when a previous acquire is still inside the window.
    /// </returns>
    public bool TryAcquire(DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_lastAcquiredUtc is null || now - _lastAcquiredUtc.Value >= Window)
            {
                _lastAcquiredUtc = now;
                return true;
            }

            return false;
        }
    }
}
