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
    /// UIR-04 tooltip line: "{ChipLabel}: {UsedPct:F0}% used" and, when ResetsAtUtc
    /// is in the future, " · resets {local yyyy-MM-dd HH:mm}" (InvariantCulture).
    /// No estimate sentence.
    /// </summary>
    public static string FormatTooltipLine(WindowReading w, DateTimeOffset now)
    {
        string line = $"{ChipLabel(w.Kind)}: {w.UsedPct:F0}% used";
        if (w.ResetsAtUtc is DateTimeOffset reset && reset > now)
        {
            line += $" · resets {reset.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}";
        }

        return line;
    }
}
