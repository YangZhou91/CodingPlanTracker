namespace PlanMeter.Core.Presentation;

/// <summary>
/// LAY-01..04 / THM-05 / THM-06 — PlanMeter B chrome geometry (DIP @ 96).
/// No WPF types: XAML sites cite these constants in comments; a later x:Static pass
/// can bind them without touching the formulas.
///
/// Width formula:  border(2) + pad(16) + cols(87+72+60+37+22=278) + gaps(6×4=24) = 320
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
    public const double NameW = 87;
    public const double BarW = 72;
    public const double NumberW = 60;
    public const double ChipW = 37;
    public const double ResetW = 22;
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

    /// <summary>Name + gap + bar + gap + number = 231 (REG-02 drag hit target).</summary>
    public static double DragRegionWidth =>
        NameW + ColGap + BarW + ColGap + NumberW;

    /// <summary>
    /// THM-06 — steal 0..3 DIP from the bar column for the chip so "MONTH" can fit
    /// without changing the 109 DIP bar+chip total. Default callers use 0.
    /// </summary>
    public static (double bar, double chip) ChipSteal(double steal)
    {
        double clamped = steal < 0 ? 0 : steal > 3 ? 3 : steal;
        return (BarW - clamped, ChipW + clamped);
    }
}
