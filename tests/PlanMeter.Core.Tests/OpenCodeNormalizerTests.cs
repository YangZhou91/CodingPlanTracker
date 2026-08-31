using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using PlanMeter.Core.Adapters;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// OPC-02 (08-01) — OpenCode GO usage normalizer tests, fixture-driven following the
/// ZaiNormalizerTests pattern. Covers: the live-verified 3-window happy path, the Q1
/// empty-usage non-error mapping (Z.ai parity), the D-07 non-OK-status exclusion,
/// the D-08 all-non-OK Error gate, malformed-shape graceful degradation, the D-06
/// WindowPriority tie-break, and both Normalize overloads.
/// </summary>
public sealed class OpenCodeNormalizerTests
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
    public void Happy_path_fixture_normalises_to_Monthly_most_binding_with_three_windows()
    {
        // Live-verified response shape (2026-08-19): rolling 1% / weekly 5% / monthly 47% USED.
        // D-06 rule = argmin(RemainingPct) — the window closest to exhaustion is most-binding:
        // rolling 99% rem, weekly 95% rem, monthly 53% rem → Monthly wins, Status = Ok.
        // (The plan prose's "Rolling is most-binding" annotation computed argmin(used);
        // the normative argmin(remaining) rule — shared with Zai/MiniMax — yields Monthly.)
        string json = LoadFixture("opencode-usage-happy.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        UsageReading reading = OpenCodeNormalizer.Normalize(json, fetchedAt);

        reading.Provider.Should().Be("OpenCode GO");
        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.Monthly,
            "monthly has the lowest RemainingPct (100-47=53) → it is the most-binding window under the D-06 argmin(remaining) rule");
        reading.UsedPct.Should().BeApproximately(47.0, 0.01);
        reading.RemainingPct.Should().BeApproximately(53.0, 0.01);
        reading.FetchedAtUtc.Should().Be(fetchedAt);
        reading.ErrorMessage.Should().BeNull();

        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(3, "rolling + weekly + monthly all parse");

        reading.AllWindows[0].Kind.Should().Be(WindowKind.Rolling);
        reading.AllWindows[0].UsedPct.Should().BeApproximately(1.0, 0.01);
        reading.AllWindows[0].RemainingPct.Should().BeApproximately(99.0, 0.01);
        reading.AllWindows[0].ResetsAtUtc.Should().Be(new DateTimeOffset(2026, 8, 19, 15, 30, 0, TimeSpan.Zero));

        reading.AllWindows[1].Kind.Should().Be(WindowKind.Weekly);
        reading.AllWindows[1].UsedPct.Should().BeApproximately(5.0, 0.01);
        reading.AllWindows[1].RemainingPct.Should().BeApproximately(95.0, 0.01);
        reading.AllWindows[1].ResetsAtUtc.Should().Be(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));

        reading.AllWindows[2].Kind.Should().Be(WindowKind.Monthly);
        reading.AllWindows[2].UsedPct.Should().BeApproximately(47.0, 0.01);
        reading.AllWindows[2].RemainingPct.Should().BeApproximately(53.0, 0.01);
        reading.AllWindows[2].ResetsAtUtc.Should().Be(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Empty_usage_fixture_maps_to_Ok_with_null_figure_Q1()
    {
        // Q1 (Z.ai parity): `{"usage":{}}` has no windows at all — "no usage yet", NOT an
        // error. Status=Ok with a null figure; AllWindows empty.
        string json = LoadFixture("opencode-usage-empty.json");

        UsageReading reading = OpenCodeNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok,
            "an empty usage object is not an error — same Q1 stance as ZaiNormalizer's empty data:{}");
        reading.UsedPct.Should().BeNull();
        reading.RemainingPct.Should().BeNull();
        reading.ErrorMessage.Should().BeNull();
        reading.AllWindows.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Non_ok_status_window_is_excluded_from_figure_but_stays_in_AllWindows()
    {
        // D-07: rolling's status is "exceeded" — excluded from figure selection but still
        // visible in AllWindows. Among the usable windows, monthly (53% remaining) is
        // more binding than weekly (95% remaining) under argmin(remaining) — D-06.
        string json = LoadFixture("opencode-usage-nonok-status.json");

        UsageReading reading = OpenCodeNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.Monthly,
            "the exceeded rolling window must never become the figure (D-07); monthly is the most-binding usable window (53% rem < weekly 95% rem)");
        reading.UsedPct.Should().BeApproximately(47.0, 0.01);
        reading.RemainingPct.Should().BeApproximately(53.0, 0.01);

        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(3, "the non-OK window stays in the tooltip stack (D-07)");
        reading.AllWindows[0].Kind.Should().Be(WindowKind.Rolling);
        reading.AllWindows[0].UsedPct.Should().BeApproximately(100.0, 0.01,
            "the exceeded window keeps its parsed percent for tooltip visibility");
    }

    [Fact]
    public void All_windows_non_ok_returns_Error_with_message_and_populated_AllWindows()
    {
        // D-08: every window status is non-"ok" → Error ("no usable window data"), never
        // a fabricated number — but the non-OK windows stay in AllWindows for the tooltip.
        string json = LoadFixture("opencode-usage-all-nonok.json");

        UsageReading reading = OpenCodeNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("no usable window data");
        reading.UsedPct.Should().BeNull();
        reading.RemainingPct.Should().BeNull();
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(3,
            "non-OK windows with parseable percents remain visible in the tooltip (D-07/D-08)");
    }

    [Fact]
    public void Malformed_shape_degrades_gracefully_without_throwing()
    {
        // Two layers of tolerance:
        // (a) The typed overload with captured-nulls (what tolerant deserialization of a
        //     bad-typed percent yields at the DTO boundary when the adapter feeds fields
        //     that failed to parse as null) must not crash — unparseable windows are
        //     skipped entirely, leaving zero windows → Q1 Ok-with-null-figure.
        // (b) The raw fixture body (percent typed as a non-numeric JSON string) throws
        //     JsonException from the string overload — the documented contract the
        //     ADAPTER catches and maps to an Error reading ("sent a malformed response").
        var envelope = new OpenCodeUsageEnvelope
        {
            Usage = new OpenCodeUsage
            {
                Rolling = new OpenCodeWindow { Status = "ok", Percent = null },
                Weekly = null,
                Monthly = null,
            },
        };

        UsageReading reading = OpenCodeNormalizer.Normalize(envelope, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok,
            "nothing parseable is Q1 (no usage), not an error — graceful degradation");
        reading.UsedPct.Should().BeNull();
        reading.ErrorMessage.Should().BeNull();

        string rawJson = LoadFixture("opencode-usage-malformed.json");
        var act = () => OpenCodeNormalizer.Normalize(rawJson, DateTimeOffset.UtcNow);
        act.Should().Throw<JsonException>(
            "a hard type mismatch surfaces as JsonException from the string overload — the adapter's catch maps it to Error");
    }

    [Fact]
    public void Remaining_exactly_at_threshold_returns_NearLimit()
    {
        var envelope = new OpenCodeUsageEnvelope
        {
            Usage = new OpenCodeUsage
            {
                Rolling = new OpenCodeWindow { Status = "ok", Percent = 80 },
            },
        };
        UsageReading reading = OpenCodeNormalizer.Normalize(envelope, DateTimeOffset.UtcNow);
        reading.Status.Should().Be(ReadingStatus.NearLimit,
            "remaining == 20.0 is exactly at the NearLimit threshold (<= 20.0)");
        reading.RemainingPct.Should().BeApproximately(20.0, 0.01);
    }

    [Fact]
    public void Tie_break_equal_remaining_prefers_lower_WindowPriority_Rolling_over_Weekly()
    {
        // D-06: argmin(remainingPct) ties at 50% remaining → the WindowPriority tie-break
        // decides: Rolling (0) beats Weekly (2) — the sooner-resetting window wins.
        var envelope = new OpenCodeUsageEnvelope
        {
            Usage = new OpenCodeUsage
            {
                Rolling = new OpenCodeWindow { Status = "ok", Percent = 50 },
                Weekly = new OpenCodeWindow { Status = "ok", Percent = 50 },
            },
        };

        UsageReading reading = OpenCodeNormalizer.Normalize(envelope, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.Rolling,
            "on an equal-remaining tie the lower WindowPriority wins (Rolling=0 < Weekly=2)");
        reading.UsedPct.Should().BeApproximately(50.0, 0.01);
        reading.AllWindows!.Count.Should().Be(2);
    }

    [Fact]
    public void Null_envelope_input_returns_Error_reading()
    {
        UsageReading reading = OpenCodeNormalizer.Normalize((OpenCodeUsageEnvelope?)null, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("empty envelope");
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public void String_overload_matches_typed_overload_result()
    {
        // Convenience overload: deserialize then normalize — same result as feeding the
        // parsed envelope to the typed overload.
        string json = LoadFixture("opencode-usage-happy.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        var envelope = JsonSerializer.Deserialize<OpenCodeUsageEnvelope>(json);

        UsageReading fromString = OpenCodeNormalizer.Normalize(json, fetchedAt);
        UsageReading fromTyped = OpenCodeNormalizer.Normalize(envelope, fetchedAt);

        fromString.Status.Should().Be(fromTyped.Status);
        fromString.UsedPct.Should().Be(fromTyped.UsedPct);
        fromString.MostBindingWindow.Should().Be(fromTyped.MostBindingWindow);
        fromString.AllWindows!.Count.Should().Be(fromTyped.AllWindows!.Count);
    }
}
