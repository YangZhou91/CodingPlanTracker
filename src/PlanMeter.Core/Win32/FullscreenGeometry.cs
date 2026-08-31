using System;
using System.Runtime.InteropServices;

namespace PlanMeter.Core.Win32;

/// <summary>
/// Pure geometric predicate logic for fullscreen detection and off-screen clamping.
/// No P/Invoke calls — fully testable from any TFM.
/// </summary>
public static class FullscreenGeometry
{
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

    /// <summary>
    /// Pure predicate: given a foreground window rect, its style bits, and all monitor
    /// full rects, returns true if the foreground window is borderless-fullscreen on
    /// ANY monitor. Borderless-fullscreen = WS_POPUP set AND WS_CAPTION clear AND rect
    /// exactly matches at least one monitor's full rect. Must NOT false-positive on
    /// maximized windows which have WS_CAPTION set (Pitfall 2).
    /// </summary>
    public static bool IsFullscreenByRectAndStyle(RECT fgRect, int style, RECT[] monitorRects)
    {
        if (monitorRects is null || monitorRects.Length == 0)
        {
            return false;
        }

        // Borderless-fullscreen predicate: WS_POPUP set, no caption
        bool isBorderless = (style & 0x80000000) != 0 && (style & 0x00C00000) == 0;
        if (!isBorderless)
        {
            return false;
        }

        // Any-monitor rule (D-09): check ALL monitors
        foreach (var monitor in monitorRects)
        {
            if (fgRect.Left == monitor.Left &&
                fgRect.Top == monitor.Top &&
                fgRect.Right == monitor.Right &&
                fgRect.Bottom == monitor.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pure clamp logic: given a window position/size and all monitor work areas,
    /// returns the clamped (Left, Top) snapped to the nearest visible work area.
    /// Returns the original position when on-screen.
    /// </summary>
    public static (double Left, double Top) ComputeClamp(
        double left, double top, double width, double height, RECT[] workAreas)
    {
        if (workAreas is null || workAreas.Length == 0)
        {
            // No monitors — return original (no crash, D-13 safety)
            return (left, top);
        }

        // Check if the window intersects ANY work area
        foreach (var wa in workAreas)
        {
            if (RectsIntersect(left, top, width, height, wa))
            {
                return (left, top);
            }
        }

        // Off-screen: snap to the first work area (primary in practice)
        RECT target = workAreas[0];

        // Clamp: ensure the window fits within the target work area
        double clampedLeft = Math.Max(target.Left, Math.Min(left, target.Right - width));
        double clampedTop = Math.Max(target.Top, Math.Min(top, target.Bottom - height));

        // NaN guard (D-14 / 260810-t13): ensure finite values
        if (double.IsNaN(clampedLeft) || double.IsInfinity(clampedLeft))
        {
            clampedLeft = target.Left + 16;
        }
        if (double.IsNaN(clampedTop) || double.IsInfinity(clampedTop))
        {
            clampedTop = target.Bottom - height - 16;
        }

        return (clampedLeft, clampedTop);
    }

    /// <summary>
    /// Checks if a window rect intersects a work area rect.
    /// </summary>
    public static bool RectsIntersect(double left, double top, double width, double height, RECT wa)
    {
        double right = left + width;
        double bottom = top + height;

        return left < wa.Right &&
               right > wa.Left &&
               top < wa.Bottom &&
               bottom > wa.Top;
    }
}
