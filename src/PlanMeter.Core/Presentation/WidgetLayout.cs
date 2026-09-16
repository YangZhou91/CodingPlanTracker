namespace PlanMeter.Core.Presentation;

/// <summary>
/// Applied PlanMeter B chrome geometry (DIP @ 96) after live font-measure tweaks.
/// No WPF types: XAML sites cite these constants in comments.
///
/// Width formula:  border(2) + pad(16) + cols(56+64+64+45+26=255) + gaps(6×4=24) = 297
/// Height formula: border(2) + pad(12) + rows(26×N); N=5 → 144
/// </summary>
public static class WidgetLayout
{
    public const double Border = 1;
    public const double CornerRadius = 7;
    public const double PadH = 8;
    public const double PadV = 6;
    public const double RowHeight = 26;
    public const double ColGap = 6;
    /// <summary>Name column — short labels leave a small gap before the bar.</summary>
    public const double NameW = 56;
    /// <summary>Quota bar column (left-aligned, fixed length).</summary>
    public const double BarW = 64;
    /// <summary>Remaining label region after the bar (剩余 100% fits).</summary>
    public const double NumberW = 64;
    /// <summary>Period chip column so WEEK/MONTH fit.</summary>
    public const double ChipW = 45;
    /// <summary>Compact reset column so "now"/"6d" fit.</summary>
    public const double ResetW = 26;
    public const double BarHeight = 4;
    public const double BarRadius = 3;
    public const int StandardRowCount = 5;

    /// <summary>Name + bar + remaining + chip + reset + four 6 DIP gaps = 279.</summary>
    public static double ContentWidth =>
        NameW + BarW + NumberW + ChipW + ResetW + 4 * ColGap;

    /// <summary>Content + two pads + two borders = 297.</summary>
    public static double WindowWidth =>
        ContentWidth + 2 * PadH + 2 * Border;

    /// <summary>rows × 26 + two pads + two borders; 5 → 144.</summary>
    public static double WindowHeight(int rows) =>
        rows * RowHeight + 2 * PadV + 2 * Border;

    /// <summary>Name + gap + bar + gap + remaining = 196 (REG-02 drag hit target).</summary>
    public static double DragRegionWidth =>
        NameW + ColGap + BarW + ColGap + NumberW;

    /// <summary>
    /// THM-06 — further steal 0..3 DIP from the applied bar column for the chip.
    /// bar+chip stays 109. Callers use 0 (applied 64/45 already font-measured).
    /// </summary>
    public static (double bar, double chip) ChipSteal(double steal)
    {
        double clamped = steal < 0 ? 0 : steal > 3 ? 3 : steal;
        return (BarW - clamped, ChipW + clamped);
    }
}
