using FluentAssertions;
using PlanMeter.Core.Presentation;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// LAY-01..04 / THM-05 / THM-06 geometry pins for the PlanMeter B chrome.
/// Content width 302, window 320×144 at 5 Ok rows, row 26, bar 4/r3, ChipSteal invariant.
/// </summary>
public sealed class WidgetLayoutTests
{
    [Fact]
    public void ContentWidth_is_302()
    {
        WidgetLayout.ContentWidth.Should().Be(302);
    }

    [Fact]
    public void WindowWidth_is_320()
    {
        WidgetLayout.WindowWidth.Should().Be(320);
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
    public void DragRegionWidth_is_231()
    {
        WidgetLayout.DragRegionWidth.Should().Be(231);
    }

    [Fact]
    public void Columns_plus_gaps_plus_padding_plus_border_equals_320()
    {
        double cols = WidgetLayout.NameW + WidgetLayout.BarW + WidgetLayout.NumberW
                      + WidgetLayout.ChipW + WidgetLayout.ResetW;
        double gaps = 4 * WidgetLayout.ColGap;
        double chrome = 2 * WidgetLayout.PadH + 2 * WidgetLayout.Border;
        (cols + gaps + chrome).Should().Be(320);
    }

    [Fact]
    public void ChipSteal_0_is_default_72_37()
    {
        WidgetLayout.ChipSteal(0).Should().Be((72, 37));
    }

    [Fact]
    public void ChipSteal_1_2_3_preserves_bar_plus_chip_109()
    {
        foreach (double steal in new[] { 1.0, 2.0, 3.0 })
        {
            var (bar, chip) = WidgetLayout.ChipSteal(steal);
            bar.Should().Be(72 - steal);
            chip.Should().Be(37 + steal);
            (bar + chip).Should().Be(109, "THM-06 keeps bar+chip at the fixed 109 DIP total");
        }
    }

    [Fact]
    public void ChipSteal_clamps_to_0_and_3()
    {
        WidgetLayout.ChipSteal(-1).Should().Be((72, 37));
        WidgetLayout.ChipSteal(4).Should().Be((69, 40));
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
