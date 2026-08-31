using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlanMeter.Core.Models;

namespace PlanMeter.Core.Config;

/// <summary>
/// D-15 — the on-disk config shape the settings window reads AND writes:
/// <c>{ "pollIntervalSeconds": 600, "enabledProviders": ["zai"] }</c>. A NULL
/// <see cref="EnabledProviders"/> means "not configured / all enabled" (the old
/// <c>{ "pollIntervalSeconds": N }</c> shape the read tolerates); an EMPTY list means
/// "all disabled" (an explicit user choice). The credential NEVER appears in this record
/// or the serialized file (SEC-04) — keys live only in per-provider DPAPI blobs.
/// </summary>
/// <param name="PollIntervalSeconds">The polling interval in seconds (clamped by
/// <see cref="PlanMeterConfigLoader"/> at startup; the settings window writes the startup value).</param>
/// <param name="EnabledProviders">The enabled provider ids, or null when not configured
/// (all enabled).</param>
public sealed record ConfigData(
    [property: JsonPropertyName("pollIntervalSeconds")] int PollIntervalSeconds,
    [property: JsonPropertyName("enabledProviders")] IReadOnlyList<ProviderId>? EnabledProviders);

/// <summary>
/// D-15 — the read/write companion to the Phase-2 read-only
/// <see cref="PlanMeterConfigLoader"/>. Reads <c>{ pollIntervalSeconds, enabledProviders }</c>
/// from <c>%LOCALAPPDATA%\PlanMeter\config.json</c> with the Pattern E read-fresh
/// <see cref="FileShare.Read"/> open (degrade-don't-throw — missing/malformed → null),
/// and writes it ATOMICALLY: serialize to a temp file in the same directory, then
/// <see cref="File.Move(string,string,bool)"/> (same-volume rename) so the read-fresh
/// reader never observes a partial file (Pitfall 3). Keys NEVER live here (SEC-04).
///
/// NON-STATIC sealed class with a public parameterless ctor and INSTANCE members: a static
/// class cannot be DI-registered (<c>AddSingleton&lt;T&gt;</c> — CS0718) nor ctor-injected
/// (<c>ConfigStore configStore</c> — CS0721). Registered as
/// <c>services.AddSingleton&lt;ConfigStore&gt;()</c> and injected as an instance into the
/// settings window + App DI factories (matching the App's existing DI bootstrap — no
/// static + DI mix).
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new ProviderIdJsonConverter() },
    };

    /// <summary>
    /// Pattern E read-fresh <see cref="FileShare.Read"/> read: open, read all, close, then
    /// parse. A missing file → null; malformed/unreadable JSON → null (the caller treats
    /// null as "all enabled / default"); the old <c>{ pollIntervalSeconds }</c> shape reads
    /// with <c>EnabledProviders == null</c> (all enabled). NEVER throws.
    /// </summary>
    public ConfigData? TryRead(string path)
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

            return JsonSerializer.Deserialize<ConfigData>(bytes, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Pattern E / REFRESH-01/empty: a missing/unparseable config degrades to the
            // caller's default (all enabled / default interval), never throws.
            // WR-01: InvalidOperationException is included because the ProviderId
            // converter's reader.GetString() throws it (NOT JsonException) for a JSON-valid
            // but malformed non-string enabledProviders entry (e.g. [123] or [{}]) — without
            // this, a user-writable config file could crash the app at startup through the
            // never-throw contract.
            return null;
        }
    }

    /// <summary>
    /// Atomic write (Pitfall 3 / D-15): create the parent directory if missing, serialize
    /// to <c>path + ".tmp"</c> (same directory → same volume), then
    /// <see cref="File.Move(string,string,bool)"/> (atomic same-volume replace). A crash
    /// mid-write leaves only a stale <c>.tmp</c> — never a truncated <c>config.json</c> the
    /// read-fresh reader would degrade to defaults. Throws on failure (the settings-window
    /// toggle handler surfaces the W3 revert + error line — see 03-03-PLAN threat register).
    /// </summary>
    public void Write(string path, ConfigData data)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + ".tmp";
        File.WriteAllText(
            temp,
            JsonSerializer.Serialize(data, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The single source of the non-roamed config path
    /// (<c>%LOCALAPPDATA%\PlanMeter\config.json</c> — Pitfall 8), delegated to
    /// <see cref="PlanMeterConfigLoader.DefaultConfigPath"/>. Instance property on the
    /// injected singleton.
    /// </summary>
    public string DefaultConfigPath => PlanMeterConfigLoader.DefaultConfigPath;

    /// <summary>
    /// Serializes <see cref="ProviderId"/> as its plain string value (<c>"zai"</c>), NOT
    /// the default <c>{ "Value": "zai" }</c> object shape — so the JSON round-trips as
    /// <c>"enabledProviders": ["zai"]</c>.
    /// </summary>
    private sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
    {
        public override ProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}
