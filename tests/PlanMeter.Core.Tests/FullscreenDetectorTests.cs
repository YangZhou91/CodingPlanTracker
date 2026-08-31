using System;
using FluentAssertions;
using PlanMeter.Core.Win32;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// WIDGET-05b — fullscreen detector unit tests. Exercises the pure geometric
/// predicate logic (rect comparison, style-bit checking) without P/Invoke.
/// </summary>
public sealed class FullscreenDetectorTests
{
    // Style constants (duplicated from FullscreenGeometry to avoid coupling)
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;

    [Fact]
    public void Borderless_window_covering_monitor_rect_returns_true()
    {
        // Window exactly covers a 1920x1080 monitor, borderless style
        var fgRect = new FullscreenGeometry.RECT(0, 0, 1920, 1080);
        int style = WS_POPUP; // borderless, no caption
        var monitors = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        FullscreenGeometry.IsFullscreenByRectAndStyle(fgRect, style, monitors)
            .Should().BeTrue();
    }

    [Fact]
    public void Maximized_normal_window_does_not_false_positive()
    {
        // Maximized window covers the monitor BUT has WS_CAPTION set (Pitfall 2 guard)
        var fgRect = new FullscreenGeometry.RECT(0, 0, 1920, 1080);
        int style = WS_POPUP | WS_CAPTION; // borderless WITH caption = maximized
        var monitors = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        FullscreenGeometry.IsFullscreenByRectAndStyle(fgRect, style, monitors)
            .Should().BeFalse("maximized windows have WS_CAPTION set — must not false-positive");
    }

    [Fact]
    public void Window_not_covering_any_monitor_rect_returns_false()
    {
        // Borderless window that doesn't match any monitor rect
        var fgRect = new FullscreenGeometry.RECT(100, 100, 1200, 800);
        int style = WS_POPUP;
        var monitors = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        FullscreenGeometry.IsFullscreenByRectAndStyle(fgRect, style, monitors)
            .Should().BeFalse();
    }

    [Fact]
    public void Any_monitor_rule_fullscreen_on_secondary_monitor()
    {
        // Borderless window covering secondary monitor (non-primary)
        var fgRect = new FullscreenGeometry.RECT(1920, 0, 3840, 1080);
        int style = WS_POPUP;
        var monitors = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),      // primary
            new FullscreenGeometry.RECT(1920, 0, 3840, 1080),  // secondary
        };

        FullscreenGeometry.IsFullscreenByRectAndStyle(fgRect, style, monitors)
            .Should().BeTrue("any-monitor rule: fullscreen on secondary monitor");
    }

    [Fact]
    public void Zero_HWND_returns_false()
    {
        // Zero foreground HWND = no foreground window = not fullscreen
        // In the pure predicate, this is tested by the caller. Here we verify
        // empty monitors array returns false.
        var fgRect = new FullscreenGeometry.RECT(0, 0, 0, 0);
        int style = WS_POPUP;
        var monitors = Array.Empty<FullscreenGeometry.RECT>();

        FullscreenGeometry.IsFullscreenByRectAndStyle(fgRect, style, monitors)
            .Should().BeFalse();
    }

    [Fact]
    public void Multi_monitor_borderless_on_monitor2_while_widget_on_monitor1()
    {
        // Borderless fullscreen on monitor 2 — should still trigger fullscreen
        // even though the widget might be on monitor 1 (any-monitor rule)
        var fgRect = new FullscreenGeometry.RECT(-1920, 0, 0, 1200); // monitor 2 (left of primary)
        int style = WS_POPUP;
        var monitors = new[]
        {
            new FullscreenGeometry.RECT(-1920, 0, 0, 1200),   // monitor 2 (left)
            new FullscreenGeometry.RECT(0, 0, 2560, 1440),     // primary
        };

        FullscreenGeometry.IsFullscreenByRectAndStyle(fgRect, style, monitors)
            .Should().BeTrue();
    }

    [Fact]
    public void Null_monitor_array_returns_false()
    {
        var fgRect = new FullscreenGeometry.RECT(0, 0, 1920, 1080);
        int style = WS_POPUP;

        FullscreenGeometry.IsFullscreenByRectAndStyle(fgRect, style, null!)
            .Should().BeFalse();
    }
}
