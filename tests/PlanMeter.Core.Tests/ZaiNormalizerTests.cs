using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// ZAI-01 — Z.ai response normalizer. Plan-1 Plan 01-01 ships the both-windows
/// happy-path fixture + the empty-data (Q1) fixture. Plan 01-02 Task 3 adds the
/// full D-07 6-fixture set (only-5h, only-weekly, unrecognized-meter, zero-remaining)
/// plus the inline edge-case assertions (tie-break, near-limit boundary, clamp).
/// </summary>
public sealed class ZaiNormalizerTests
{
    private static string LoadFixture(string name)
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;
        string path = System.IO.Path.Combine(dir, "Fixtures", name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"fixture {name} not found at {path}");
        }

        return File.ReadAllText(path);
    }

    [Fact]
    public void Both_windows_fixture_normalises_to_FiveHour_when_5h_has_lower_remaining()
    {
        // D-07 fixture set — Plan 01-02 Task 3. The fixture pins the shape:
        // 5h: used=740k/1M = 74%, remaining=26%
        // weekly: used=2.6M/10M = 26%, remaining=74%
        // TIME_LIMIT entry: skipped (non-TOKENS_LIMIT — R1 mitigation).
        // argmin(remaining) → 5h (26% < 74%) → MostBindingWindow = FiveHour, Status = Ok (>20%).
        string json = LoadFixture("zai-quota-limit-both-windows.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        UsageReading reading = ZaiNormalizer.Normalize(json, fetchedAt);

        reading.Provider.Should().Be("Z.ai");
        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour,
            "5h has the lower RemainingPct (26%) → it is the most-binding window");
        reading.UsedPct.Should().BeApproximately(74.0, 0.01);
        reading.RemainingPct.Should().BeApproximately(26.0, 0.01);
        reading.FetchedAtUtc.Should().Be(fetchedAt);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(2,
            "TIME_LIMIT entry must be skipped, leaving the 5h + weekly TOKENS_LIMIT entries");
        reading.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Empty_data_fixture_maps_to_Ok_with_null_figure_Q1()
    {
        // Q1 — empty data:{} maps to Status=Ok with UsedPct=null (non-error, the user
        // simply has no usage yet). Figure renders as "—".
        string json = LoadFixture("zai-quota-limit-empty-data.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        UsageReading reading = ZaiNormalizer.Normalize(json, fetchedAt);

        reading.Status.Should().Be(ReadingStatus.Ok,
            "empty data is not an error — research §C2 / planner Q1 decision");
        reading.UsedPct.Should().BeNull();
        reading.RemainingPct.Should().BeNull();
        reading.AllWindows.Should().NotBeNull().And.BeEmpty();
        reading.ErrorMessage.Should().BeNull();
        ZaiNormalizer.FormatFigure(reading.UsedPct).Should().Be("—");
    }

    [Fact]
    public void Only_5h_fixture_normalises_to_FiveHour_with_single_window()
    {
        // D-07 fixture set — Plan 01-02 Task 3. Only the 5h TOKENS_LIMIT entry is
        // present. MostBindingWindow = FiveHour; AllWindows has exactly one entry.
        string json = LoadFixture("zai-quota-limit-only-5h.json");

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour);
        reading.UsedPct.Should().BeApproximately(74.0, 0.01);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(1, "only the 5h TOKENS_LIMIT entry is present");
    }

    [Fact]
    public void Only_weekly_fixture_normalises_to_Weekly_with_single_window()
    {
        // D-07 fixture set — Plan 01-02 Task 3. Only the weekly TOKENS_LIMIT entry is
        // present. MostBindingWindow = Weekly; AllWindows has exactly one entry.
        string json = LoadFixture("zai-quota-limit-only-weekly.json");

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.Weekly);
        reading.UsedPct.Should().BeApproximately(41.0, 0.01);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(1, "only the weekly TOKENS_LIMIT entry is present");
    }

    [Fact]
    public void Unrecognised_meter_fixture_skips_non_tokens_entries_no_throw()
    {
        // D-07 fixture set — Plan 01-02 Task 3 (R1 mitigation). A TOKENS_LIMIT entry
        // sits alongside an unrecognized TIME_LIMIT entry; the normalizer MUST skip
        // the unrecognized one and NOT throw. The TOKENS_LIMIT entry still parses.
        string json = LoadFixture("zai-quota-limit-unrecognized-meter.json");

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(1,
            "the TIME_LIMIT entry must be skipped, leaving the 5h TOKENS_LIMIT entry");
        reading.UsedPct.Should().BeApproximately(74.0, 0.01);
    }

    [Fact]
    public void Zero_remaining_fixture_normalises_to_FiveHour_near_limit()
    {
        // D-07 fixture set — Plan 01-02 Task 3. The 5h TOKENS_LIMIT entry has
        // used == limit → RemainingPct == 0 → MostBindingWindow = FiveHour, near-limit
        // amber (>=80% used). UI-SPEC E2/figure zero-state.
        string json = LoadFixture("zai-quota-limit-zero-remaining.json");

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour);
        reading.RemainingPct.Should().BeApproximately(0.0, 0.01);
        reading.UsedPct.Should().Be(100.0);
        reading.Status.Should().Be(ReadingStatus.NearLimit,
            "RemainingPct==0 is below the 20% threshold → NearLimit amber");
    }

    [Fact]
    public void Both_windows_picks_weekly_when_weekly_has_lower_remaining()
    {
        // Synthetic case: 5h at 10% used (90% remaining), weekly at 95% used (5% remaining).
        // argmin(remaining) → weekly → MostBindingWindow = Weekly, Status = NearLimit (<=20%).
        string json = /*lang=json,strict*/ """
        {
          "code": 200,
          "msg": "ok",
          "data": {
            "limits": [
              { "type": "TOKENS_LIMIT", "number": 5, "window": 18000, "used": 100, "limit": 1000 },
              { "type": "TOKENS_LIMIT", "number": 7, "window": 604800, "used": 950, "limit": 1000 }
            ]
          },
          "success": true
        }
        """;

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.MostBindingWindow.Should().Be(WindowKind.Weekly);
        reading.Status.Should().Be(ReadingStatus.NearLimit);
        reading.RemainingPct.Should().BeApproximately(5.0, 0.01);
    }

    [Fact]
    public void Tie_break_FiveHour_wins_on_equal_remaining()
    {
        // DATA-02/tie-break truth — when 5h and weekly RemainingPct are EQUAL, FiveHour
        // wins (it resets sooner, so it is the one the user will hit first).
        string json = /*lang=json,strict*/ """
        {
          "code": 200,
          "msg": "ok",
          "data": {
            "limits": [
              { "type": "TOKENS_LIMIT", "number": 5, "window": 18000, "used": 500, "limit": 1000 },
              { "type": "TOKENS_LIMIT", "number": 7, "window": 604800, "used": 500, "limit": 1000 }
            ]
          },
          "success": true
        }
        """;

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour,
            "on a tie FiveHour wins the tie-break (resets sooner)");
        reading.RemainingPct.Should().BeApproximately(50.0, 0.01);
        reading.Status.Should().Be(ReadingStatus.Ok);
    }

    [Fact]
    public void NearLimit_boundary_at_exactly_20_percent_remaining()
    {
        // ZAI-01/boundary truth — NearLimit threshold is RemainingPct <= 20.0 (>=80% used).
        string json = /*lang=json,strict*/ """
        {
          "code": 200,
          "msg": "ok",
          "data": {
            "limits": [
              { "type": "TOKENS_LIMIT", "number": 5, "window": 18000, "used": 800, "limit": 1000 }
            ]
          },
          "success": true
        }
        """;

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.RemainingPct.Should().BeApproximately(20.0, 0.01);
        reading.Status.Should().Be(ReadingStatus.NearLimit, "RemainingPct==20 is the NearLimit boundary");
    }

    [Fact]
    public void Clamp_caps_usedPct_at_100()
    {
        // ZAI-01/precision — UsedPct clamps to [0.0, 100.0]; an over-limit response
        // (>100%) clamps to 100.0.
        string json = /*lang=json,strict*/ """
        {
          "code": 200,
          "msg": "ok",
          "data": {
            "limits": [
              { "type": "TOKENS_LIMIT", "number": 5, "window": 18000, "used": 1500, "limit": 1000 }
            ]
          },
          "success": true
        }
        """;

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.UsedPct.Should().Be(100.0);
        reading.RemainingPct.Should().Be(0.0);
        reading.Status.Should().Be(ReadingStatus.NearLimit);
    }

    [Fact]
    public void Unrecognised_meter_type_is_skipped_not_thrown()
    {
        // DATA-02/window-discrimination truth — entries whose type is unrecognised
        // (e.g. TIME_LIMIT) are skipped, NOT thrown on.
        string json = /*lang=json,strict*/ """
        {
          "code": 200,
          "msg": "ok",
          "data": {
            "limits": [
              { "type": "TIME_LIMIT", "number": 5, "used": 99, "limit": 100 },
              { "type": "FUTURE_LIMIT", "used": 50, "limit": 100 }
            ]
          },
          "success": true
        }
        """;

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok, "no TOKENS_LIMIT entries → empty-windows non-error per Q1");
        reading.UsedPct.Should().BeNull();
        reading.AllWindows.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void FormatFigure_rounds_to_whole_number_with_percent_suffix()
    {
        // DATA-01/figure — UI-SPEC §Copywriting: "74%" not "74.3%".
        ZaiNormalizer.FormatFigure(74.3).Should().Be("74%");
        ZaiNormalizer.FormatFigure(74.5).Should().Be("75%");
        ZaiNormalizer.FormatFigure(0.0).Should().Be("0%");
        ZaiNormalizer.FormatFigure(100.0).Should().Be("100%");
        ZaiNormalizer.FormatFigure(null).Should().Be("—");
    }

    // ---------------------------------------------------------------------------
    // Real-shape regression (260811-rd8): Z.ai's live `api/monitor/usage/quota/limit`
    // response puts the consumed-quota signal in `data.limits[].percentage` + a window
    // discriminator in `data.limits[].unit`. Pre-fix the DTO had neither field, so
    // every real-shape TOKENS_LIMIT entry was silently dropped → Q1 Ok-with-null-figure
    // → widget rendered the grey "—" (the 01-RESEARCH.md line-70 dogfood-curl mandate
    // had never run; this IS that overdue curl). The four tests below pin the fix.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Real_shape_fixture_renders_weekly_20_percent_as_Ok()
    {
        // PRIMARY regression (260811-rd8) — the user's verbatim captured response
        // against a real GLM Coding Pro account with a real weekly 20% reading. Pre-fix
        // this exact response silently rendered the grey "—" because the normalizer
        // dropped every TOKENS_LIMIT entry (no used/limit fields — the real shape uses
        // percentage+unit). Pinned by the dedicated fixture; faithful to the live body
        // (NO secret — the API key is not in the response body).
        string json = LoadFixture("zai-quota-limit-real-shape.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        UsageReading reading = ZaiNormalizer.Normalize(json, fetchedAt);

        reading.Provider.Should().Be("Z.ai");
        reading.Status.Should().Be(ReadingStatus.Ok,
            "weekly 80% remaining > 20% NearLimit threshold");
        reading.MostBindingWindow.Should().Be(WindowKind.Weekly,
            "argmin(remaining) → weekly 80% < 5h 100%");
        reading.UsedPct.Should().BeApproximately(20.0, 0.01,
            "the weekly TOKENS_LIMIT entry carries percentage:20 and is preferred over the absent used/limit");
        reading.RemainingPct.Should().BeApproximately(80.0, 0.01);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(2,
            "the 5h + weekly TOKENS_LIMIT entries are kept; the TIME_LIMIT entry is skipped by the type filter");
        reading.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Real_shape_near_limit_weekly_85_percent()
    {
        // NearLimit mirror of the real-shape path — a synthetic weekly TOKENS_LIMIT
        // entry at percentage:85 (15% remaining <= 20% threshold). Mirrors the existing
        // NearLimit_boundary_at_exactly_20_percent_remaining test for the new field path.
        string json = /*lang=json,strict*/ """
        {
          "code": 200, "msg": "Operation successful", "success": true,
          "data": { "level": "pro", "limits": [
            { "type": "TOKENS_LIMIT", "unit": 6, "number": 1, "percentage": 85 }
          ] }
        }
        """;

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.NearLimit,
            "15% remaining <= 20% threshold");
        reading.MostBindingWindow.Should().Be(WindowKind.Weekly);
        reading.UsedPct.Should().BeApproximately(85.0, 0.01);
        reading.AllWindows!.Count.Should().Be(1);
    }

    [Fact]
    public void Percentage_preferred_over_used_limit_when_both_present()
    {
        // Additive guard — when BOTH percentage AND used/limit are present, percentage
        // WINS (the two can diverge for pro-rated windows; Z.ai's value is authoritative).
        // Pins the "do not recompute when percentage is given" rule so a future refactor
        // that flips the preference order would flip this assertion.
        string json = /*lang=json,strict*/ """
        {
          "code": 200, "msg": "ok", "success": true,
          "data": { "limits": [
            { "type": "TOKENS_LIMIT", "unit": 6, "window": 604800, "used": 500, "limit": 1000, "percentage": 85 }
          ] }
        }
        """;

        UsageReading reading = ZaiNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.UsedPct.Should().BeApproximately(85.0, 0.01,
            "percentage wins when both percentage and used/limit are present — do NOT recompute used/limit*100 (would give 50.0; the two diverge for pro-rated windows)");
        reading.MostBindingWindow.Should().Be(WindowKind.Weekly);
    }

    [Fact]
    public void Percentage_clamps_above_100_and_below_0()
    {
        // Defensive — Math.Clamp caps Z.ai's percentage into [0.0, 100.0]. Mirrors the
        // existing Clamp_caps_usedPct_at_100 test for the new field path.
        string jsonOver = /*lang=json,strict*/ """
        { "code": 200, "msg": "ok", "success": true,
          "data": { "limits": [ { "type": "TOKENS_LIMIT", "unit": 3, "percentage": 150 } ] } }
        """;
        var over = ZaiNormalizer.Normalize(jsonOver, DateTimeOffset.UtcNow);
        over.UsedPct.Should().Be(100.0);
        over.RemainingPct.Should().Be(0.0);
        over.Status.Should().Be(ReadingStatus.NearLimit);

        string jsonUnder = /*lang=json,strict*/ """
        { "code": 200, "msg": "ok", "success": true,
          "data": { "limits": [ { "type": "TOKENS_LIMIT", "unit": 3, "percentage": -5 } ] } }
        """;
        var under = ZaiNormalizer.Normalize(jsonUnder, DateTimeOffset.UtcNow);
        under.UsedPct.Should().Be(0.0);
        under.RemainingPct.Should().Be(100.0);
        under.Status.Should().Be(ReadingStatus.Ok);
    }
}
