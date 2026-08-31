using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using PlanMeter.Core.Config;
using PlanMeter.Core.Models;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// D-15 — the read/write ConfigStore contract: <c>{ pollIntervalSeconds, enabledProviders }</c>
/// at <c>%LOCALAPPDATA%\PlanMeter\config.json</c> with an ATOMIC temp+move write and a
/// degrade-don't-throw read. Modeled on <see cref="ConfigReaderTests"/> (TempConfigFile +
/// Pattern E degrade fixture). The class is stateless — each test constructs its own
/// <c>new ConfigStore()</c> (public parameterless ctor, DI-registered singleton in the App).
///
/// SEC-04: the ConfigData shape carries NO credential field; the serialized file must never
/// contain a key/token-shaped property (the keys live only in per-provider DPAPI blobs).
/// </summary>
public sealed class ConfigStoreTests
{
    private static readonly ProviderId Zai = new("zai");
    private static readonly ProviderId Stub = new("stub");

    // (a) atomic write→read round-trip of both fields.
    [Fact]
    public void Write_then_Read_round_trips_poll_interval_and_enabled_providers()
    {
        using var dir = TempDir.Create();
        string path = Path.Combine(dir.Path, "config.json");
        var configStore = new ConfigStore();

        configStore.Write(path, new ConfigData(900, new[] { Zai }));

        ConfigData? read = configStore.TryRead(path);
        read.Should().NotBeNull();
        read!.PollIntervalSeconds.Should().Be(900);
        read.EnabledProviders.Should().BeEquivalentTo(new[] { Zai });
    }

    // (b) missing enabledProviders field (the old { pollIntervalSeconds } shape) → null
    //     → the caller treats null as "all enabled".
    [Fact]
    public void Missing_enabledProviders_field_reads_as_null_all_enabled()
    {
        using var dir = TempDir.Create();
        string path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, "{ \"pollIntervalSeconds\": 600 }");
        var configStore = new ConfigStore();

        ConfigData? read = configStore.TryRead(path);
        read.Should().NotBeNull();
        read!.PollIntervalSeconds.Should().Be(600);
        read.EnabledProviders.Should().BeNull();
    }

    // (c) malformed JSON / missing file → null, NEVER throws (Pattern E degrade).
    [Fact]
    public void Missing_or_malformed_file_returns_null_without_throwing()
    {
        using var dir = TempDir.Create();
        string malformed = Path.Combine(dir.Path, "malformed.json");
        File.WriteAllText(malformed, "{ this is not valid json ");
        var configStore = new ConfigStore();

        configStore.TryRead(malformed).Should().BeNull("malformed JSON must degrade to null");
        configStore.TryRead(Path.Combine(dir.Path, "missing.json")).Should().BeNull("a missing file must degrade to null");
    }

    // (d) WR-01 regression — a JSON-valid but malformed config with a NON-STRING
    //     enabledProviders entry must degrade to null, NEVER throw. The ProviderId
    //     converter's reader.GetString() throws InvalidOperationException (not
    //     JsonException) for a number or object token, so TryRead's catch filter must
    //     include it to honor the never-throw contract (a user-writable config file
    //     must not crash the app at startup).
    [Theory]
    [InlineData("{ \"pollIntervalSeconds\": 600, \"enabledProviders\": [123] }")]
    [InlineData("{ \"pollIntervalSeconds\": 600, \"enabledProviders\": [{}] }")]
    [InlineData("{ \"pollIntervalSeconds\": 600, \"enabledProviders\": [{\"Value\":\"zai\"}] }")]
    public void Non_string_enabledProviders_entry_returns_null_without_throwing(string json)
    {
        using var dir = TempDir.Create();
        string path = Path.Combine(dir.Path, "config.json");
        File.WriteAllText(path, json);
        var configStore = new ConfigStore();

        ConfigData? read = configStore.TryRead(path);
        read.Should().BeNull("a non-string enabledProviders entry must degrade to null, never throw (WR-01)");
    }

    // (e) the serialized file carries NO credential-bearing property (SEC-04).
    [Fact]
    public void Written_file_contains_no_credential_property()
    {
        using var dir = TempDir.Create();
        string path = Path.Combine(dir.Path, "config.json");
        var configStore = new ConfigStore();

        configStore.Write(path, new ConfigData(600, new[] { Zai }));

        string json = File.ReadAllText(path);
        // The round-trip shape: both fields, and the provider id as a plain string.
        json.Should().Contain("pollIntervalSeconds");
        json.Should().Contain("enabledProviders");
        json.Should().Contain("\"zai\"");

        // Negative assertion on the SERIALIZED TEXT (the ConfigData shape has no credential
        // field): no credential-named JSON property, and no SEC-03 token-shape catch-alls.
        json.Should().NotContain("apiKey", "the config file must never carry an API-key field");
        json.Should().NotContain("\"key\"", "no key-named property may serialize");
        json.Should().NotContain("\"token\"", "no token-named property may serialize");
        json.Should().NotContain("\"credential\"", "no credential-named property may serialize");
        json.Should().NotContain("\"secret\"", "no secret-named property may serialize");
        json.Should().NotContain("\"password\"", "no password-named property may serialize");
        json.Should().NotContain("eyJ", "no JWT-shaped value may serialize");
        json.Should().NotContain("sk-", "no API-key-shaped value may serialize");
    }

    // (f) Write creates the parent directory.
    [Fact]
    public void Write_creates_the_parent_directory()
    {
        using var dir = TempDir.Create();
        string path = Path.Combine(dir.Path, "nested", "subdir", "config.json");
        var configStore = new ConfigStore();

        configStore.Write(path, new ConfigData(600, null));

        File.Exists(path).Should().BeTrue();
        configStore.TryRead(path)!.PollIntervalSeconds.Should().Be(600);
    }

    // Null-vs-empty semantics (D-15): an EMPTY enabledProviders list is an EXPLICIT
    // "all disabled" choice (distinct from null = "not configured / all enabled").
    [Fact]
    public void Empty_enabledProviders_list_round_trips_as_an_explicit_all_disabled_choice()
    {
        using var dir = TempDir.Create();
        string path = Path.Combine(dir.Path, "config.json");
        var configStore = new ConfigStore();

        configStore.Write(path, new ConfigData(600, Array.Empty<ProviderId>()));

        ConfigData? read = configStore.TryRead(path);
        read.Should().NotBeNull();
        read!.EnabledProviders.Should().NotBeNull();
        read.EnabledProviders.Should().BeEmpty();
    }

    // Multi-provider ordering is preserved by the serializer (the enabled list order the
    // registry seeds from must survive the disk round-trip — D-05/ordering).
    [Fact]
    public void Write_then_Read_preserves_enabled_provider_order()
    {
        using var dir = TempDir.Create();
        string path = Path.Combine(dir.Path, "config.json");
        var configStore = new ConfigStore();

        configStore.Write(path, new ConfigData(600, new[] { Zai, Stub }));

        configStore.TryRead(path)!.EnabledProviders!.Select(p => p.Value).Should().Equal("zai", "stub");
    }

    /// <summary>A disposable temp directory (never the real %LOCALAPPDATA% path).</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        private TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public static TempDir Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "planmeter-configstore-" + Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
