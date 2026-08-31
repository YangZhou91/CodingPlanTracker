using System;
using System.Collections.Generic;
using System.Linq;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// SC#1/D-03 — the flat provider list PLUS a mutable enabled-set. The full adapter list
/// (<see cref="All"/>, registration order) drives the settings window's provider cards
/// (every known provider renders with an enable/disable toggle — enabled + disabled);
/// <see cref="Enabled"/> is the filtered view the poller setup and the UI row build read
/// (a disabled provider's row disappears, D-03). Looking an adapter up by
/// <see cref="ProviderId"/> is the key-management path. Adding a provider = one entry in
/// the list the App registers (never a scheduler / store / UI edit).
/// </summary>
/// <remarks>
/// Thread-safety: <see cref="_providers"/> is snapshotted to an array at construction and
/// never mutated; the <see cref="_enabled"/> set is mutated ONLY by the settings-window
/// toggle handler on the UI thread and read by <see cref="Enabled"/>/<see cref="IsEnabled"/>
/// on the UI thread (row rebuild) and at startup (DI factory). The <see cref="EnabledChanged"/>
/// event is a UI-thread event — the WPF subscribers marshal via the Dispatcher.
/// </remarks>
public sealed class ProviderRegistry
{
    private readonly IReadOnlyList<IProviderAdapter> _providers;
    private readonly HashSet<ProviderId> _enabled;

    /// <summary>
    /// Construct with the full adapter list and the (optional) enabled ids.
    /// </summary>
    /// <param name="providers">The flat list of <see cref="IProviderAdapter"/> instances.</param>
    /// <param name="enabled">
    /// The initially-enabled provider ids. Null → every registered provider is enabled
    /// (the default). Ids that are not registered (e.g. a disabled Demo absent in a Release
    /// build) are ignored by the set.
    /// </param>
    public ProviderRegistry(IEnumerable<IProviderAdapter> providers, IEnumerable<ProviderId>? enabled = null)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        _providers = providers.ToArray();
        var known = _providers.Select(p => p.Id).ToHashSet();
        _enabled = enabled is null
            ? known
            : new HashSet<ProviderId>(enabled.Where(known.Contains));
    }

    /// <summary>
    /// Every known adapter in registration order — enabled AND disabled (D-03). The
    /// settings window iterates this so a fully-disabled provider stays reachable for
    /// re-enabling.
    /// </summary>
    public IReadOnlyList<IProviderAdapter> All => _providers;

    /// <summary>
    /// The enabled adapters in registration order (the UI renders rows in this order —
    /// Z.ai first, then Demo — preserving stable row ordering, PROV-02/ordering). A
    /// filtered view over the enabled-set; a disabled provider is absent.
    /// </summary>
    public IReadOnlyList<IProviderAdapter> Enabled =>
        _providers.Where(p => _enabled.Contains(p.Id)).ToArray();

    /// <summary>Whether the provider with the given id is currently enabled.</summary>
    public bool IsEnabled(ProviderId id) => _enabled.Contains(id);

    /// <summary>
    /// D-03 — add/remove the provider from the enabled-set. Fires <see cref="EnabledChanged"/>
    /// ONLY when the state actually changed (an idempotent <c>SetEnabled</c> is a no-op) —
    /// the settings-window toggle calls this after persisting; <c>MainWindow</c> rebuilds
    /// the row stack from <see cref="Enabled"/> on the event.
    /// </summary>
    public void SetEnabled(ProviderId id, bool enabled)
    {
        bool changed = enabled ? _enabled.Add(id) : _enabled.Remove(id);
        if (changed)
        {
            EnabledChanged?.Invoke(this, new ProviderEnabledChangedEventArgs(id, enabled));
        }
    }

    /// <summary>
    /// Fired by <see cref="SetEnabled"/> when the enabled-set changes. UI-thread event —
    /// WPF subscribers (MainWindow row rebuild) run on the Dispatcher.
    /// </summary>
    public event EventHandler<ProviderEnabledChangedEventArgs>? EnabledChanged;

    /// <summary>
    /// Resolve an adapter by its identity, or null when no registered adapter has that id
    /// (regardless of enabled state — the settings window + save handler resolve by id).
    /// </summary>
    public IProviderAdapter? Get(ProviderId id) => _providers.FirstOrDefault(p => p.Id == id);
}

/// <summary>
/// The enabled-set change payload: WHICH provider changed and to what state.
/// </summary>
/// <param name="Id">The provider whose enabled state changed.</param>
/// <param name="Enabled">The new enabled state (true = enabled).</param>
public sealed record ProviderEnabledChangedEventArgs(ProviderId Id, bool Enabled);
