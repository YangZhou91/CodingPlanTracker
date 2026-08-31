using System;
using System.IO;
using System.Reflection;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// MINI-01 — MiniMax token-plan normalizer tests. Fixture-pinned against the live spike
/// capture (D-03). Covers the REMAINING-semantics direction pin (Pitfall 1), clamp,
/// empty-windows, most-binding selection, and NearLimit boundary.
/// </summary>
public sealed class MinimaxNormalizerTests
{
    private static string LoadFixture(string name)
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;
        string path = Path.Combine(dir, "Fixtures", name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"fixture {name} not found at {path}");
        }

        return File.ReadAllText(path);
    }

    /// <summary>
    /// Test 1 (fixture pin — THE D-03 deliverable): the live spike capture normalizes
    /// to the correct values derived from the fixture body.
    ///
    /// Fixture values (read from the captured JSON):
    ///   Entry "general":
    ///     interval: current_interval_remaining_percent = 96 -> UsedPct = 100 - 96 = 4
    ///     weekly:   current_weekly_remaining_percent = 98 -> UsedPct = 100 - 98 = 2
    ///   Entry "video":
    ///     interval: current_interval_remaining_percent = 100 -> UsedPct = 0
    ///     weekly:   current_weekly_remaining_percent = 100 -> UsedPct = 0
    ///
    /// Most-binding window = argmin(remaining) -> general interval 96% remaining (lowest)
    /// -> UsedPct = 4, RemainingPct = 96, MostBindingWindow = FiveHour, Status = Ok (>20).
    /// All windows = 4 (2 entries x 2 windows each).
    /// </summary>
    [Fact]
    public void Fixture_pin_live_capture_normalizes_correctly()
    {
        string json = LoadFixture("minimax-token-plan-remains.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        UsageReading reading = MinimaxNormalizer.Normalize(json, fetchedAt);

        reading.Provider.Should().Be("MiniMax");
        reading.Status.Should().Be(ReadingStatus.Ok,
            "most-binding remaining 96% > 20% NearLimit threshold");
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour,
            "general interval has the lowest RemainingPct (96%) -> most-binding");
        reading.UsedPct.Should().BeApproximately(4.0, 0.01,
            "general interval remaining 96% -> UsedPct = 100 - 96 = 4");
        reading.RemainingPct.Should().BeApproximately(96.0, 0.01,
            "general interval remaining 96% (REMAINING-semantics)");
        reading.FetchedAtUtc.Should().Be(fetchedAt);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(4,
            "2 entries x 2 windows each = 4 windows total");
        reading.ErrorMessage.Should().BeNull();
    }

    /// <summary>
    /// Test 2 (direction pin — Pitfall 1): asserts the REMAINING-vs-consumed direction.
    /// The captured fields are REMAINING-semantics (spike-confirmed). UsedPct must equal
    /// 100 minus the reported remaining (clamped). If a future change inverts the
    /// direction, this test fails loudly.
    /// </summary>
    [Fact]
    public void Direction_pin_remaining_semantics_UsedPct_equals_100_minus_remaining_clamped()
    {
        // "general" entry: interval remaining 96 -> UsedPct 4, weekly remaining 98 -> UsedPct 2
        // "video" entry: interval remaining 100 -> UsedPct 0, weekly remaining 100 -> UsedPct 0
        string json = LoadFixture("minimax-token-plan-remains.json");
        UsageReading reading = MinimaxNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        // The most-binding window is the "general" interval with UsedPct = 4.
        reading.UsedPct.Should().BeApproximately(4.0, 0.01,
            "Pitfall 1 direction pin: REMAINING-semantics confirmed. UsedPct = 100 - 96 = 4");

        // Verify individual window directions from AllWindows.
        var allWindows = reading.AllWindows!;
        // General interval: remaining 96, used 4
        var generalInterval = allWindows.FirstOrDefault(w => w.Kind == WindowKind.FiveHour);
        generalInterval.Should().NotBeNull("general interval window should exist");
        generalInterval!.RemainingPct.Should().BeApproximately(96.0, 0.1,
            "general interval: remaining 96 (remaining semantics)");
        generalInterval.UsedPct.Should().BeApproximately(4.0, 0.1,
            "general interval: used 4 = 100 - 96 (remaining semantics)");
        // General weekly: remaining 98, used 2
        var generalWeekly = allWindows.FirstOrDefault(w => w.Kind == WindowKind.Weekly);
        generalWeekly.Should().NotBeNull("general weekly window should exist");
        generalWeekly!.RemainingPct.Should().BeApproximately(98.0, 0.1,
            "general weekly: remaining 98 (remaining semantics)");
        generalWeekly.UsedPct.Should().BeApproximately(2.0, 0.1,
            "general weekly: used 2 = 100 - 98 (remaining semantics)");
    }

    /// <summary>
    /// Test 3: clamp — an out-of-range percentage normalizes into [0,100].
    /// </summary>
    [Fact]
    public void Clamp_out_of_range_remaining_into_valid_range()
    {
        // remaining = -10 -> clamped to 0 -> UsedPct = 100
        string jsonBelow = /*lang=json,strict*/ """
        {
          "model_remains": [
            { "current_interval_remaining_percent": -10, "current_weekly_remaining_percent": 50 }
          ],
          "base_resp": { "status_code": 0, "status_msg": "success" }
        }
        """;
        var below = MinimaxNormalizer.Normalize(jsonBelow, DateTimeOffset.UtcNow);
        below.AllWindows!.Should().Contain(w => w.RemainingPct == 0.0 && w.UsedPct == 100.0,
            "remaining -10 clamped to 0 -> UsedPct 100");

        // remaining = 150 -> clamped to 100 -> UsedPct = 0
        string jsonAbove = /*lang=json,strict*/ """
        {
          "model_remains": [
            { "current_interval_remaining_percent": 150, "current_weekly_remaining_percent": 50 }
          ],
          "base_resp": { "status_code": 0, "status_msg": "success" }
        }
        """;
        var above = MinimaxNormalizer.Normalize(jsonAbove, DateTimeOffset.UtcNow);
        above.AllWindows!.Should().Contain(w => w.RemainingPct == 100.0 && w.UsedPct == 0.0,
            "remaining 150 clamped to 100 -> UsedPct 0");
    }

    /// <summary>
    /// Test 4: empty model_remains -> Status Ok with UsedPct null (non-error, Q1 analogue).
    /// </summary>
    [Fact]
    public void Empty_model_remains_maps_to_Ok_with_null_figure()
    {
        string json = /*lang=json,strict*/ """
        {
          "model_remains": [],
          "base_resp": { "status_code": 0, "status_msg": "success" }
        }
        """;
        UsageReading reading = MinimaxNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok,
            "empty model_remains is not an error — same as Z.ai Q1");
        reading.UsedPct.Should().BeNull();
        reading.RemainingPct.Should().BeNull();
        reading.AllWindows.Should().NotBeNull().And.BeEmpty();
        reading.ErrorMessage.Should().BeNull();
        ZaiNormalizer.FormatFigure(reading.UsedPct).Should().Be("—");
    }

    /// <summary>
    /// Test 5: most-binding selection + NearLimit boundary (RemainingPct exactly 20.0 -> NearLimit).
    /// </summary>
    [Fact]
    public void Most_binding_selection_and_NearLimit_boundary()
    {
        // Interval remaining 90% (used 10%), weekly remaining 20% (used 80%).
        // argmin(remaining) -> weekly (20%) -> NearLimit (exactly at threshold).
        string json = /*lang=json,strict*/ """
        {
          "model_remains": [
            { "current_interval_remaining_percent": 90, "current_weekly_remaining_percent": 20 }
          ],
          "base_resp": { "status_code": 0, "status_msg": "success" }
        }
        """;
        UsageReading reading = MinimaxNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.MostBindingWindow.Should().Be(WindowKind.Weekly,
            "weekly 20% remaining < interval 90% -> weekly is most-binding");
        reading.RemainingPct.Should().BeApproximately(20.0, 0.01);
        reading.Status.Should().Be(ReadingStatus.NearLimit,
            "RemainingPct==20 is the NearLimit boundary");
    }
}
