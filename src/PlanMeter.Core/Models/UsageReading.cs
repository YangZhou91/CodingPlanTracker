namespace PlanMeter.Core.Models;

/// <summary>
/// The kind of quota window a reading comes from (DATA-02 — chip label).
/// Phase 1 Z.ai exposes FiveHour (rolling 5h) and Weekly (7-day cap).
/// Monthly is defined now so the multi-provider phases (4+) do not need to amend the enum.
/// </summary>
public enum WindowKind
{
    FiveHour,
    Weekly,
    Monthly,
    Rolling,

    /// <summary>
    /// A credits bucket rather than a time-banded window (Phase-10 D-12):
    /// the Grok plan fallback when days-to-reset falls outside the
    /// weekly/monthly bands or no reset was reported.
    /// </summary>
    Credits,
}

/// <summary>
/// The row state for a single provider (WIDGET-03). Each row independently holds
/// one of these states; a single failure never cascades to another row.
/// </summary>
public enum ReadingStatus
{
    Ok,
    NearLimit,
    Error,
    NotLoggedIn,
    Unsupported,
}

/// <summary>
/// One per-window meter reading (DATA-02 tooltip stack).
/// </summary>
/// <param name="Kind">Window discriminator (5h / weekly / monthly).</param>
/// <param name="UsedPct">Used percentage in [0.0, 100.0].</param>
/// <param name="RemainingPct">Remaining percentage in [0.0, 100.0] (= 100 - UsedPct).</param>
/// <param name="ResetsAtUtc">When the window resets (null when the provider did not report).</param>
public sealed record WindowReading(
    WindowKind Kind,
    double UsedPct,
    double RemainingPct,
    DateTimeOffset? ResetsAtUtc);

/// <summary>
/// The uniform reading the UI consumes and that Phase 2's <c>IProviderAdapter</c> port
/// will require. Shape is RESEARCH.md §Normalizer Design verbatim — do not invent fields.
/// </summary>
/// <param name="Provider">Human-readable provider name (e.g. "Z.ai").</param>
/// <param name="FetchedAtUtc">When the 200 response was received (DATA-03).</param>
/// <param name="Status">Row state.</param>
/// <param name="UsedPct">Most-binding window's used %, in [0.0, 100.0]; null when not computable (no data yet / not logged in / error / unsupported).</param>
/// <param name="RemainingPct">Most-binding window's remaining %; null when not computable.</param>
/// <param name="MostBindingWindow">Which window the figure/chip describe.</param>
/// <param name="AllWindows">All windows the provider reported (tooltip stack); null/empty when none.</param>
/// <param name="ErrorMessage">Human-readable cause on Error (SEC-03 redacted — no URL/headers/body); null otherwise.</param>
public sealed record UsageReading(
    string Provider,
    DateTimeOffset FetchedAtUtc,
    ReadingStatus Status,
    double? UsedPct,
    double? RemainingPct,
    WindowKind MostBindingWindow,
    IReadOnlyList<WindowReading>? AllWindows,
    string? ErrorMessage)
{
    /// <summary>
    /// Near-limit threshold (ZAI-01/boundary + WIDGET-03/boundary): a row is NearLimit
    /// when the most-binding window's RemainingPct is at or below this value (>= 80% used).
    /// </summary>
    public const double NearLimitRemainingThreshold = 20.0;

    /// <summary>
    /// Convenience: true when this reading carries a usable figure (Ok or NearLimit).
    /// </summary>
    public bool HasFigure => Status is ReadingStatus.Ok or ReadingStatus.NearLimit && UsedPct.HasValue;
}
