using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using PlanMeter.Core.Boot;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// BOOT-01 — temp-folder real-COM round-trip for <see cref="BootShortcutManager"/>.
/// Never touches the real Start Menu Startup folder. Production ctor is only
/// used for the read-only path assertion.
/// </summary>
public sealed class BootShortcutManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _shortcutPath;

    public BootShortcutManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"planmeter-boot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _shortcutPath = Path.Combine(_tempDir, BootShortcutManager.ShortcutFileName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private BootShortcutManager CreateManager() => new(_shortcutPath);

    [Fact]
    public void Enable_creates_lnk_file()
    {
        var manager = CreateManager();

        manager.IsEnabled.Should().BeFalse();

        manager.Enable();

        File.Exists(_shortcutPath).Should().BeTrue();
        manager.IsEnabled.Should().BeTrue();
        new FileInfo(_shortcutPath).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Enable_target_round_trips_current_exe_path()
    {
        var manager = CreateManager();
        manager.Enable();

        string? target = ReadShortcutTarget(_shortcutPath);
        target.Should().NotBeNullOrWhiteSpace();
        string? expected = Environment.ProcessPath;
        expected.Should().NotBeNullOrWhiteSpace("Environment.ProcessPath is the single-file-safe EXE path (Pitfall 1)");
        string.Equals(target, expected, StringComparison.OrdinalIgnoreCase).Should().BeTrue(
            $"TargetPath '{target}' should equal ProcessPath '{expected}'");
    }

    [Fact]
    public void Enable_is_idempotent_when_file_exists()
    {
        var manager = CreateManager();
        manager.Enable();
        manager.Enable();

        Directory.GetFiles(_tempDir).Should().HaveCount(1);
        manager.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Disable_removes_lnk_file()
    {
        var manager = CreateManager();
        manager.Enable();

        manager.Disable();

        File.Exists(_shortcutPath).Should().BeFalse();
        manager.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Disable_is_noop_when_file_missing()
    {
        var manager = CreateManager();

        Action act = () => manager.Disable();
        act.Should().NotThrow();
        manager.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ShortcutPath_reports_injected_path()
    {
        var manager = CreateManager();
        manager.ShortcutPath.Should().Be(_shortcutPath);
    }

    [Fact]
    public void Production_ctor_targets_real_startup_folder()
    {
        // Read-only: never Enable/Disable against the real Startup folder.
        var manager = new BootShortcutManager();
        manager.ShortcutPath.Should().EndWith(BootShortcutManager.ShortcutFileName);
        Path.GetDirectoryName(manager.ShortcutPath).Should().EndWith("Startup");
    }

    private static string? ReadShortcutTarget(string shortcutPath)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not registered.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            return (string?)shortcut.TargetPath;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }
}
