using System.Globalization;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Presentation;

/// <summary>
/// UIR-01 / UIR-02 / UIR-04 — remaining-fill, compact until-reset, chip labels, and
/// tooltip lines. No WPF types; ProviderRow consumes these from the Ok paint path.
/// </summary>
public static class QuotaRowFormatter
{
    /// <summary>
    /// Remaining-quota fill identity (UIR-01). Clamp remainingPct to [0, 100].
    /// Non-finite input (NaN / Infinity) maps to (0, 100) so a hostile already-parsed
    /// value cannot produce a negative GridLength (T-12-01).
    /// </summary>
    public static (double fillStars, double emptyStars) RemainingFill(double remainingPct)
    {
        if (!double.IsFinite(remainingPct))
        {
            return (0, 100);
        }

        double fill = Math.Clamp(remainingPct, 0.0, 100.0);
        return (fill, 100.0 - fill);
    }

    /// <summary>
    /// Compact until-reset (UIR-02). null / elapsed / now-or-past → null (Credits-style
    /// empty slot). Days band is the load-bearing gap vs FormatRelative (no "d" token).
    /// </summary>
    public static string? FormatCompactUntil(DateTimeOffset? resetsAtUtc, DateTimeOffset now)
    {
        if (resetsAtUtc is not DateTimeOffset reset)
        {
            return null;
        }

        TimeSpan ts = reset - now;
        if (ts <= TimeSpan.Zero)
        {
            return null;
        }

        if (ts < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (ts < TimeSpan.FromMinutes(60))
        {
            return $"{(int)ts.TotalMinutes}m";
        }

        if (ts < TimeSpan.FromMinutes(90))
        {
            return "1h";
        }

        if (ts < TimeSpan.FromHours(24))
        {
            return $"{(int)Math.Floor(ts.TotalHours)}h";
        }

        return $"{(int)Math.Floor(ts.TotalDays)}d";
    }

    /// <summary>
    /// OBS-02 (260922-eqy) — the render-log age formatter: a compact whole-number
    /// TRUNCATION of a reading's age for the "ui row rendered" line (mirrors
    /// FormatCompactUntil's band style). Bands: zero-or-negative → "0s"; under 1 min →
    /// "{s}s"; under 60 min → "{m}m"; under 24 h → "{h}h{mm:D2}m"; else "{d}d{h}h".
    /// InvariantCulture digits; no WPF types.
    /// </summary>
    public static string FormatAgeCompact(TimeSpan age)
    {
        if (age <= TimeSpan.Zero)
        {
            return "0s";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return $"{(int)Math.Floor(age.TotalSeconds)}s";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)Math.Floor(age.TotalMinutes)}m";
        }

        if (age < TimeSpan.FromHours(24))
        {
            int hours = (int)Math.Floor(age.TotalHours);
            return $"{hours}h{age.Minutes:D2}m";
        }

        int days = (int)Math.Floor(age.TotalDays);
        return $"{days}d{age.Hours}h";
    }

    /// <summary>
    /// First AllWindows entry whose Kind equals MostBindingWindow; else null.
    /// </summary>
    public static DateTimeOffset? LookupMostBindingReset(UsageReading reading)
    {
        if (reading.AllWindows is not { Count: > 0 } all)
        {
            return null;
        }

        foreach (WindowReading w in all)
        {
            if (w.Kind == reading.MostBindingWindow)
            {
                return w.ResetsAtUtc;
            }
        }

        return null;
    }

    /// <summary>Locked DATA-02 chip vocabulary.</summary>
    public static string ChipLabel(WindowKind kind) => kind switch
    {
        WindowKind.Rolling => "ROLL",
        WindowKind.FiveHour => "5H",
        WindowKind.Weekly => "WEEK",
        WindowKind.Monthly => "MONTH",
        WindowKind.Credits => "CRED",
        _ => string.Empty,
    };

    /// <summary>
    /// UIR-04 tooltip line: "{ChipLabel}: {RemainingPct:F0}% remaining / {UsedPct:F0}% used"
    /// and, when ResetsAtUtc is in the future, " · resets {local yyyy-MM-dd HH:mm}"
    /// (InvariantCulture). No estimate sentence.
    /// </summary>
    public static string FormatTooltipLine(WindowReading w, DateTimeOffset now)
    {
        string line = $"{ChipLabel(w.Kind)}: {w.RemainingPct:F0}% remaining / {w.UsedPct:F0}% used";
        if (w.ResetsAtUtc is DateTimeOffset reset && reset > now)
        {
            line += $" · resets {reset.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
        }

        return line;
    }

    // ── DAT-01 / DAT-06 — remaining label/value ──────────────────────────────

    /// <summary>DAT-01 — the fixed Chinese prefix for the number column.</summary>
    public const string RemainingPrefix = "剩余";

    /// <summary>
    /// DAT-01 — "剩余 {F0}%". Display precision F0. Caller supplies RemainingPct only
    /// when HasFigure; never coerce null → 0 (DAT-06). Non-finite → null.
    /// </summary>
    public static string? FormatRemainingLabel(double? remainingPct)
    {
        if (remainingPct is not double r || !double.IsFinite(r))
        {
            return null;
        }

        return $"{RemainingPrefix} {Math.Clamp(r, 0, 100):F0}%";
    }

    /// <summary>DAT-01 — value TextBlock only ("62%" / "100%"). Same guards as label.</summary>
    public static string? FormatRemainingValue(double? remainingPct)
    {
        if (remainingPct is not double r || !double.IsFinite(r))
        {
            return null;
        }

        return $"{Math.Clamp(r, 0, 100):F0}%";
    }

    /// <summary>
    /// DAT-05 — unrounded RemainingPct ≤ NearLimitRemainingThreshold (20). NaN/∞/null → false.
    /// </summary>
    public static bool IsLow(double? remainingPct)
    {
        return remainingPct is double r && double.IsFinite(r)
            && r <= UsageReading.NearLimitRemainingThreshold;
    }

    // ── ROW-08 — chip + stale suffix ─────────────────────────────────────────

    /// <summary>ROW-08 — "WEEK" / "WEEK · 旧".</summary>
    public static string ChipWithStaleSuffix(WindowKind kind, bool stale)
    {
        string chip = ChipLabel(kind);
        return stale ? $"{chip} · 旧" : chip;
    }

    // ── DAT-07 — last-update line ────────────────────────────────────────────

    /// <summary>DAT-07 — "Updated {local yyyy-MM-dd HH:mm}" (InvariantCulture).</summary>
    public static string FormatLastUpdateLine(DateTimeOffset fetchedAtUtc)
    {
        return $"Updated {fetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    }
}
