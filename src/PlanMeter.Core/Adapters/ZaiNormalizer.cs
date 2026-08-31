using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// Defensive DTOs for the Z.ai <c>api/monitor/usage/quota/limit</c> response.
/// The endpoint is undocumented (Pitfall 2) and community sources disagree on the
/// per-entry field names inside <c>data.limits[]</c> (research §Z.ai Endpoint Contract C4).
/// These DTOs are deliberately tolerant: every candidate consumed-field name is
/// tried (<c>usage</c> / <c>currentValue</c> / <c>used</c>), and entries with an
/// unrecognised meter <c>type</c> are skipped rather than throwing.
/// </summary>
public sealed class ZaiEnvelope
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public ZaiEnvelopeData? Data { get; set; }
}

public sealed class ZaiEnvelopeData
{
    [JsonPropertyName("limits")]
    public List<ZaiLimitEntry>? Limits { get; set; }
}

public sealed class ZaiLimitEntry
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Window-length discriminator (research §C3/§C4). Sub-daily (e.g. 5) → FiveHour;
    /// multi-day (e.g. 7) → Weekly. Keyed off LENGTH, never off a hard-coded literal.
    /// </summary>
    [JsonPropertyName("number")]
    public double Number { get; set; }

    /// <summary>
    /// Window length in seconds (some community sources surface this instead of / in
    /// addition to <c>number</c>). Used to discriminate FiveHour vs Weekly when
    /// <c>number</c> is absent. 5h = 18 000; 7d = 604 800.
    /// </summary>
    [JsonPropertyName("window")]
    public double? Window { get; set; }

    [JsonPropertyName("usage")]
    public double? Usage { get; set; }

    [JsonPropertyName("currentValue")]
    public double? CurrentValue { get; set; }

    [JsonPropertyName("used")]
    public double? Used { get; set; }

    [JsonPropertyName("limit")]
    public double? Limit { get; set; }

    [JsonPropertyName("total")]
    public double? Total { get; set; }

    /// <summary>
    /// Window discriminator Z.ai actually uses for TOKENS_LIMIT entries (live-evidenced
    /// 2026-08-11 dogfood curl against a real GLM Coding Pro account — the overdue
    /// 01-RESEARCH.md line-70 mandate). Observed values: unit==3 → FiveHour rolling
    /// window; unit==6 → Weekly cap; unit==5 on TIME_LIMIT entries (skipped by the type
    /// filter, never reaches the classifier). <see cref="TryClassifyWindow"/> consults
    /// this FIRST; the legacy <c>number</c>/<c>window</c> heuristics are fallback only.
    /// </summary>
    [JsonPropertyName("unit")]
    public int? Unit { get; set; }

    /// <summary>
    /// Z.ai pre-computes the used % per window (live-evidenced 2026-08-11 dogfood curl).
    /// Preferred over the used/limit*100 computation when present (the two can diverge
    /// for pro-rated windows; Z.ai's value is authoritative). Clamped to [0.0, 100.0]
    /// on consume. The legacy used/limit*100 path is fallback when this is absent.
    /// </summary>
    [JsonPropertyName("percentage")]
    public double? Percentage { get; set; }

    [JsonPropertyName("resetTime")]
    public DateTimeOffset? ResetTime { get; set; }

    /// <summary>
    /// Defensive fallback: any future field Z.ai adds is captured here rather than
    /// throwing. The normalizer inspects this dictionary for additional candidate
    /// consumed/limit fields if the typed fields above are all absent.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Resolve the consumed-tokens value, whichever of the candidate fields the
    /// endpoint populated. Returns null when none is present (entry is skipped).
    /// </summary>
    public double? ResolveUsed() => Usage ?? CurrentValue ?? Used ?? TryExtensionDouble("consumed");

    /// <summary>
    /// Resolve the window-cap value. Returns null when none is present (entry is skipped).
    /// </summary>
    public double? ResolveLimit() => Limit ?? Total ?? TryExtensionDouble("cap");

    private double? TryExtensionDouble(string key)
    {
        if (ExtensionData is null)
        {
            return null;
        }

        if (ExtensionData.TryGetValue(key, out var element) && element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out var d) ? d : (double?)null;
        }

        return null;
    }
}

