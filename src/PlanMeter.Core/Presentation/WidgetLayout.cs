namespace PlanMeter.Core.Presentation;

/// <summary>
/// LAY-01..04 / THM-05 / THM-06 — PlanMeter B chrome geometry (DIP @ 96).
/// No WPF types: XAML sites cite these constants in comments; a later x:Static pass
/// can bind them without touching the formulas.
/// </summary>
public static class WidgetLayout
{
    // Stub — TDD RED. Real values land in the GREEN commit.
    public const double Border = 0;
    public const double CornerRadius = 0;
    public const double PadH = 0;
    public const double PadV = 0;
    public const double RowHeight = 0;
    public const double ColGap = 0;
    public const double NameW = 0;
    public const double BarW = 0;
    public const double NumberW = 0;
    public const double ChipW = 0;
    public const double ResetW = 0;
    public const double BarHeight = 0;
    public const double BarRadius = 0;
    public const int StandardRowCount = 0;

    public static double ContentWidth => 0;
    public static double WindowWidth => 0;
    public static double WindowHeight(int rows) => 0;
    public static double DragRegionWidth => 0;

    public static (double bar, double chip) ChipSteal(double steal) => (0, 0);
}
