using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// Defensive DTOs for the MiniMax <c>/v1/token_plan/remains</c> response.
/// The endpoint is undocumented but spike-confirmed (D-01/D-02, BRANCH A).
/// These DTOs are tolerant: nullable fields, <see cref="JsonExtensionData"/> for
/// unknown fields — never throw on a shape change.
/// </summary>
public sealed class MinimaxEnvelope
{
    [JsonPropertyName("model_remains")]
    public List<MinimaxModelEntry>? ModelRemains { get; set; }

    [JsonPropertyName("base_resp")]
    public MinimaxBaseResp? BaseResp { get; set; }
}

public sealed class MinimaxBaseResp
{
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("status_msg")]
    public string? StatusMsg { get; set; }
}

/// <summary>
/// One per-model entry in the MiniMax token-plan response. Each entry carries BOTH
/// an interval (5h) window AND a weekly window — the normalizer extracts TWO
/// <see cref="WindowReading"/>s per entry.
///
/// Field semantics (spike-confirmed, Pitfall 1): the percentage fields report REMAINING,
/// not consumed. <c>current_interval_remaining_percent</c> and
/// <c>current_weekly_remaining_percent</c> are the percent LEFT in the window.
/// </summary>
public sealed class MinimaxModelEntry
{
    /// <summary>Window start epoch-ms (interval).</summary>
    [JsonPropertyName("start_time")]
    public long? StartTime { get; set; }

    /// <summary>Window end epoch-ms (interval).</summary>
    [JsonPropertyName("end_time")]
    public long? EndTime { get; set; }

    /// <summary>Remaining time in the interval (ms).</summary>
    [JsonPropertyName("remains_time")]
    public long? RemainsTime { get; set; }

    /// <summary>Interval total quota count.</summary>
    [JsonPropertyName("current_interval_total_count")]
    public long? CurrentIntervalTotalCount { get; set; }

    /// <summary>Interval used quota count.</summary>
    [JsonPropertyName("current_interval_usage_count")]
    public long? CurrentIntervalUsageCount { get; set; }

    /// <summary>Model name (e.g. "general", "video").</summary>
    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    /// <summary>Weekly total quota count.</summary>
    [JsonPropertyName("current_weekly_total_count")]
    public long? CurrentWeeklyTotalCount { get; set; }

    /// <summary>Weekly used quota count.</summary>
    [JsonPropertyName("current_weekly_usage_count")]
    public long? CurrentWeeklyUsageCount { get; set; }

    /// <summary>Weekly window start epoch-ms.</summary>
    [JsonPropertyName("weekly_start_time")]
    public long? WeeklyStartTime { get; set; }

    /// <summary>Weekly window end epoch-ms.</summary>
    [JsonPropertyName("weekly_end_time")]
    public long? WeeklyEndTime { get; set; }

    /// <summary>Weekly remaining time (ms).</summary>
    [JsonPropertyName("weekly_remains_time")]
    public long? WeeklyRemainsTime { get; set; }

    /// <summary>
    /// Interval window status (observed values: 1, 3). Not load-bearing for v1 —
    /// classification is via the remaining-percent field.
    /// </summary>
    [JsonPropertyName("current_interval_status")]
    public int? CurrentIntervalStatus { get; set; }

    /// <summary>
    /// REMAINING-semantics (Pitfall 1, spike-confirmed): percent of the interval
    /// window LEFT. UsedPct = 100 - remaining, clamped.
    /// </summary>
    [JsonPropertyName("current_interval_remaining_percent")]
    public double? CurrentIntervalRemainingPercent { get; set; }

    /// <summary>
    /// REMAINING-semantics (Pitfall 1, spike-confirmed): percent of the weekly
    /// window LEFT. UsedPct = 100 - remaining, clamped.
    /// </summary>
    [JsonPropertyName("current_weekly_remaining_percent")]
    public double? CurrentWeeklyRemainingPercent { get; set; }

    /// <summary>
    /// Weekly window status (observed values: 1, 3). Not load-bearing for v1.
    /// </summary>
    [JsonPropertyName("current_weekly_status")]
    public int? CurrentWeeklyStatus { get; set; }

    /// <summary>Weekly boost permille (not load-bearing for v1).</summary>
    [JsonPropertyName("weekly_boost_permille")]
    public int? WeeklyBoostPermille { get; set; }

