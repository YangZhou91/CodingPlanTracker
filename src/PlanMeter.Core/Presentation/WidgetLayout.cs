namespace PlanMeter.Core.Presentation;

/// <summary>
/// LAY-01..04 / THM-05 / THM-06 — PlanMeter B chrome geometry (DIP @ 96).
/// No WPF types: XAML sites cite these constants in comments; a later x:Static pass
/// can bind them without touching the formulas.
///
/// Design base was Name 87 / Bar 72 / Chip 37 / Number 60 / Reset 22. Live measure:
/// chip +8 (WEEK/MONTH), reset +4 ("now"), number +4 and name −8 so 剩余 100% fits
/// after the 6 DIP gap. bar+chip stays 109; window stays 320.
///
/// Width formula:  border(2) + pad(16) + cols(79+64+64+45+26=278) + gaps(6×4=24) = 320
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
    /// <summary>Applied name column (design 87 − 8) — long names ellipsize (design allows).</summary>
    public const double NameW = 79;
    /// <summary>Applied bar column (design 72 − 8 steal for chip).</summary>
    public const double BarW = 64;
    /// <summary>Applied number column (design 60 + 4) so 剩余 100% fits with gap margin.</summary>
    public const double NumberW = 64;
    /// <summary>Applied chip column (design 37 + 8 steal) so WEEK/MONTH fit.</summary>
    public const double ChipW = 45;
    /// <summary>Applied reset column (design 22 + 4 steal) so "now"/"6d" fit.</summary>
    public const double ResetW = 26;
    public const double BarHeight = 4;
    public const double BarRadius = 3;
    public const int StandardRowCount = 5;

    /// <summary>Name + bar + number + chip + reset + four 6 DIP gaps = 302.</summary>
    public static double ContentWidth =>
        NameW + BarW + NumberW + ChipW + ResetW + 4 * ColGap;

    /// <summary>Content + two pads + two borders = 320.</summary>
    public static double WindowWidth =>
        ContentWidth + 2 * PadH + 2 * Border;

    /// <summary>rows × 26 + two pads + two borders; 5 → 144.</summary>
    public static double WindowHeight(int rows) =>
        rows * RowHeight + 2 * PadV + 2 * Border;

    /// <summary>Name + gap + bar + gap + number = 219 (REG-02 drag hit target).</summary>
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
