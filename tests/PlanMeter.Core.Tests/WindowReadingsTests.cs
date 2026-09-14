using FluentAssertions;
using PlanMeter.Core.Models;
using PlanMeter.Core.Presentation;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// AllWindows tooltip-stack contract: one WindowReading per WindowKind.
/// Oracle: specified (DATA-02 chip vocabulary — duplicate kinds are not distinct windows).
/// </summary>
public sealed class WindowReadingsTests
{
    [Fact]
    public void DistinctMostBindingByKind_empty_is_empty()
    {
        WindowReadings.DistinctMostBindingByKind(Array.Empty<WindowReading>()).Should().BeEmpty();
        WindowReadings.DistinctMostBindingByKind(null).Should().BeEmpty();
    }

    [Fact]
    public void DistinctMostBindingByKind_unique_kinds_passthrough_in_display_order()
    {
        var now = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var weekly = new WindowReading(WindowKind.Weekly, 10, 90, now);
        var fiveHour = new WindowReading(WindowKind.FiveHour, 40, 60, now);

        IReadOnlyList<WindowReading> result = WindowReadings.DistinctMostBindingByKind(
            new[] { weekly, fiveHour });

        result.Select(w => w.Kind).Should().Equal(WindowKind.FiveHour, WindowKind.Weekly);
    }

    [Fact]
    public void DistinctMostBindingByKind_keeps_lower_remaining_of_duplicate_kind()
    {
        var now = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var windows = new[]
        {
            new WindowReading(WindowKind.FiveHour, 4, 96, now),
            new WindowReading(WindowKind.Weekly, 2, 98, now),
            new WindowReading(WindowKind.FiveHour, 0, 100, now),
            new WindowReading(WindowKind.Weekly, 0, 100, now),
        };

        IReadOnlyList<WindowReading> result = WindowReadings.DistinctMostBindingByKind(windows);

        result.Should().HaveCount(2);
        result.Should().ContainSingle(w => w.Kind == WindowKind.FiveHour && w.UsedPct == 4);
        result.Should().ContainSingle(w => w.Kind == WindowKind.Weekly && w.UsedPct == 2);
    }

    [Fact]
    public void DistinctMostBindingByKind_tooltip_lines_are_unique_kind_chips()
    {
        var now = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var windows = new[]
        {
            new WindowReading(WindowKind.FiveHour, 4, 96, now.AddHours(5)),
            new WindowReading(WindowKind.Weekly, 2, 98, now.AddDays(7)),
            new WindowReading(WindowKind.FiveHour, 0, 100, now.AddHours(24)),
            new WindowReading(WindowKind.Weekly, 0, 100, now.AddDays(7)),
        };

        var lines = WindowReadings.DistinctMostBindingByKind(windows)
            .Select(w => QuotaRowFormatter.FormatTooltipLine(w, now))
            .ToList();

        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("5H: 4% used");
        lines[1].Should().StartWith("WEEK: 2% used");
        lines.Should().OnlyHaveUniqueItems();
    }
}
