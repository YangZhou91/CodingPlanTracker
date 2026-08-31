using System;

namespace PlanMeter.Core.Polling;

/// <summary>
/// The refresh-backbone constants for every provider poller (D-17/D-18/D-21/D-22).
/// Planner-chosen values become manifest-driven fields (rate_tolerance) in Phase 3/4.
/// </summary>
public static class PollingDefaults
{
    /// <summary>
    /// D-17 — the default background auto-poll interval, unchanged from Phase 1's 10 min.
    /// The conservative research-mandated default; now overridable per provider via the
    /// shared interval source (<see cref="PollIntervalSource"/>, 03-04).
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// D-11 (CONF-03, 03-04) — the preset dropdown values the settings-window Polling card
    /// maps to: {5, 10, 15, 30, 60} minutes = {300, 600, 900, 1800, 3600} seconds. There is
    /// NO free numeric input (D-11); the code-side 5-min clamp (D-12) stays absolute in
    /// <see cref="PlanMeter.Config.PlanMeterConfigLoader"/> for the env-var / config.json
    /// path — the presets never present a sub-5-min value.
    /// </summary>
    public static readonly IReadOnlyList<int> PresetIntervalSeconds = new[] { 300, 600, 900, 1800, 3600 };

    /// <summary>
    /// D-18 — the 5-minute floor is absolute in Release. Any configured interval below
    /// this is clamped to it (and the clamp logged). A dev-only bypass is compile-gated
    /// to Debug and never ships in Release.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// D-21 — cap on how far a 429 <c>Retry-After</c> can extend a provider's NEXT tick.
    /// Conservative default; becomes manifest-driven (backoff_ceiling_secs) in Phase 3/4.
    /// </summary>
    public static readonly TimeSpan BackoffCeiling = TimeSpan.FromHours(2);

    /// <summary>
    /// D-22 — per-poll baseline jitter range (±40% of the nominal period). Applied to
    /// EVERY tick so N providers don't fire at the same instant.
    /// </summary>
    public const double JitterMin = 0.6;

    /// <summary>
    /// D-22 — per-poll baseline jitter range (±40% of the nominal period).
    /// </summary>
    public const double JitterMax = 1.4;
}
