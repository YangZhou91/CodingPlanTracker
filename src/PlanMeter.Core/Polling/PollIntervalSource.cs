using System;
using System.Threading;

namespace PlanMeter.Core.Polling;

/// <summary>
/// CONF-03/D-13/D-14 (03-04) — the shared, SINGLE-GLOBAL current-interval source every
/// <see cref="ProviderPoller"/> re-reads each tick. The settings window's Polling card
/// <see cref="SetInterval"/>s it live (persisting through the ConfigStore); the poll loop's
/// <c>ComputeNextDelay()</c> reads <see cref="Current"/> per iteration, so a change takes
/// effect on the NEXT scheduled poll with NO immediate fetch and NO restart (SC#3).
///
/// One instance serves EVERY provider (D-14) — there is no per-provider interval and no
/// merging. The store's STALE threshold (<c>UsageStore.Interval</c>) is deliberately NOT
/// coupled to this value (Pitfall 6): the live source is read only by the pollers.
///
/// Modeled on <see cref="PlanMeter.Core.Refresh.GlobalRefreshGate"/> — a tiny stateful Core
/// singleton, DI-registered <c>AddSingleton</c>. No event is needed: the poll loop re-reads
/// the volatile field every iteration.
/// </summary>
public sealed class PollIntervalSource
{
    /// <summary>
    /// The current interval as <see cref="TimeSpan.Ticks"/>, read/written with
    /// <see cref="Volatile.Read(ref long)"/> / <see cref="Volatile.Write(ref long, long)"/>.
    /// Storing the full tick count — rather than integer seconds — keeps the interval exact:
    /// production values are minute-scale presets (300-3600 s), while the poller tests inject
    /// a sub-second short-tick seam (<c>new PollIntervalSource(TimeSpan.FromMilliseconds(400))</c>)
    /// that integer-seconds truncation would collapse to <see cref="TimeSpan.Zero"/>. The
    /// volatile ops give the cross-thread visibility the poll loop needs (loop task reads it
    /// per tick; the settings-window UI thread writes it) without a lock on a hot path.
    /// </summary>
    private long _intervalTicks;

    /// <summary>Seeds the source with the startup interval (already clamped + precedence-resolved).</summary>
    public PollIntervalSource(TimeSpan interval)
    {
        _intervalTicks = interval.Ticks;
    }

    /// <summary>
    /// The current interval. Read per tick by <c>ComputeNextDelay()</c> — a change to this
    /// value is picked up on the NEXT tick (D-13), never mid-cycle.
    /// </summary>
    public TimeSpan Current => TimeSpan.FromTicks(Volatile.Read(ref _intervalTicks));

    /// <summary>
    /// Live-apply a new interval. A pure volatile write — it performs NO immediate fetch and
    /// signals NO event (D-13); the running pollers pick it up on their next scheduled tick.
    /// </summary>
    public void SetInterval(TimeSpan interval) => Volatile.Write(ref _intervalTicks, interval.Ticks);
}