/// <summary>
/// Maps a parsed <see cref="ZaiEnvelope"/> to the uniform <see cref="UsageReading"/>.
/// Research §Normalizer Design verbatim:
/// <list type="bullet">
///   <item>Per window: PREFER the pre-computed <c>percentage</c> field when present (clamped to [0.0, 100.0]); FALL BACK to <c>(used / limit) * 100</c> clamped to [0.0, 100.0] when <c>percentage</c> is absent (live-evidenced 2026-08-11 dogfood curl).</item>
///   <item>Window kind classified by <c>unit</c> first (3→FiveHour, 6→Weekly); falls back to <c>window</c>-seconds then <c>number</c> for the legacy shape.</item>
///   <item>MostBindingWindow = argmin(remainingPct); on a tie, FiveHour wins (resets sooner).</item>
///   <item>Status = Ok if mostBindingRemaining &gt; 20, else NearLimit.</item>
///   <item>Empty <c>data: {{}}</c> maps to Status=Ok with UsedPct=null (Q1 — non-error).</item>
///   <item>Unrecognised meter types (e.g. TIME_LIMIT) are skipped, NOT thrown on.</item>
/// </list>
/// </summary>
public static class ZaiNormalizer
{
    private const string TokensLimitType = "TOKENS_LIMIT";

