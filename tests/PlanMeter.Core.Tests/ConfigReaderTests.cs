using System;
using System.IO;
using FluentAssertions;
using PlanMeter.Core.Config;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// REFRESH-01 (D-18/D-19) — the startup interval config: precedence
/// env &gt; config file &gt; 600s default, the absolute 5-min clamp (SC#3) with its boundary,
/// and the Debug-only sub-floor bypass. Each test injects the seams (env string, config
/// path, debug flag) so nothing depends on the machine's real env or a real config file.
/// </summary>
public sealed class ConfigReaderTests
{
    // --- DEFAULT (REFRESH-01/default): no env, no file -> 600s, unclamped. ---

    [Fact]
    public void Load_defaults_to_600_seconds_when_no_env_or_file()
    {
        var config = PlanMeterConfigLoader.Load(envInterval: "", configPath: MissingPath(), debugBuild: false);

        config.IntervalSeconds.Should().Be(600);
        config.Clamped.Should().BeFalse();
        config.Source.Should().Be("default");
    }

    // --- CLAMP (REFRESH-01/clamp + boundary): the 5-min floor is absolute in Release. ---

    [Theory]
    [InlineData("120", 300, true)]  // far below the floor -> clamped
    [InlineData("299", 300, true)]  // one second below the floor -> clamped
    [InlineData("300", 300, false)] // exactly the floor -> passes unclamped (boundary, >= not >)
    [InlineData("500", 500, false)] // above the floor -> passes through untouched
    [InlineData("600", 600, false)] // the default itself -> unclamped
    public void Load_clamps_below_the_5_minute_floor_in_release(
        string envValue, int expectedSeconds, bool expectedClamped)
    {
        var config = PlanMeterConfigLoader.Load(envInterval: envValue, configPath: MissingPath(), debugBuild: false);

        config.IntervalSeconds.Should().Be(expectedSeconds);
        config.Clamped.Should().Be(expectedClamped);
    }

    // --- BYPASS (REFRESH-01/dev-bypass): PLANMETER_UNSAFE_MIN_INTERVAL works ONLY in Debug. ---

    [Fact]
    public void Unsafe_min_interval_bypass_is_honored_only_in_a_debug_build()
    {
        Environment.SetEnvironmentVariable(PlanMeterConfigLoader.UnsafeMinIntervalEnvName, "1");
        try
        {
            // Debug build + bypass set -> the sub-floor value is honored, unclamped.
            var debug = PlanMeterConfigLoader.Load(envInterval: "120", configPath: MissingPath(), debugBuild: true);
            debug.IntervalSeconds.Should().Be(120);
            debug.Clamped.Should().BeFalse();

            // Release build + bypass set -> the bypass is IGNORED, clamped to 300s.
            var release = PlanMeterConfigLoader.Load(envInterval: "120", configPath: MissingPath(), debugBuild: false);
            release.IntervalSeconds.Should().Be(300);
            release.Clamped.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlanMeterConfigLoader.UnsafeMinIntervalEnvName, null);
        }
    }

    // --- FILE SOURCE (REFRESH-01/config-source + Pattern E): %LOCALAPPDATA%\PlanMeter\config.json. ---

    [Fact]
    public void File_source_pollIntervalSeconds_is_read_and_clamped_like_any_other_source()
    {
        using var file = TempConfigFile.Create("{ \"pollIntervalSeconds\": 240 }");

        var config = PlanMeterConfigLoader.Load(envInterval: "", configPath: file.Path, debugBuild: false);

        config.IntervalSeconds.Should().Be(300, "a sub-floor file value is clamped to the 5-min floor");
        config.Clamped.Should().BeTrue();
        config.Source.Should().StartWith("file:");
    }

    [Fact]
    public void File_source_above_floor_passes_through()
    {
        using var file = TempConfigFile.Create("{ \"pollIntervalSeconds\": 450 }");

        var config = PlanMeterConfigLoader.Load(envInterval: "", configPath: file.Path, debugBuild: false);

        config.IntervalSeconds.Should().Be(450);
        config.Clamped.Should().BeFalse();
    }

    // --- EMPTY (REFRESH-01/empty): a missing OR malformed file degrades to the default, never throws. ---

    [Fact]
    public void Missing_or_malformed_file_degrades_to_600_seconds_without_throwing()
    {
        using var malformed = TempConfigFile.Create("{ this is not valid json ");

        var missing = PlanMeterConfigLoader.Load(envInterval: "", configPath: MissingPath(), debugBuild: false);
        missing.IntervalSeconds.Should().Be(600);
        missing.Clamped.Should().BeFalse();

        var bad = PlanMeterConfigLoader.Load(envInterval: "", configPath: malformed.Path, debugBuild: false);
        bad.IntervalSeconds.Should().Be(600);
        bad.Clamped.Should().BeFalse();
    }

    // --- PRECEDENCE (REFRESH-01/config-source): env wins over the file. ---

    [Fact]
    public void Env_value_takes_precedence_over_the_config_file()
    {
        using var file = TempConfigFile.Create("{ \"pollIntervalSeconds\": 240 }");

        var config = PlanMeterConfigLoader.Load(envInterval: "500", configPath: file.Path, debugBuild: false);

        config.IntervalSeconds.Should().Be(500, "env beats the file — the file's 240 is never read");
        config.Clamped.Should().BeFalse();
        config.Source.Should().StartWith("env:");
    }

    // --- WRITE PATH (D-15, CONF-03): a ConfigStore.Write round-trips through the loader. ---

    [Fact]
    public void ConfigStore_write_round_trips_through_the_loader()
    {
        // D-15 — the settings-window Polling card (03-04) persists the interval via the
        // atomic ConfigStore.Write; the same file must read back with the unchanged loader
        // precedence (env > file > default) and clamp. 900 = 15 min, a legal preset.
        string tempDir = Path.Combine(Path.GetTempPath(), "planmeter-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "config.json");
        try
        {
            new ConfigStore().Write(path, new ConfigData(900, null));

            var config = PlanMeterConfigLoader.Load(envInterval: "", configPath: path, debugBuild: false);
            config.IntervalSeconds.Should().Be(900, "the loader reads the file's pollIntervalSeconds after a ConfigStore write");
            config.Clamped.Should().BeFalse();
            config.Source.Should().StartWith("file:");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void ConfigStore_write_of_the_minimum_preset_round_trips_unclamped()
    {
        // D-12 boundary — 300 (5 min) is a legal preset and must pass the clamp unclamped
        // even when the settings window wrote it (the preset UI never presents sub-5-min).
        string tempDir = Path.Combine(Path.GetTempPath(), "planmeter-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string path = Path.Combine(tempDir, "config.json");
        try
        {
            new ConfigStore().Write(path, new ConfigData(300, null));

            var config = PlanMeterConfigLoader.Load(envInterval: "", configPath: path, debugBuild: false);
            config.IntervalSeconds.Should().Be(300);
            config.Clamped.Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A temp-directory path that is guaranteed not to exist (the "no file" case).</summary>
    private static string MissingPath() =>
        Path.Combine(Path.GetTempPath(), "planmeter-config-" + Guid.NewGuid().ToString("N"), "config.json");

    /// <summary>A disposable temp config.json written to a fresh temp dir (never the real path).</summary>
    private sealed class TempConfigFile : IDisposable
    {
        public string Path { get; }
        private readonly string _dir;

        private TempConfigFile(string path, string dir)
        {
            Path = path;
            _dir = dir;
        }

        public static TempConfigFile Create(string contents)
        {
            string dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "planmeter-config-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "config.json");
            File.WriteAllText(path, contents);
            return new TempConfigFile(path, dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
