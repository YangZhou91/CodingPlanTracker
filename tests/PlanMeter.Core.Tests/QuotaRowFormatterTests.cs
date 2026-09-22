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

        line.Should().Be($"WEEK: 74% remaining / 26% used · resets {expectedStamp}");
        line.Should().NotContain("Estimated");
    }

    [Fact]
    public void FormatTooltipLine_Credits_null_reset_omits_reset_segment()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var window = new WindowReading(WindowKind.Credits, 10, 90, null);

        string line = QuotaRowFormatter.FormatTooltipLine(window, now);

        line.Should().Be("CRED: 90% remaining / 10% used");
        line.Should().NotContain("resets");
        line.Should().NotContain("Estimated");
    }

    [Fact]
    public void FormatTooltipLine_elapsed_reset_omits_reset_segment()
    {
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture);
        var window = new WindowReading(WindowKind.FiveHour, 40, 60, now.AddMinutes(-5));

        string line = QuotaRowFormatter.FormatTooltipLine(window, now);

        line.Should().Be("5H: 60% remaining / 40% used");
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

    // ── DAT-01 / DAT-06 — remaining label/value ──────────────────────────────

    [Fact]
    public void RemainingPrefix_is_Chinese_remaining()
    {
        QuotaRowFormatter.RemainingPrefix.Should().Be("剩余");
    }

    [Fact]
    public void FormatRemainingLabel_100_is_full()
    {
        QuotaRowFormatter.FormatRemainingLabel(100).Should().Be("剩余 100%");
    }

    [Fact]
    public void FormatRemainingLabel_0_is_zero()
    {
        QuotaRowFormatter.FormatRemainingLabel(0).Should().Be("剩余 0%");
    }

    [Fact]
    public void FormatRemainingLabel_62_4_rounds_to_62()
    {
        QuotaRowFormatter.FormatRemainingLabel(62.4).Should().Be("剩余 62%");
    }

    [Fact]
    public void FormatRemainingLabel_20_is_twenty()
    {
        QuotaRowFormatter.FormatRemainingLabel(20).Should().Be("剩余 20%");
    }

    [Fact]
    public void FormatRemainingLabel_null_is_null()
    {
        QuotaRowFormatter.FormatRemainingLabel(null).Should().BeNull();
    }

    [Fact]
    public void FormatRemainingLabel_NaN_is_null()
    {
        QuotaRowFormatter.FormatRemainingLabel(double.NaN).Should().BeNull();
    }

    [Fact]
    public void FormatRemainingLabel_Infinity_is_null()
    {
        QuotaRowFormatter.FormatRemainingLabel(double.PositiveInfinity).Should().BeNull();
        QuotaRowFormatter.FormatRemainingLabel(double.NegativeInfinity).Should().BeNull();
    }

    [Fact]
    public void FormatRemainingValue_matches_label_suffix()
    {
        QuotaRowFormatter.FormatRemainingValue(100).Should().Be("100%");
        QuotaRowFormatter.FormatRemainingValue(0).Should().Be("0%");
        QuotaRowFormatter.FormatRemainingValue(62.4).Should().Be("62%");
        QuotaRowFormatter.FormatRemainingValue(20).Should().Be("20%");
        QuotaRowFormatter.FormatRemainingValue(null).Should().BeNull();
        QuotaRowFormatter.FormatRemainingValue(double.NaN).Should().BeNull();
    }

    // ── DAT-05 — unrounded IsLow ─────────────────────────────────────────────

    [Fact]
    public void IsLow_20_is_true()
    {
        QuotaRowFormatter.IsLow(20).Should().BeTrue();
    }

    [Fact]
    public void IsLow_20_point_0001_is_false()
    {
        QuotaRowFormatter.IsLow(20.0001).Should().BeFalse();
    }

    [Fact]
    public void IsLow_19_point_999_is_true()
    {
        QuotaRowFormatter.IsLow(19.999).Should().BeTrue();
    }

    [Fact]
    public void IsLow_0_is_true()
    {
        QuotaRowFormatter.IsLow(0).Should().BeTrue();
    }

    [Fact]
    public void IsLow_NaN_is_false()
    {
        QuotaRowFormatter.IsLow(double.NaN).Should().BeFalse();
    }

    [Fact]
    public void IsLow_Infinity_is_false()
    {
        QuotaRowFormatter.IsLow(double.PositiveInfinity).Should().BeFalse();
    }

    [Fact]
    public void IsLow_null_is_false()
    {
        QuotaRowFormatter.IsLow(null).Should().BeFalse();
    }

    // ── ROW-08 — chip + stale suffix ─────────────────────────────────────────

    [Fact]
    public void ChipWithStaleSuffix_WEEK_true_is_WEEK_dot_old()
    {
        QuotaRowFormatter.ChipWithStaleSuffix(WindowKind.Weekly, true).Should().Be("WEEK · 旧");
    }

    [Fact]
    public void ChipWithStaleSuffix_WEEK_false_is_WEEK()
    {
        QuotaRowFormatter.ChipWithStaleSuffix(WindowKind.Weekly, false).Should().Be("WEEK");
    }

    [Fact]
    public void ChipWithStaleSuffix_5H_true_is_5H_dot_old()
    {
        QuotaRowFormatter.ChipWithStaleSuffix(WindowKind.FiveHour, true).Should().Be("5H · 旧");
    }

    // ── DAT-07 — FormatLastUpdateLine ────────────────────────────────────────

    [Fact]
    public void FormatLastUpdateLine_exact_local_stamp()
    {
        var fetchedAtUtc = DateTimeOffset.Parse("2026-09-14T18:40:00Z", CultureInfo.InvariantCulture);
        string expected = $"Updated {fetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        QuotaRowFormatter.FormatLastUpdateLine(fetchedAtUtc).Should().Be(expected);
    }

    // ── 260922-eqy / OBS-02 — FormatAgeCompact render-log age bands ──────────

    [Fact]
    public void FormatAgeCompact_zero_is_0s()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.Zero).Should().Be("0s");
    }

    [Fact]
    public void FormatAgeCompact_negative_clamps_to_0s()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromSeconds(-5)).Should().Be("0s",
            "a clock-skewed negative age clamps to 0s — never a minus sign in the log line");
    }

    [Fact]
    public void FormatAgeCompact_45s_is_45s()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromSeconds(45)).Should().Be("45s");
    }

    [Fact]
    public void FormatAgeCompact_under_1min_truncates_seconds()
    {
        // Whole-number truncation: 59.999s renders 59s, never rounds up to 60s.
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromMilliseconds(59999)).Should().Be("59s");
    }

    [Fact]
    public void FormatAgeCompact_13m_is_13m()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromMinutes(13)).Should().Be("13m");
    }

    [Fact]
    public void FormatAgeCompact_under_60min_truncates_minutes()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59))
            .Should().Be("59m");
    }

    [Fact]
    public void FormatAgeCompact_13h07m_is_13h07m()
    {
        // The stale-row scenario's band: hours + zero-padded residual minutes.
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromHours(13) + TimeSpan.FromMinutes(7))
            .Should().Be("13h07m");
    }

    [Fact]
    public void FormatAgeCompact_exactly_60min_is_1h00m()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromHours(1)).Should().Be("1h00m");
    }

    [Fact]
    public void FormatAgeCompact_3d5h_is_3d5h()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromDays(3) + TimeSpan.FromHours(5)).Should().Be("3d5h");
    }

    [Fact]
    public void FormatAgeCompact_exactly_24h_is_1d0h()
    {
        QuotaRowFormatter.FormatAgeCompact(TimeSpan.FromDays(1)).Should().Be("1d0h");
    }
}
