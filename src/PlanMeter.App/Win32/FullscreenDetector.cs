using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace PlanMeter.App.Win32;

/// <summary>
/// WIDGET-05b — fullscreen detection via pure Win32 P/Invoke. Checks whether the
/// current foreground window covers an entire monitor with a borderless style
/// (WS_POPUP set, WS_CAPTION clear). Uses NO System.Windows.Forms reference.
/// Also provides off-screen detection and clamp-to-workspace logic for DPI/monitor changes.
/// </summary>
public static class FullscreenDetector
{
    // --- P/Invoke declarations (user32.dll only, SetLastError=true per pattern) ---

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // --- Constants ---

    private const int GWL_STYLE = -16;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;

    private const int MONITORINFOF_PRIMARY = 0x00000001;
    private const int CCHDEVICENAME = 32;

    // --- Structs ---

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;  // Full monitor rect
        public RECT rcWork;     // Work area (excludes taskbar)
        public uint dwFlags;
    }

    // --- Pure predicate methods (testable without P/Invoke) ---

    /// <summary>
    /// Pure predicate: given a foreground window rect, its style bits, and all monitor
    /// full rects, returns true if the foreground window is borderless-fullscreen on
    /// ANY monitor. Delegates to <see cref="PlanMeter.Core.Win32.FullscreenGeometry"/>
    /// for testability from the net8.0 test project.
    /// </summary>
    public static bool IsFullscreenByRectAndStyle(RECT fgRect, int style, RECT[] monitorRects)
    {
        var geoRects = ConvertRects(monitorRects);
        return Core.Win32.FullscreenGeometry.IsFullscreenByRectAndStyle(
            new Core.Win32.FullscreenGeometry.RECT(fgRect.Left, fgRect.Top, fgRect.Right, fgRect.Bottom),
            style, geoRects);
    }

    public static System.Windows.Rect ComputeClamp(
        double left, double top, double width, double height, RECT[] workAreas)
    {
        var geoWorkAreas = ConvertRects(workAreas);
        var (clampedLeft, clampedTop) = Core.Win32.FullscreenGeometry.ComputeClamp(
            left, top, width, height, geoWorkAreas);
        return new System.Windows.Rect(clampedLeft, clampedTop, width, height);
    }

    /// <summary>
    /// Checks if a window rect intersects a work area rect.
    /// Delegates to <see cref="PlanMeter.Core.Win32.FullscreenGeometry"/>.
    /// </summary>
    internal static bool RectsIntersect(double left, double top, double width, double height, RECT wa)
    {
        return Core.Win32.FullscreenGeometry.RectsIntersect(left, top, width, height,
            new Core.Win32.FullscreenGeometry.RECT(wa.Left, wa.Top, wa.Right, wa.Bottom));
    }

    /// <summary>
    /// Converts App.RECT[] to Core.Win32.FullscreenGeometry.RECT[].
    /// </summary>
    private static Core.Win32.FullscreenGeometry.RECT[] ConvertRects(RECT[] rects)
    {
        var result = new Core.Win32.FullscreenGeometry.RECT[rects.Length];
        for (int i = 0; i < rects.Length; i++)
        {
            result[i] = new Core.Win32.FullscreenGeometry.RECT(
                rects[i].Left, rects[i].Top, rects[i].Right, rects[i].Bottom);
        }
        return result;
    }

    // --- P/Invoke-based public methods (called by the watcher/clamp at runtime) ---

    /// <summary>
    /// Returns true if the current foreground window covers an entire monitor
    /// and has a borderless style (WS_POPUP without caption). Checks ALL monitors.
    /// </summary>
    public static bool IsForegroundFullscreen()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out RECT fgRect))
        {
            return false;
        }

        int style = GetWindowLong(hwnd, GWL_STYLE);

        var monitorRects = GetAllMonitorRects();
        return IsFullscreenByRectAndStyle(fgRect, style, monitorRects);
    }

    /// <summary>
    /// Returns true when the given window's rect does not intersect ANY monitor's
    /// work area (used by clamp logic).
    /// </summary>
    public static bool IsWindowOffScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out RECT windowRect))
        {
            return false;
        }

        var workAreas = GetAllWorkAreas();
        double width = windowRect.Width;
        double height = windowRect.Height;

        foreach (var wa in workAreas)
        {
            if (RectsIntersect(windowRect.Left, windowRect.Top, width, height, wa))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns a clamped position when off-screen, snapped to the nearest work area.
    /// Returns the original position when on-screen.
    /// </summary>
    public static System.Windows.Rect ClampToNearestWorkspace(double left, double top, double width, double height)
    {
        var workAreas = GetAllWorkAreas();
        return ComputeClamp(left, top, width, height, workAreas);
    }

    // --- Private helpers ---

    /// <summary>
    /// Enumerates all monitors and returns their full rects (for fullscreen detection).
    /// </summary>
    private static RECT[] GetAllMonitorRects()
    {
        var rects = new List<RECT>();
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                rects.Add(mi.rcMonitor);
            }
            return true; // continue enumeration
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return rects.ToArray();
    }

    /// <summary>
    /// Enumerates all monitors and returns their work area rects (for off-screen clamp).
    /// Primary monitor is first in the array.
    /// </summary>
    private static RECT[] GetAllWorkAreas()
    {
        var workAreas = new List<RECT>();
        var primaryWorkArea = default(RECT);
        bool foundPrimary = false;

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                if (!foundPrimary && (mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
                {
                    // Primary first for clamp target selection
                    primaryWorkArea = mi.rcWork;
                    foundPrimary = true;
                }
                workAreas.Add(mi.rcWork);
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        if (foundPrimary)
        {
            // Insert primary at front for clamp target
            workAreas.Insert(0, primaryWorkArea);
        }

        return workAreas.ToArray();
    }
}
