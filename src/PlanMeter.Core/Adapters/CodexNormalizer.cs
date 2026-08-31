using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// Defensive DTOs for the Codex <c>backend-api/wham/usage</c> response.
/// Tolerant: nullable fields, <see cref="JsonExtensionData"/> on every DTO.
/// Extra unnamed windows are absorbed, not walked (RESEARCH Q2).
/// </summary>
public sealed class CodexUsageEnvelope
{
    [JsonPropertyName("rate_limit")]
    public CodexRateLimit? RateLimit { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class CodexRateLimit
{
    [JsonPropertyName("primary_window")]
    public CodexWindow? PrimaryWindow { get; set; }

    [JsonPropertyName("secondary_window")]
    public CodexWindow? SecondaryWindow { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class CodexWindow
{
    [JsonPropertyName("used_percent")]
    public double? UsedPercent { get; set; }

    [JsonPropertyName("limit_window_seconds")]
    public double? LimitWindowSeconds { get; set; }

    [JsonPropertyName("reset_at")]
    public JsonElement ResetAt { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Maps a parsed <see cref="CodexUsageEnvelope"/> to the uniform <see cref="UsageReading"/>.
/// Deltas vs OpenCodeNormalizer:
/// <list type="bullet">
///   <item>Walk primary + secondary; classify kind via duration bands, not the JSON key.</item>
///   <item>Skip a window with missing used_percent.</item>
///   <item>D-11: zero valid windows → Error "Codex returned no usable window data." — do not copy OpenCode empty→Ok.</item>
///   <item>D-09: figure = argmin(RemainingPct) then WindowPriority (Rolling, FiveHour, Weekly, Monthly).</item>
///   <item>D-10: else → Rolling (do not share ZaiNormalizer.TryClassifyWindow else→Monthly).</item>
/// </list>
/// </summary>
public static class CodexNormalizer
{
    // D-10 duration bands — named constants so a one-line retune is possible.
    internal const double FiveHourMinSeconds = 14400;
    internal const double FiveHourMaxSeconds = 21600;
    internal const double WeeklyMinSeconds = 518400;
    internal const double WeeklyMaxSeconds = 691200;
    internal const double MonthlyMinSeconds = 2332800;
    internal const double MonthlyMaxSeconds = 2764800;

    /// <summary>
    /// Normalise a parsed Codex usage envelope into a <see cref="UsageReading"/>.
    /// </summary>
    public static UsageReading Normalize(CodexUsageEnvelope? envelope, DateTimeOffset fetchedAtUtc)
    {
        if (envelope is null)
        {
            return ErrorReading(fetchedAtUtc, "Codex returned no usable window data.");
        }

        var windows = new List<WindowReading>();

        if (envelope.RateLimit is not null)
        {
            AddWindow(envelope.RateLimit.PrimaryWindow, windows);
            AddWindow(envelope.RateLimit.SecondaryWindow, windows);
        }

        // D-11: empty / zero valid windows is Error — never fabricate a percent.
        if (windows.Count == 0)
        {
            return ErrorReading(fetchedAtUtc, "Codex returned no usable window data.");
        }

        var mostBinding = windows
            .OrderBy(w => w.RemainingPct)
            .ThenBy(w => WindowPriority(w.Kind))
            .First();

        var status = mostBinding.RemainingPct <= UsageReading.NearLimitRemainingThreshold
            ? ReadingStatus.NearLimit
            : ReadingStatus.Ok;

        return new UsageReading(
            Provider: "Codex",
            FetchedAtUtc: fetchedAtUtc,
            Status: status,
            UsedPct: mostBinding.UsedPct,
            RemainingPct: mostBinding.RemainingPct,
            MostBindingWindow: mostBinding.Kind,
            AllWindows: windows,
            ErrorMessage: null);
    }

    /// <summary>
    /// Convenience overload — parse JSON then normalise. Throws <see cref="JsonException"/>
    /// upward so the adapter can map malformed JSON to an Error reading.
    /// Empty/whitespace body → Error (does not throw).
    /// </summary>
    public static UsageReading Normalize(string json, DateTimeOffset fetchedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ErrorReading(fetchedAtUtc, "Codex returned no usable window data.");
        }

        var envelope = JsonSerializer.Deserialize<CodexUsageEnvelope>(json);
        return Normalize(envelope, fetchedAtUtc);
    }

    /// <summary>
    /// D-10 duration-band classifier. Else → Rolling (not Monthly).
    /// </summary>
    internal static WindowKind Classify(double? seconds)
    {
        if (seconds is null)
        {
            return WindowKind.Rolling;
        }

        if (seconds >= FiveHourMinSeconds && seconds <= FiveHourMaxSeconds)
        {
            return WindowKind.FiveHour;
        }

        if (seconds >= WeeklyMinSeconds && seconds <= WeeklyMaxSeconds)
        {
            return WindowKind.Weekly;
        }

        if (seconds >= MonthlyMinSeconds && seconds <= MonthlyMaxSeconds)
        {
            return WindowKind.Monthly;
        }

        return WindowKind.Rolling;
    }

    private static void AddWindow(CodexWindow? window, List<WindowReading> windows)
    {
        if (window is null || !window.UsedPercent.HasValue)
        {
            return;
        }

        double usedPct = Math.Clamp(window.UsedPercent.Value, 0.0, 100.0);
        double remainingPct = 100.0 - usedPct;
        WindowKind kind = Classify(window.LimitWindowSeconds);
        DateTimeOffset? resetsAt = ParseResetAt(window.ResetAt);
        windows.Add(new WindowReading(kind, usedPct, remainingPct, resetsAt));
    }

    private static DateTimeOffset? ParseResetAt(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                string? text = element.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                {
                    return parsed;
                }

                return null;
            }

            case JsonValueKind.Number:
            {
                if (element.TryGetInt64(out long n))
                {
                    return FromUnix(n);
                }

                if (element.TryGetDouble(out double d))
                {
                    return FromUnix((long)d);
                }

                return null;
            }

            default:
                return null;
        }
    }

    private static DateTimeOffset? FromUnix(long value)
    {
        try
        {
            // Values above ~10^12 are unix milliseconds (year ~2001 in seconds is 1e9).
            return value > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Tie-break priority — lower wins. Rolling=0, FiveHour=1, Weekly=2, Monthly=3, _=4.</summary>
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
            Provider: "Codex",
            FetchedAtUtc: fetchedAtUtc,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: message);
}
