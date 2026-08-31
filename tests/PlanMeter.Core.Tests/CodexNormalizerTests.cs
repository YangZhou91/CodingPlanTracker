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
/// CODEX-02 (09-01) — Codex wham/usage normalizer tests, fixture-driven following the
/// OpenCodeNormalizerTests pattern. Deltas vs OpenCode: empty → Error (D-11, do not
/// copy Q1 empty→Ok); duration-band classify else→Rolling (D-10); figure is
/// argmin(remaining) then WindowPriority (D-09).
/// </summary>
public sealed class CodexNormalizerTests
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
    public void Happy_weekly_fixture_normalises_to_Weekly_used_61()
    {
        // Live 2026-08-19 used_percent 61 / RESEARCH A1 604800s weekly band.
        string json = LoadFixture("codex-wham-primary-weekly.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        UsageReading reading = CodexNormalizer.Normalize(json, fetchedAt);

        reading.Provider.Should().Be("Codex");
        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.Weekly);
        reading.UsedPct.Should().BeApproximately(61.0, 0.01);
        reading.RemainingPct.Should().BeApproximately(39.0, 0.01);
        reading.FetchedAtUtc.Should().Be(fetchedAt);
        reading.ErrorMessage.Should().BeNull();
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(1);
        reading.AllWindows[0].Kind.Should().Be(WindowKind.Weekly);
        reading.AllWindows[0].ResetsAtUtc.Should().Be(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Two_window_fixture_figure_is_argmin_remaining_not_hardwired_primary()
    {
        // primary 40% used / 60% rem Weekly; secondary 75% used / 25% rem FiveHour.
        // argmin(remaining) = secondary — must not hard-wire primary (D-09).
        string json = LoadFixture("codex-wham-primary-and-secondary.json");

        UsageReading reading = CodexNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour,
            "secondary remaining 25 < primary remaining 60 — figure is argmin, not primary");
        reading.UsedPct.Should().BeApproximately(75.0, 0.01);
        reading.RemainingPct.Should().BeApproximately(25.0, 0.01);
        reading.AllWindows.Should().NotBeNull();
        reading.AllWindows!.Count.Should().Be(2);
    }

    [Fact]
    public void Empty_rate_limit_fixture_maps_to_Error_no_usable_window_data()
    {
        string json = LoadFixture("codex-wham-empty.json");

        UsageReading reading = CodexNormalizer.Normalize(json, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Error, "D-11: empty rate_limit is Error, not Ok");
        reading.ErrorMessage.Should().Contain("no usable window data");
        reading.UsedPct.Should().BeNull();
        reading.RemainingPct.Should().BeNull();
    }

    [Fact]
    public void Missing_used_percent_is_skipped_remaining_valid_window_is_Ok()
    {
        var envelope = new CodexUsageEnvelope
        {
            RateLimit = new CodexRateLimit
            {
                PrimaryWindow = new CodexWindow { UsedPercent = null, LimitWindowSeconds = 604800 },
                SecondaryWindow = new CodexWindow { UsedPercent = 10, LimitWindowSeconds = 18000 },
            },
        };

        UsageReading reading = CodexNormalizer.Normalize(envelope, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour);
        reading.UsedPct.Should().BeApproximately(10.0, 0.01);
        reading.AllWindows!.Count.Should().Be(1, "the window with no used_percent is skipped entirely");
    }

    [Fact]
    public void All_invalid_windows_return_Error()
    {
        var envelope = new CodexUsageEnvelope
        {
            RateLimit = new CodexRateLimit
            {
                PrimaryWindow = new CodexWindow { UsedPercent = null, LimitWindowSeconds = 604800 },
                SecondaryWindow = new CodexWindow { UsedPercent = null, LimitWindowSeconds = 18000 },
            },
        };

        UsageReading reading = CodexNormalizer.Normalize(envelope, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.ErrorMessage.Should().Contain("no usable window data");
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public void Used_80_returns_NearLimit()
    {
        var envelope = new CodexUsageEnvelope
        {
            RateLimit = new CodexRateLimit
            {
                PrimaryWindow = new CodexWindow { UsedPercent = 80, LimitWindowSeconds = 604800 },
            },
        };

        UsageReading reading = CodexNormalizer.Normalize(envelope, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.NearLimit,
            "remaining == 20.0 is exactly at the NearLimit threshold (<= 20.0)");
        reading.RemainingPct.Should().BeApproximately(20.0, 0.01);
    }

    [Fact]
    public void Tie_break_equal_remaining_FiveHour_beats_Weekly()
    {
        var envelope = new CodexUsageEnvelope
        {
            RateLimit = new CodexRateLimit
            {
                PrimaryWindow = new CodexWindow { UsedPercent = 50, LimitWindowSeconds = 604800 },
                SecondaryWindow = new CodexWindow { UsedPercent = 50, LimitWindowSeconds = 18000 },
            },
        };

        UsageReading reading = CodexNormalizer.Normalize(envelope, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Ok);
        reading.MostBindingWindow.Should().Be(WindowKind.FiveHour,
            "on an equal-remaining tie WindowPriority FiveHour=1 beats Weekly=2");
        reading.UsedPct.Should().BeApproximately(50.0, 0.01);
        reading.AllWindows!.Count.Should().Be(2);
    }

    [Fact]
    public void Null_envelope_input_returns_Error_reading()
    {
        UsageReading reading = CodexNormalizer.Normalize((CodexUsageEnvelope?)null, DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public void String_overload_matches_typed_overload_result()
    {
        string json = LoadFixture("codex-wham-primary-weekly.json");
        var fetchedAt = DateTimeOffset.UtcNow;

        var envelope = JsonSerializer.Deserialize<CodexUsageEnvelope>(json);

        UsageReading fromString = CodexNormalizer.Normalize(json, fetchedAt);
        UsageReading fromTyped = CodexNormalizer.Normalize(envelope, fetchedAt);

        fromString.Status.Should().Be(fromTyped.Status);
        fromString.UsedPct.Should().Be(fromTyped.UsedPct);
        fromString.MostBindingWindow.Should().Be(fromTyped.MostBindingWindow);
        fromString.AllWindows!.Count.Should().Be(fromTyped.AllWindows!.Count);
    }

    [Fact]
    public void String_overload_empty_body_returns_Error()
    {
        UsageReading reading = CodexNormalizer.Normalize("   ", DateTimeOffset.UtcNow);

        reading.Status.Should().Be(ReadingStatus.Error);
        reading.UsedPct.Should().BeNull();
    }

    [Fact]
    public void String_overload_malformed_fixture_throws_JsonException()
    {
        string rawJson = LoadFixture("codex-wham-malformed.json");
        var act = () => CodexNormalizer.Normalize(rawJson, DateTimeOffset.UtcNow);
        act.Should().Throw<JsonException>(
            "a hard type mismatch surfaces as JsonException from the string overload — the adapter's catch maps it to Error");
    }

    [Theory]
    [InlineData(18000.0, WindowKind.FiveHour)]
    [InlineData(604800.0, WindowKind.Weekly)]
    [InlineData(2592000.0, WindowKind.Monthly)]
    [InlineData(10800.0, WindowKind.Rolling)]
    [InlineData(7776000.0, WindowKind.Rolling)]
    [InlineData(null, WindowKind.Rolling)]
    public void Classify_duration_bands(double? seconds, WindowKind expected)
    {
        CodexNormalizer.Classify(seconds).Should().Be(expected, "D-10 duration-band table else→Rolling");
    }
}