    /// <summary>
    /// Normalise a parsed Z.ai envelope into a <see cref="UsageReading"/>. The
    /// <paramref name="fetchedAtUtc"/> must be captured at the moment the 200 response
    /// was received (DATA-03).
    /// </summary>
    public static UsageReading Normalize(ZaiEnvelope envelope, DateTimeOffset fetchedAtUtc)
    {
        if (envelope is null)
        {
            return ErrorReading(fetchedAtUtc, "Z.ai returned an empty envelope.");
        }

        var windows = new List<WindowReading>();

        if (envelope.Data?.Limits is { Count: > 0 } limits)
        {
            foreach (var entry in limits)
            {
                if (string.IsNullOrEmpty(entry.Type))
                {
                    continue;
                }

                // Skip non-token meters (e.g. TIME_LIMIT) — never throw.
                if (!string.Equals(entry.Type, TokensLimitType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                double? used = entry.ResolveUsed();
                double? limit = entry.ResolveLimit();

                bool hasPercentage = entry.Percentage.HasValue;
                bool hasUsedLimit = used.HasValue && limit.HasValue && limit.Value > 0;

                // Keep a TOKENS_LIMIT entry if EITHER signal is present (research §C4
                // tolerance: the endpoint is undocumented; do not silently drop a
                // real-shape percentage-only entry — live-evidenced 2026-08-11 dogfood
                // curl: the real shape carries percentage+unit, NOT used/limit/window,
                // so the pre-fix used-only guard dropped every real-shape entry). The
                // legacy-shape entries (used+limit, no percentage) still pass through.
                if (!hasPercentage && !hasUsedLimit)
                {
                    continue;
                }

                if (!TryClassifyWindow(entry, out var kind))
                {
                    // Skip entries whose window length we cannot classify (defensive —
                    // never throw; future Z.ai window types degrade gracefully).
                    continue;
                }

                // Prefer Z.ai's pre-computed percentage (authoritative for pro-rated
                // windows); fall back to used/limit*100 only when percentage is absent.
                // The ternary's fallback branch is only evaluated when hasUsedLimit is
                // true, so used/limit are guaranteed non-null there (the !. operator is
                // safe). hasPercentage path never touches used/limit (they may be null
                // on a percentage-only real-shape entry — that's the bug fix).
                double usedPct = hasPercentage
                    ? Math.Clamp(entry.Percentage!.Value, 0.0, 100.0)
                    : Math.Clamp(used!.Value / limit!.Value * 100.0, 0.0, 100.0);
                double remainingPct = 100.0 - usedPct;

                windows.Add(new WindowReading(kind, usedPct, remainingPct, entry.ResetTime));
            }
        }

        if (windows.Count == 0)
        {
            // Empty data:{} (or no TOKENS_LIMIT entries) — Q1: map to Ok with null figure.
            return new UsageReading(
                Provider: "Z.ai",
                FetchedAtUtc: fetchedAtUtc,
                Status: ReadingStatus.Ok,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: Array.Empty<WindowReading>(),
                ErrorMessage: null);
        }

        // Most-binding = argmin(remainingPct). Tie-break: FiveHour wins (resets sooner —
        // the one the user will hit first). Stable ordering: when remainingPct is equal,
        // FiveHour is preferred over Weekly over Monthly.
        var mostBinding = windows
            .OrderBy(w => w.RemainingPct)
            .ThenBy(w => WindowPriority(w.Kind))
            .First();

        var status = mostBinding.RemainingPct <= UsageReading.NearLimitRemainingThreshold
            ? ReadingStatus.NearLimit
            : ReadingStatus.Ok;

        return new UsageReading(
            Provider: "Z.ai",
            FetchedAtUtc: fetchedAtUtc,
            Status: status,
            UsedPct: mostBinding.UsedPct,
            RemainingPct: mostBinding.RemainingPct,
            MostBindingWindow: mostBinding.Kind,
            AllWindows: windows,
            ErrorMessage: null);
    }

    /// <summary>
    /// Classify a Z.ai <c>TOKENS_LIMIT</c> entry into a <see cref="WindowKind"/>. The
    /// primary signal is <see cref="ZaiLimitEntry.Unit"/> (live-evidenced 2026-08-11
    /// dogfood curl: 3→FiveHour, 6→Weekly); the legacy <c>window</c>-seconds and
    /// <c>number</c> heuristics are fallback for the older shape. Returns false when
    /// neither path yields a recognized window.
    /// </summary>
    private static bool TryClassifyWindow(ZaiLimitEntry entry, out WindowKind kind)
    {
        // PRIMARY — unit is the live-evidenced window discriminator (2026-08-11 dogfood
        // curl). 3=5h rolling, 6=weekly cap. 7/8 unobserved but mapped defensively to
        // Monthly in case a future plan tier surfaces them; a unit we do not recognize
        // falls through to the legacy window/number heuristics below rather than being
        // dropped.
        if (entry.Unit.HasValue)
        {
            switch (entry.Unit.Value)
            {
                case 3:
                    kind = WindowKind.FiveHour;
                    return true;
                case 6:
                    kind = WindowKind.Weekly;
                    return true;
                case 7:
                case 8:
                    kind = WindowKind.Monthly;
                    return true;
                default:
                    // Unknown unit — fall through to the legacy fallback rather than drop.
                    break;
            }
        }

        // Legacy fallback: prefer the explicit window length in seconds when present
        // (5h=18000, 7d=604800). < 1 day in seconds → FiveHour. >= 1 day → Weekly/Monthly.
        double? windowSeconds = entry.Window;
        double? number = entry.Number > 0 ? entry.Number : (double?)null;

        if (windowSeconds.HasValue)
        {
            if (windowSeconds.Value <= TimeSpan.FromHours(12).TotalSeconds)
            {
                kind = WindowKind.FiveHour;
                return true;
            }

            if (windowSeconds.Value <= TimeSpan.FromDays(10).TotalSeconds)
            {
                kind = WindowKind.Weekly;
                return true;
            }

            kind = WindowKind.Monthly;
            return true;
        }

        // Legacy fallback: use number when window is absent. Sub-daily (e.g. number=5
        // for the 5-hour window) → FiveHour. Multi-day → Weekly.
        if (number.HasValue)
        {
            if (number.Value < 24)
            {
                kind = WindowKind.FiveHour;
                return true;
            }

            kind = WindowKind.Weekly;
            return true;
        }

        kind = default;
        return false;
    }

    /// <summary>Tie-break priority — lower wins. Rolling (0) &lt; FiveHour (1) &lt; Weekly (2) &lt; Monthly (3). Matches OpenCodeNormalizer.</summary>
    private static int WindowPriority(WindowKind kind) => kind switch
    {
        WindowKind.Rolling => 0,
        WindowKind.FiveHour => 1,
        WindowKind.Weekly => 2,
        WindowKind.Monthly => 3,
        _ => 4,
    };

    private static UsageReading ErrorReading(DateTimeOffset fetchedAtUtc, string message)
        => new(
            Provider: "Z.ai",
            FetchedAtUtc: fetchedAtUtc,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: message);

    /// <summary>
    /// Convenience overload — parse JSON then normalise. Throws <see cref="JsonException"/>
    /// upward so the adapter can map malformed JSON to an Error reading.
    /// </summary>
    public static UsageReading Normalize(string json, DateTimeOffset fetchedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ErrorReading(fetchedAtUtc, "Z.ai returned an empty body.");
        }

        var envelope = JsonSerializer.Deserialize<ZaiEnvelope>(json);
        return Normalize(envelope ?? new ZaiEnvelope(), fetchedAtUtc);
    }

    /// <summary>
    /// Format a UsedPct for display — whole number, no decimals (UI-SPEC §Copywriting:
    /// "74%" not "74.3%"). The comparison that picks NearLimit uses the UNROUNDED
    /// RemainingPct (ZAI-01/precision truth); this formatter is purely cosmetic.
    /// </summary>
    public static string FormatFigure(double? usedPct)
    {
        if (!usedPct.HasValue)
        {
            return "—";
        }

        double rounded = Math.Round(usedPct.Value, MidpointRounding.AwayFromZero);
        return $"{rounded.ToString("0", CultureInfo.InvariantCulture)}%";
    }
}
