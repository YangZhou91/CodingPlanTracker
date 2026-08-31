using System;
using FluentAssertions;
using PlanMeter.Core.Win32;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// D-13/D-14 — off-screen clamp-to-workspace unit tests. Exercises the pure
/// geometric clamp logic without P/Invoke.
/// </summary>
public sealed class OffScreenClampTests
{
    [Fact]
    public void Window_within_primary_work_area_is_not_clamped()
    {
        // 200x400 window at (1600, 400) on a 1920x1080 work area (0,0,1920,1080)
        var workAreas = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        var (left, top) = FullscreenGeometry.ComputeClamp(1600, 400, 200, 400, workAreas);
        left.Should().Be(1600);
        top.Should().Be(400);
    }

    [Fact]
    public void Window_entirely_off_screen_is_clamped_to_nearest_work_area()
    {
        // 200x400 window at (-400, 400) — entirely left of all monitors
        var workAreas = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        var (left, top) = FullscreenGeometry.ComputeClamp(-400, 400, 200, 400, workAreas);
        // Clamped to the left edge of the work area: 0
        left.Should().Be(0);
        top.Should().Be(400);
    }

    [Fact]
    public void Window_partially_off_screen_is_not_clamped()
    {
        // 200x400 window straddling the left edge: partially visible
        var workAreas = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        var (left, top) = FullscreenGeometry.ComputeClamp(-50, 400, 200, 400, workAreas);
        // Partially on-screen (150px visible) — NOT clamped
        left.Should().Be(-50);
        top.Should().Be(400);
    }

    [Fact]
    public void Empty_monitor_array_returns_original_position()
    {
        var workAreas = Array.Empty<FullscreenGeometry.RECT>();

        var (left, top) = FullscreenGeometry.ComputeClamp(100, 200, 300, 400, workAreas);
        left.Should().Be(100);
        top.Should().Be(200);
    }

    [Fact]
    public void Null_work_areas_returns_original_position()
    {
        var (left, top) = FullscreenGeometry.ComputeClamp(100, 200, 300, 400, null!);
        left.Should().Be(100);
        top.Should().Be(200);
    }

    [Fact]
    public void NaNSafe_clamp_returns_finite_values()
    {
        // D-14 / 260810-t13 NaN guard: extreme monitor-edge positions should
        // produce finite Left/Top values, never NaN or Infinity.
        var workAreas = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        // Window completely off-screen to the right
        var (left, top) = FullscreenGeometry.ComputeClamp(10000, 10000, 200, 400, workAreas);

        left.Should().NotBe(double.NaN).And.NotBe(double.PositiveInfinity).And.NotBe(double.NegativeInfinity);
        top.Should().NotBe(double.NaN).And.NotBe(double.PositiveInfinity).And.NotBe(double.NegativeInfinity);
        // Should be clamped into the work area
        left.Should().BeLessThanOrEqualTo(1920 - 200);
        top.Should().BeLessThanOrEqualTo(1080 - 400);
    }

    [Fact]
    public void Window_off_screen_right_is_clamped_to_right_edge()
    {
        // Window off-screen to the right
        var workAreas = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        var (left, top) = FullscreenGeometry.ComputeClamp(2000, 500, 200, 400, workAreas);
        // Clamped: Max(0, Min(2000, 1920-200)) = Max(0, Min(2000, 1720)) = 1720
        left.Should().Be(1720);
        top.Should().Be(500);
    }

    [Fact]
    public void Window_off_screen_bottom_is_clamped_to_bottom_edge()
    {
        // Window off-screen below
        var workAreas = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),
        };

        var (left, top) = FullscreenGeometry.ComputeClamp(800, 1200, 200, 400, workAreas);
        // Clamped: Max(0, Min(1200, 1080-400)) = Max(0, Min(1200, 680)) = 680
        left.Should().Be(800);
        top.Should().Be(680);
    }

    [Fact]
    public void Multi_monitor_window_visible_on_secondary_is_not_clamped()
    {
        // Window visible on a secondary monitor
        var workAreas = new[]
        {
            new FullscreenGeometry.RECT(0, 0, 1920, 1080),       // primary
            new FullscreenGeometry.RECT(1920, 0, 3840, 1080),    // secondary
        };

        var (left, top) = FullscreenGeometry.ComputeClamp(2000, 400, 200, 400, workAreas);
        left.Should().Be(2000);
        top.Should().Be(400);
    }
}
