using FluentAssertions;
using PlanMeter.Core.Presentation;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// Geometry pins for the compact PlanMeter B chrome (window 297×144 at 5 rows).
/// </summary>
public sealed class WidgetLayoutTests
{
    [Fact]
    public void ContentWidth_is_279()
    {
        WidgetLayout.ContentWidth.Should().Be(279);
    }

    [Fact]
    public void WindowWidth_is_297()
    {
        WidgetLayout.WindowWidth.Should().Be(297);
    }

    [Fact]
    public void WindowHeight_5_rows_is_144()
    {
        WidgetLayout.WindowHeight(WidgetLayout.StandardRowCount).Should().Be(144);
    }

    [Fact]
    public void WindowHeight_monotonic_in_rows()
    {
        WidgetLayout.WindowHeight(0).Should().Be(14);
        WidgetLayout.WindowHeight(6).Should().Be(170);
        WidgetLayout.WindowHeight(6).Should().BeGreaterThan(WidgetLayout.WindowHeight(5));
    }

    [Fact]
    public void DragRegionWidth_is_196()
    {
        WidgetLayout.DragRegionWidth.Should().Be(196);
    }

    [Fact]
    public void Columns_plus_gaps_plus_padding_plus_border_equals_297()
    {
        double cols = WidgetLayout.NameW + WidgetLayout.BarW + WidgetLayout.NumberW
                      + WidgetLayout.ChipW + WidgetLayout.ResetW;
        double gaps = 4 * WidgetLayout.ColGap;
        double chrome = 2 * WidgetLayout.PadH + 2 * WidgetLayout.Border;
        (cols + gaps + chrome).Should().Be(297);
    }

    [Fact]
    public void Applied_chip_width_fits_WEEK_and_MONTH_labels()
    {
        WidgetLayout.ChipW.Should().BeGreaterThanOrEqualTo(45);
        WidgetLayout.BarW.Should().Be(64);
        (WidgetLayout.BarW + WidgetLayout.ChipW).Should().Be(109);
    }

    [Fact]
    public void Applied_remaining_width_fits_remaining_100_label()
    {
        WidgetLayout.NumberW.Should().Be(64);
        WidgetLayout.NameW.Should().Be(56);
    }

    [Fact]
    public void Applied_reset_width_fits_now_and_compact_days()
    {
        WidgetLayout.ResetW.Should().BeGreaterThanOrEqualTo(26);
    }

    [Fact]
    public void ChipSteal_0_is_applied_64_45()
    {
        WidgetLayout.ChipSteal(0).Should().Be((64, 45));
    }

    [Fact]
    public void ChipSteal_1_2_3_preserves_bar_plus_chip_109()
    {
        foreach (double steal in new[] { 1.0, 2.0, 3.0 })
        {
            var (bar, chip) = WidgetLayout.ChipSteal(steal);
            bar.Should().Be(64 - steal);
            chip.Should().Be(45 + steal);
            (bar + chip).Should().Be(109);
        }
    }

    [Fact]
    public void ChipSteal_clamps_to_0_and_3()
    {
        WidgetLayout.ChipSteal(-1).Should().Be((64, 45));
        WidgetLayout.ChipSteal(4).Should().Be((61, 48));
    }

    [Fact]
    public void BarHeight_is_4_and_radius_3()
    {
        WidgetLayout.BarHeight.Should().Be(4);
        WidgetLayout.BarRadius.Should().Be(3);
    }

    [Fact]
    public void RowHeight_is_26()
    {
        WidgetLayout.RowHeight.Should().Be(26);
    }
}
