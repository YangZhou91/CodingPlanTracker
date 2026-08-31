using System;
using System.Collections.Generic;
using PlanMeter.Core.Models;
using PlanMeter.Core.Polling;

namespace PlanMeter.Core.Store;

/// <summary>
/// Per-provider keyed observable store (D-13/D-14). One <see cref="Slot"/> per
/// <see cref="ProviderId"/> holding <c>Current</c> + <c>LastSuccessful</c>, so the row
/// can show STALE (last-known @ 50% opacity) on a transient failure — per provider,
/// never cross-slot. The Phase-1 single-provider store was the N=1 case of this shape.
/// </summary>
/// <remarks>
/// SC#2 isolation at the store level: updating slot A fires <c>Changed(A)</c> ONLY —
/// slot B's subscribers are never invoked. Thread-safety: a single <c>_gate</c> lock for
/// all slot reads/writes; per-slot state lives inside the dictionary, so two pollers
/// writing different slots never block each other longer than one lock acquisition.
/// </remarks>
public sealed class UsageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ProviderId, Slot> _slots = new();

    /// <summary>
    /// Fired on every <see cref="Update"/> / <see cref="Clear"/> for the ONE slot that
    /// changed. Subscribers marshal onto the UI thread themselves (WPF:
    /// <c>Dispatcher.InvokeAsync</c>).
    /// </summary>
    public event EventHandler<ProviderReadingChangedEventArgs>? Changed;

    /// <summary>
    /// The STALE threshold (WR-04 age gate). Binds to
    /// <see cref="PollingDefaults.DefaultInterval"/> — unchanged 10-min binding from
    /// Phase 1 (do NOT break it: <c>StaleReadingTests.WR04_*</c> pin the value).
    /// </summary>
    public static readonly TimeSpan Interval = PollingDefaults.DefaultInterval;

    /// <summary>
    /// Create the provider's slot if absent (idempotent). The App calls this for each
    /// enabled provider during DI setup so the slot exists before the poller writes.
    /// </summary>
    public void Register(ProviderId id)
    {
        lock (_gate)
        {
            if (!_slots.ContainsKey(id))
            {
                _slots[id] = new Slot();
            }
        }
    }

    /// <summary>The provider's latest reading, or null when the slot does not exist or was cleared.</summary>
    public UsageReading? Current(ProviderId id)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(id, out var slot) ? slot.Current : null;
        }
    }

    /// <summary>
    /// The provider's most recent <c>Status == Ok || NearLimit</c> reading — the
    /// last-known figure to decay from when <see cref="Current"/> is an Error. Null when
    /// the slot does not exist, was cleared, or has never succeeded.
    /// </summary>
    public UsageReading? LastSuccessful(ProviderId id)
    {
        lock (_gate)
        {
            return _slots.TryGetValue(id, out var slot) ? slot.LastSuccessful : null;
        }
    }

    /// <summary>
    /// WR-04 / DATA-03 STALE-vs-ERROR age gate, evaluated against THIS provider's own
    /// <c>LastSuccessful</c> (per-slot — a second provider's prior success never makes
    /// this one's error stale). A row renders STALE only when BOTH hold: (a) the
    /// provider has a prior successful reading to decay from, AND (b) the supplied
    /// error reading is OLDER than one interval (STRICT <c>&gt;</c> — an error exactly
    /// one interval old is still a hard ERROR, because the next scheduled poll may be
    /// landing momentarily).
    /// </summary>
    /// <param name="id">The provider whose slot to evaluate against.</param>
    /// <param name="errorReading">The current reading (normally the Error just published).</param>
    /// <param name="nowUtc">Injection seam for tests; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public bool IsStale(ProviderId id, UsageReading? errorReading, DateTimeOffset? nowUtc = null)
    {
        if (errorReading is null || errorReading.Status != ReadingStatus.Error)
        {
            return false;
        }

        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        TimeSpan errorAge = now - errorReading.FetchedAtUtc;

        lock (_gate)
        {
            return _slots.TryGetValue(id, out var slot)
                && slot.LastSuccessful is not null
                && errorAge > Interval;
        }
    }

    /// <summary>
    /// Publish a new reading for the provider's slot. Updates the slot's <c>Current</c>
    /// and, when the reading is Ok/NearLimit, its <c>LastSuccessful</c>. Invokes
    /// <see cref="Changed"/> with the per-slot args AFTER the lock. Never throws on a
    /// null reading (treated as no-op). A missing slot is created on first update.
    /// </summary>
    public void Update(ProviderId id, UsageReading? reading)
    {
        if (reading is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_slots.TryGetValue(id, out var slot))
            {
                slot = new Slot();
                _slots[id] = slot;
            }

            slot.Current = reading;
            if (reading.Status is ReadingStatus.Ok or ReadingStatus.NearLimit)
            {
                slot.LastSuccessful = reading;
            }
        }

        Changed?.Invoke(this, new ProviderReadingChangedEventArgs(id, reading));
    }

    /// <summary>
    /// Clear the provider's slot (both <c>Current</c> and <c>LastSuccessful</c>, e.g.
    /// when the user clears the saved key). Fires <see cref="Changed"/> with
    /// <c>(id, null)</c>.
    /// </summary>
    public void Clear(ProviderId id)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(id, out var slot))
            {
                slot.Current = null;
                slot.LastSuccessful = null;
            }
        }

        Changed?.Invoke(this, new ProviderReadingChangedEventArgs(id, null));
    }

    /// <summary>
    /// One snapshot per registered slot, in first-<see cref="Register"/> order. The UI
    /// pulls this once on startup to render the initial rows, then subscribes per-slot
    /// via <see cref="Changed"/>.
    /// </summary>
    public IReadOnlyList<ProviderSlotSnapshot> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<ProviderSlotSnapshot>(_slots.Count);
            foreach (var pair in _slots)
            {
                result.Add(new ProviderSlotSnapshot(pair.Key, pair.Value.Current, pair.Value.LastSuccessful));
            }

            return result;
        }
    }

    /// <summary>Per-provider slot state. All access is under <c>_gate</c>.</summary>
    private sealed class Slot
    {
        public UsageReading? Current;
        public UsageReading? LastSuccessful;
    }
}

/// <summary>
/// The per-slot change payload: WHICH provider changed and the new reading (null on clear).
/// </summary>
/// <param name="Id">The provider whose slot changed.</param>
/// <param name="Reading">The new reading, or null when the slot was cleared.</param>
public sealed record ProviderReadingChangedEventArgs(ProviderId Id, UsageReading? Reading);

/// <summary>
/// One provider's store state, for the startup snapshot.
/// </summary>
/// <param name="Id">The provider.</param>
/// <param name="Current">The provider's latest reading (null when none).</param>
/// <param name="LastSuccessful">The provider's last successful reading (null when none).</param>
public sealed record ProviderSlotSnapshot(ProviderId Id, UsageReading? Current, UsageReading? LastSuccessful);
