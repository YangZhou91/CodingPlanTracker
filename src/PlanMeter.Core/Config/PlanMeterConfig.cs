using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PlanMeter.Core.Config;

/// <summary>
/// The startup-read auto-poll interval configuration (D-18/D-19, REFRESH-01). Read ONCE
/// at startup — live-apply is Phase 3, a running app does NOT re-read it.
/// </summary>
/// <param name="IntervalSeconds">The resolved, clamped interval in integer seconds (never below <see cref="PlanMeterConfigLoader.MinIntervalSeconds"/> in Release).</param>
/// <param name="Clamped">True when the configured value was below the floor and was clamped up to it.</param>
/// <param name="Source">Where the value came from: <c>env:PLANMETER_INTERVAL_SECONDS</c>, <c>file:&lt;path&gt;</c>, or <c>default</c>.</param>
public sealed record PlanMeterConfig(int IntervalSeconds, bool Clamped, string? Source);

/// <summary>
/// Loads the <see cref="PlanMeterConfig"/> at startup with precedence
/// env &gt; config file &gt; default (REFRESH-01/config-source), applying the absolute
/// 5-minute floor in Release (REFRESH-01/clamp, SC#3).
///
/// Precedence:
///   1. <c>PLANMETER_INTERVAL_SECONDS</c> (integer seconds; an unparseable value falls through).
///   2. The <c>pollIntervalSeconds</c> field of <c>%LOCALAPPDATA%\PlanMeter\config.json</c>
///      (non-roamed per Pitfall 8 — NOT <c>%APPDATA%</c>). Read with the Pattern E
///      read-fresh <see cref="FileShare.Read"/> open; a missing/unparseable file degrades
///      to the default and NEVER throws (REFRESH-01/empty).
///   3. <see cref="DefaultIntervalSeconds"/> (600s — D-17 unchanged).
///
/// The clamp is ABSOLUTE in Release: any resolved value &lt; <see cref="MinIntervalSeconds"/>
/// is clamped to 300s and logged (<c>interval=N s (clamped to 300 s)</c>). The only bypass
/// is the dev env var <see cref="UnsafeMinIntervalEnvName"/> AND a Debug build — a Release
/// build ignores it (REFRESH-01/dev-bypass), enforced by the App passing
/// <c>debugBuild: false</c> in Release (the <c>#if DEBUG</c> compile gate).
///
/// The on-disk shape is the D-19 seed for Phase 3's ConfigStore: just
/// <c>{ "pollIntervalSeconds": 600 }</c>. Do NOT add fields here — Phase 3 grows it.
/// </summary>
public static class PlanMeterConfigLoader
{
    /// <summary>D-17 — the default background interval, unchanged from Phase 1's 10 min.</summary>
    public const int DefaultIntervalSeconds = 600;

    /// <summary>D-18 — the 5-minute floor is absolute in Release (REFRESH-01/clamp).</summary>
    public const int MinIntervalSeconds = 300;

    /// <summary>The env var carrying the interval in integer seconds (REFRESH-01/config-source).</summary>
    public const string IntervalEnvName = "PLANMETER_INTERVAL_SECONDS";

    /// <summary>
    /// D-18 — the dev-only sub-floor bypass. Honored ONLY in a Debug build; a Release build
    /// ignores it and clamps to 300s. Never ships a sub-5-min cadence in Release.
    /// </summary>
    public const string UnsafeMinIntervalEnvName = "PLANMETER_UNSAFE_MIN_INTERVAL";

    /// <summary>
    /// The default config file path under <c>%LOCALAPPDATA%</c> (non-roamed — NOT OneDrive-backed
    /// <c>%APPDATA%</c>, per Pitfall 8), matching <c>DpapiKeyStore.DefaultBlobPath</c>'s directory.
    /// </summary>
    public static string DefaultConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlanMeter",
            "config.json");

    /// <summary>
    /// Resolve the startup interval with test-injectable seams for the env value, the config
    /// file path, and the Debug flag. Logs a startup line naming the source and the (possibly
    /// clamped) value so SC#3 is observable in the running app's log.
    /// </summary>
    /// <param name="envInterval">Override for the <c>PLANMETER_INTERVAL_SECONDS</c> env value (test seam; null reads the env).</param>
    /// <param name="configPath">Override for the config file path (test seam; null uses <see cref="DefaultConfigPath"/>).</param>
    /// <param name="debugBuild">True when running a Debug build — the only build that may honor <see cref="UnsafeMinIntervalEnvName"/>.</param>
    /// <param name="logger">The startup logger (null is allowed — the clamp line is then suppressed).</param>
    public static PlanMeterConfig Load(
        string? envInterval = null,
        string? configPath = null,
        bool debugBuild = false,
        ILogger? logger = null)
    {
        string effectivePath = configPath ?? DefaultConfigPath;

        int? resolved = null;
        string? source = null;

        // 1. Env var (highest precedence). An unparseable value falls through to the file.
        string? envValue = envInterval ?? Environment.GetEnvironmentVariable(IntervalEnvName);
        if (!string.IsNullOrWhiteSpace(envValue) && int.TryParse(envValue, out int envSeconds))
        {
            resolved = envSeconds;
            source = $"env:{IntervalEnvName}";
        }

        // 2. Config file field pollIntervalSeconds (Pattern E read-fresh; never throws).
        if (resolved is null)
        {
            int? fileValue = TryReadPollIntervalSeconds(effectivePath);
            if (fileValue is not null)
            {
                resolved = fileValue;
                source = $"file:{effectivePath}";
            }
        }

        // 3. Default.
        int value = resolved ?? DefaultIntervalSeconds;
        source ??= "default";

        // D-18 clamp — absolute in Release; Debug-only bypass via the unsafe env var.
        bool clamped = false;
        if (value < MinIntervalSeconds)
        {
            bool bypass = debugBuild
                && Environment.GetEnvironmentVariable(UnsafeMinIntervalEnvName) is not null;
            if (!bypass)
            {
                logger?.LogInformation(
                    "interval={Value} s (clamped to {Min} s)", value, MinIntervalSeconds);
                value = MinIntervalSeconds;
                clamped = true;
            }
        }

        logger?.LogInformation(
            "PlanMeter auto-poll interval: {Value} s (source {Source}{ClampSuffix})",
            value, source, clamped ? ", clamped" : string.Empty);

        return new PlanMeterConfig(value, clamped, source);
    }

    /// <summary>
    /// Pattern E (02-PATTERNS.md) — read-fresh <see cref="FileShare.Read"/> config read: open,
    /// read all, close immediately, then parse. A missing file, an unreadable file, or a
    /// malformed <c>pollIntervalSeconds</c> all degrade to "no value" (the caller falls back
    /// to the default) — this method NEVER throws (REFRESH-01/empty).
    /// </summary>
    private static int? TryReadPollIntervalSeconds(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }

            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("pollIntervalSeconds", out var prop)
                && prop.TryGetInt32(out int value)
                ? value
                : (int?)null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Pattern E: a missing/unparseable config file degrades to the 600s default,
            // never throws. (IOException is the mandated swallow; JsonException and
            // UnauthorizedAccessException are the same "can't read a trustworthy value"
            // family — T-02-34 accept disposition.)
            return null;
        }
    }
}