    /// <summary>
    /// Defensive: any future field MiniMax adds is captured here rather than throwing.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Maps a parsed <see cref="MinimaxEnvelope"/> to the uniform <see cref="UsageReading"/>.
/// Design mirrors ZaiNormalizer verbatim (04-PATTERNS Pattern 2):
/// <list type="bullet">
///   <item>Per entry: extract TWO windows (interval = FiveHour from current_interval_remaining_percent;
///         weekly from current_weekly_remaining_percent).</item>
///   <item>REMAINING-semantics (Pitfall 1): UsedPct = 100 - remaining, clamped to [0.0, 100.0].</item>
///   <item>MostBindingWindow = argmin(remainingPct); on a tie, FiveHour wins.</item>
///   <item>Status = Ok if mostBindingRemaining &gt; 20, else NearLimit.</item>
///   <item>Empty model_remains[] maps to Status=Ok with UsedPct=null (non-error).</item>
///   <item>FormatFigure reused from ZaiNormalizer (provider-neutral).</item>
/// </list>
/// </summary>
public static class MinimaxNormalizer
{
    /// <summary>
    /// Normalise a parsed MiniMax envelope into a <see cref="UsageReading"/>.
    /// </summary>
    public static UsageReading Normalize(MinimaxEnvelope envelope, DateTimeOffset fetchedAtUtc)
    {
        if (envelope is null)
        {
            return ErrorReading(fetchedAtUtc, "MiniMax returned an empty envelope.");
        }

        var windows = new List<WindowReading>();

        if (envelope.ModelRemains is { Count: > 0 } entries)
        {
            foreach (var entry in entries)
            {
                // Extract interval (FiveHour) window
                if (entry.CurrentIntervalRemainingPercent.HasValue)
                {
                    double remaining = Math.Clamp(entry.CurrentIntervalRemainingPercent.Value, 0.0, 100.0);
                    double used = Math.Clamp(100.0 - remaining, 0.0, 100.0);
                    DateTimeOffset? resetsAt = entry.EndTime.HasValue
                        ? DateTimeOffset.FromUnixTimeMilliseconds(entry.EndTime.Value)
                        : null;
                    windows.Add(new WindowReading(WindowKind.FiveHour, used, remaining, resetsAt));
                }

                // Extract weekly window
                if (entry.CurrentWeeklyRemainingPercent.HasValue)
                {
                    double remaining = Math.Clamp(entry.CurrentWeeklyRemainingPercent.Value, 0.0, 100.0);
                    double used = Math.Clamp(100.0 - remaining, 0.0, 100.0);
                    DateTimeOffset? resetsAt = entry.WeeklyEndTime.HasValue
                        ? DateTimeOffset.FromUnixTimeMilliseconds(entry.WeeklyEndTime.Value)
                        : null;
                    windows.Add(new WindowReading(WindowKind.Weekly, used, remaining, resetsAt));
                }
            }
        }

        if (windows.Count == 0)
        {
            // Empty model_remains (or no entries with percentage fields) — non-error,
            // same as Z.ai Q1: Ok with null UsedPct.
            return new UsageReading(
                Provider: "MiniMax",
                FetchedAtUtc: fetchedAtUtc,
                Status: ReadingStatus.Ok,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: Array.Empty<WindowReading>(),
                ErrorMessage: null);
        }

        // Most-binding = argmin(remainingPct). Tie-break: FiveHour wins (resets sooner).
        var mostBinding = windows
            .OrderBy(w => w.RemainingPct)
            .ThenBy(w => WindowPriority(w.Kind))
            .First();

        var status = mostBinding.RemainingPct <= UsageReading.NearLimitRemainingThreshold
            ? ReadingStatus.NearLimit
            : ReadingStatus.Ok;

        return new UsageReading(
            Provider: "MiniMax",
            FetchedAtUtc: fetchedAtUtc,
            Status: status,
            UsedPct: mostBinding.UsedPct,
            RemainingPct: mostBinding.RemainingPct,
            MostBindingWindow: mostBinding.Kind,
            AllWindows: windows,
            ErrorMessage: null);
    }

    /// <summary>Tie-break priority — lower wins. Rolling (0) &lt; FiveHour (1) &lt; Weekly (2) &lt; Monthly (3). Matches OpenCodeNormalizer.</summary>
    private static int WindowPriority(WindowKind kind) => kind switch
    {
        WindowKind.Rolling => 0,
        WindowKind.FiveHour => 1,
        WindowKind.Weekly => 2,
        WindowKind.Monthly => 3,
        _ => 4,
    };

    private static UsageReading ErrorReading(DateTimeOffset fetchedAtUtc, string message)
        => new(
            Provider: "MiniMax",
            FetchedAtUtc: fetchedAtUtc,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: message);

    /// <summary>
    /// Convenience overload — parse JSON then normalise. Throws <see cref="JsonException"/>
    /// upward so the adapter can map malformed JSON to an Error reading.
    /// </summary>
    public static UsageReading Normalize(string json, DateTimeOffset fetchedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ErrorReading(fetchedAtUtc, "MiniMax returned an empty body.");
        }

        var envelope = JsonSerializer.Deserialize<MinimaxEnvelope>(json);
        return Normalize(envelope ?? new MinimaxEnvelope(), fetchedAtUtc);
    }
}
