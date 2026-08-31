using System;
using System.Globalization;
using FluentAssertions;
using PlanMeter.Core.Models;
using PlanMeter.Core.Presentation;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// UIR-01 / UIR-02 / UIR-04 Wave 0 pins for the Core quota-row formatter.
/// Fill identity is remaining, never used. Compact until-reset has a days band.
/// Tooltip lines carry used% + a full local timestamp; never an estimate sentence.
/// </summary>
public sealed class QuotaRowFormatterTests
{
    [Fact]
    public void RemainingFill_100_is_full_track()
    {
        QuotaRowFormatter.RemainingFill(100).Should().Be((100, 0));
    }

    [Fact]
    public void RemainingFill_0_is_empty_fill()
    {
        QuotaRowFormatter.RemainingFill(0).Should().Be((0, 100));
    }

    [Fact]
    public void RemainingFill_53_is_identity_not_used()
    {
        QuotaRowFormatter.RemainingFill(53).Should().Be((53, 47),
            "used=53 / remaining=47 must fill 47, never 53");
    }

    [Fact]
    public void RemainingFill_47_fills_47_not_53()
    {
        QuotaRowFormatter.RemainingFill(47).Should().Be((47, 53),
            "a used=53 / remaining=47 reading fills 47 not 53");
    }

    [Fact]
    public void RemainingFill_clamps_above_100_to_full()
    {
        QuotaRowFormatter.RemainingFill(150).Should().Be((100, 0));
    }

    [Fact]
    public void RemainingFill_clamps_negative_to_empty()
    {
        QuotaRowFormatter.RemainingFill(-5).Should().Be((0, 100));
    }

    [Fact]
    public void RemainingFill_NaN_maps_to_empty()
    {
        QuotaRowFormatter.RemainingFill(double.NaN).Should().Be((0, 100));
    }

    [Fact]
    public void RemainingFill_positive_infinity_maps_to_empty()
    {
        QuotaRowFormatter.RemainingFill(double.PositiveInfinity).Should().Be((0, 100));
    }

    [Fact]
    public void FormatCompactUntil_null_is_null()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(null, now).Should().BeNull();
    }

    [Fact]
    public void FormatCompactUntil_elapsed_is_null()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddMinutes(-1), now).Should().BeNull();
    }

    [Fact]
    public void FormatCompactUntil_now_or_past_is_null_Credits_empty_slot()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now, now).Should().BeNull(
            "Credits-style empty slot — now-or-past collapses with no placeholder");
    }

    [Fact]
    public void FormatCompactUntil_30s_is_now()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddSeconds(30), now).Should().Be("now");
    }

    [Fact]
    public void FormatCompactUntil_5m_is_5m()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddMinutes(5), now).Should().Be("5m");
    }

    [Fact]
    public void FormatCompactUntil_75m_is_1h()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddMinutes(75), now).Should().Be("1h");
    }

    [Fact]
    public void FormatCompactUntil_5h_is_5h()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddHours(5), now).Should().Be("5h");
    }

    [Fact]
    public void FormatCompactUntil_23h_is_23h()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddHours(23), now).Should().Be("23h");
    }

    [Fact]
    public void FormatCompactUntil_24h_is_1d()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddHours(24), now).Should().Be("1d");
    }

    [Fact]
    public void FormatCompactUntil_48h_is_2d()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddHours(48), now).Should().Be("2d");
    }

    [Fact]
    public void FormatCompactUntil_30h_is_1d()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        QuotaRowFormatter.FormatCompactUntil(now.AddHours(30), now).Should().Be("1d");
    }

    [Fact]
    public void LookupMostBindingReset_returns_Weekly_ResetsAtUtc_when_MostBinding_is_Weekly()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var weeklyReset = now.AddDays(2);
        var reading = new UsageReading(
            "Z.ai",
            now,
            ReadingStatus.Ok,
            80,
            20,
            WindowKind.Weekly,
            new[]
            {
                new WindowReading(WindowKind.FiveHour, 80, 20, now.AddHours(3)),
                new WindowReading(WindowKind.Weekly, 26, 74, weeklyReset),
            },
            null);

        QuotaRowFormatter.LookupMostBindingReset(reading).Should().Be(weeklyReset);
    }

    [Fact]
    public void LookupMostBindingReset_Credits_null_ResetsAtUtc_returns_null()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var reading = new UsageReading(
            "Grok",
            now,
            ReadingStatus.Ok,
            10,
            90,
            WindowKind.Credits,
            new[] { new WindowReading(WindowKind.Credits, 10, 90, null) },
            null);

        QuotaRowFormatter.LookupMostBindingReset(reading).Should().BeNull();
    }

    [Fact]
    public void LookupMostBindingReset_empty_AllWindows_returns_null()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var reading = new UsageReading(
            "Z.ai",
            now,
            ReadingStatus.Ok,
            null,
            null,
            WindowKind.FiveHour,
            Array.Empty<WindowReading>(),
            null);

        QuotaRowFormatter.LookupMostBindingReset(reading).Should().BeNull();
    }

    [Fact]
    public void FormatTooltipLine_future_reset_includes_local_timestamp()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var reset = now.AddHours(5);
        var window = new WindowReading(WindowKind.Weekly, 26, 74, reset);

        string line = QuotaRowFormatter.FormatTooltipLine(window, now);
        string expectedStamp = reset.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        line.Should().Be($"WEEK: 26% used · resets {expectedStamp}");
        line.Should().NotContain("Estimated");
    }

    [Fact]
    public void FormatTooltipLine_Credits_null_reset_omits_reset_segment()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var window = new WindowReading(WindowKind.Credits, 10, 90, null);

        string line = QuotaRowFormatter.FormatTooltipLine(window, now);

        line.Should().Be("CRED: 10% used");
        line.Should().NotContain("resets");
        line.Should().NotContain("Estimated");
    }

    [Fact]
    public void FormatTooltipLine_elapsed_reset_omits_reset_segment()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var window = new WindowReading(WindowKind.FiveHour, 40, 60, now.AddMinutes(-5));

        string line = QuotaRowFormatter.FormatTooltipLine(window, now);

        line.Should().Be("5H: 40% used");
        line.Should().NotContain("resets");
        line.Should().NotContain("Estimated");
    }

    [Fact]
    public void ChipLabel_maps_locked_DATA02_tokens()
    {
        QuotaRowFormatter.ChipLabel(WindowKind.Rolling).Should().Be("ROLL");
        QuotaRowFormatter.ChipLabel(WindowKind.FiveHour).Should().Be("5H");
        QuotaRowFormatter.ChipLabel(WindowKind.Weekly).Should().Be("WEEK");
        QuotaRowFormatter.ChipLabel(WindowKind.Monthly).Should().Be("MONTH");
        QuotaRowFormatter.ChipLabel(WindowKind.Credits).Should().Be("CRED");
    }
}
