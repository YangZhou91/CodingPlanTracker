using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Adapters;

/// <summary>
/// Defensive DTOs for the OpenCode GO <c>zen/go/v1/usage</c> response.
/// Tolerant: nullable fields, <see cref="JsonExtensionData"/> on every DTO.
/// </summary>
public sealed class OpenCodeUsageEnvelope
{
    [JsonPropertyName("usage")]
    public OpenCodeUsage? Usage { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class OpenCodeUsage
{
    [JsonPropertyName("rolling")]
    public OpenCodeWindow? Rolling { get; set; }

    [JsonPropertyName("weekly")]
    public OpenCodeWindow? Weekly { get; set; }

    [JsonPropertyName("monthly")]
    public OpenCodeWindow? Monthly { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class OpenCodeWindow
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("percent")]
    public double? Percent { get; set; }

    [JsonPropertyName("resetsAt")]
    public DateTimeOffset? ResetsAt { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Maps a parsed <see cref="OpenCodeUsageEnvelope"/> to the uniform <see cref="UsageReading"/>.
/// Design mirrors ZaiNormalizer/MinimaxNormalizer (04-PATTERNS Pattern 2):
/// <list type="bullet">
///   <item>Per window: skip if Percent is null; clamp to [0.0, 100.0].</item>
///   <item>D-07: windows whose Status is not "ok" are excluded from figure selection but remain in AllWindows.</item>
///   <item>D-06: most-binding window = argmin(remainingPct), then WindowPriority tie-break (Rolling=0, FiveHour=1, Weekly=2, Monthly=3).</item>
///   <item>Q1 (Z.ai parity): NOTHING parseable at all — an empty usage object or an envelope whose window fields are all absent/null — maps to Ok with a null figure (renders "—"), never an error.</item>
///   <item>D-08: windows were reported but none is usable (every Status non-"ok") -> Error reading "OpenCode GO returned no usable window data."; AllWindows stays populated (tooltip visibility).</item>
///   <item>Status = NearLimit when mostBindingRemaining &lt;= 20, else Ok.</item>
/// </list>
/// </summary>
public static class OpenCodeNormalizer
{
    private const string OkStatus = "ok";

    /// <summary>
    /// Normalise a parsed OpenCode usage envelope into a <see cref="UsageReading"/>.
    /// </summary>
    public static UsageReading Normalize(OpenCodeUsageEnvelope? envelope, DateTimeOffset fetchedAtUtc)
    {
        if (envelope is null)
        {
            return ErrorReading(fetchedAtUtc, "OpenCode GO returned an empty envelope.");
        }

        var windows = new List<WindowReading>();
        var allWindows = new List<WindowReading>();

        if (envelope.Usage is not null)
        {
            AddWindow(envelope.Usage.Rolling, WindowKind.Rolling, windows, allWindows);
            AddWindow(envelope.Usage.Weekly, WindowKind.Weekly, windows, allWindows);
            AddWindow(envelope.Usage.Monthly, WindowKind.Monthly, windows, allWindows);
        }

        // Q1 (Z.ai parity — ZaiNormalizer empty-data handling): NOTHING was parseable —
        // an empty usage object (`{"usage":{}}`) or an envelope whose window fields are
        // all absent/null. This is "no usage yet", not a failure: map to Ok with a null
        // figure (the row renders "—"), never an error.
        if (windows.Count == 0 && allWindows.Count == 0)
        {
            return new UsageReading(
                Provider: "OpenCode GO",
                FetchedAtUtc: fetchedAtUtc,
                Status: ReadingStatus.Ok,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: Array.Empty<WindowReading>(),
                ErrorMessage: null);
        }

        // D-08: windows WERE reported but none is usable for figure selection (every
        // Status is non-"ok") -> Error reading. Never a fabricated number; the non-OK
        // windows stay visible in the tooltip via AllWindows.
        if (windows.Count == 0)
        {
            return new UsageReading(
                Provider: "OpenCode GO",
                FetchedAtUtc: fetchedAtUtc,
                Status: ReadingStatus.Error,
                UsedPct: null,
                RemainingPct: null,
                MostBindingWindow: default,
                AllWindows: allWindows,
                ErrorMessage: "OpenCode GO returned no usable window data.");
        }

        // D-06: most-binding = argmin(remainingPct). Tie-break: Rolling wins.
        var mostBinding = windows
            .OrderBy(w => w.RemainingPct)
            .ThenBy(w => WindowPriority(w.Kind))
            .First();

        var status = mostBinding.RemainingPct <= UsageReading.NearLimitRemainingThreshold
            ? ReadingStatus.NearLimit
            : ReadingStatus.Ok;

        return new UsageReading(
            Provider: "OpenCode GO",
            FetchedAtUtc: fetchedAtUtc,
            Status: status,
            UsedPct: mostBinding.UsedPct,
            RemainingPct: mostBinding.RemainingPct,
            MostBindingWindow: mostBinding.Kind,
            AllWindows: allWindows,
            ErrorMessage: null);
    }

    private static void AddWindow(
        OpenCodeWindow? window,
        WindowKind kind,
        List<WindowReading> usable,
        List<WindowReading> allWindows)
    {
        if (window is null)
        {
            return;
        }

        if (!window.Percent.HasValue)
        {
            // No percent -> the window is unparseable (D-08 sense): WindowReading requires
            // a numeric UsedPct, so there is nothing to show in the tooltip either. Skip
            // entirely — never throw on a shape change.
            return;
        }

        double usedPct = Math.Clamp(window.Percent.Value, 0.0, 100.0);
        double remainingPct = 100.0 - usedPct;
        var reading = new WindowReading(kind, usedPct, remainingPct, window.ResetsAt);
        allWindows.Add(reading);

        // D-07: only windows with status "ok" are usable for figure selection.
        bool isOk = string.Equals(window.Status, OkStatus, StringComparison.OrdinalIgnoreCase);
        if (isOk)
        {
            usable.Add(reading);
        }
    }

    /// <summary>Tie-break priority -- lower wins. Rolling=0, FiveHour=1, Weekly=2, Monthly=3, _=4.</summary>
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
            Provider: "OpenCode GO",
            FetchedAtUtc: fetchedAtUtc,
            Status: ReadingStatus.Error,
            UsedPct: null,
            RemainingPct: null,
            MostBindingWindow: default,
            AllWindows: null,
            ErrorMessage: message);

    /// <summary>
    /// Convenience overload -- parse JSON then normalise. Throws <see cref="JsonException"/>
    /// upward so the adapter can map malformed JSON to an Error reading.
    /// </summary>
    public static UsageReading Normalize(string json, DateTimeOffset fetchedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ErrorReading(fetchedAtUtc, "OpenCode GO returned an empty body.");
        }

        var envelope = JsonSerializer.Deserialize<OpenCodeUsageEnvelope>(json);
        return Normalize(envelope, fetchedAtUtc);
    }
}