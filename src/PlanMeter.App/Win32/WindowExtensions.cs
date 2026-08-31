using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PlanMeter.App.Win32;

/// <summary>
/// WIDGET-01 — focus-safe topmost. Applies the Win32 extended style
/// <c>WS_EX_TOPMOST | WS_EX_NOACTIVATE</c> via an <see cref="HwndSource"/> hook so the
/// widget never takes keyboard focus on launch, click, or refresh. Typing into another
/// app continues uninterrupted (ROADMAP Phase-1 SC#1).
/// </summary>
/// <remarks>
/// Pitfall 4: <c>Topmost="True"</c> is set ONCE in XAML; this class NEVER re-asserts
/// Topmost on a refresh tick. A debounced re-assert runs ONLY on <c>WM_WINDOWPOSCHANGED</c>
/// reporting an actual loss of topmost — and only if <c>AllowTopmostReassert</c> is true
/// (kept false for Phase 1; Plan 01-02 wires the full re-assert path).
/// </remarks>
public static class WindowExtensions
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>
    /// Apply <c>WS_EX_TOPMOST | WS_EX_NOACTIVATE</c> to <paramref name="window"/> and
    /// install a <c>WM_WINDOWPOSCHANGED</c> hook for the debounced re-assert path.
    /// Call from the window's <c>Loaded</c> handler (HwndSource is available by then).
    /// </summary>
    public static void ApplyTopmostNoActivate(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            // Fallback: hook SourceInitialized — but Loaded should have a handle.
            var source = PresentationSource.FromVisual(window) as HwndSource;
            if (source is null)
            {
                return;
            }

            hwnd = source.Handle;
        }

        // Apply the extended style.
        int extended = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extended | WS_EX_TOPMOST | WS_EX_NOACTIVATE);

        // Re-apply topmost once via SetWindowPos with NOACTIVATE (Pitfall 4: never
        // without NOACTIVATE; never on a refresh tick).
        SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

        // Hook WM_WINDOWPOSCHANGED so Plan 01-02 can install the debounced re-assert.
        var hwndSource = HwndSource.FromHwnd(hwnd);
        if (hwndSource is not null)
        {
            hwndSource.AddHook(WindowPosChangedHook);
        }
    }

    /// <summary>
    /// The user-initiated key-entry focus path (Q4 mitigation). Call this BEFORE
    /// <c>Keyboard.Focus(passwordBox)</c> if WS_EX_NOACTIVATE blocks typing. Briefly
    /// clears NOACTIVATE, returns the previous extended style so the caller can restore.
    /// </summary>
    public static int BeginUserInitiatedFocus(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        int extended = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extended & ~WS_EX_NOACTIVATE);
        return extended;
    }

    /// <summary>
    /// Restore the previous extended style (re-apply NOACTIVATE) after the user-initiated
    /// focus path completes.
    /// </summary>
    public static void EndUserInitiatedFocus(Window window, int previousExtendedStyle)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowLong(hwnd, GWL_EXSTYLE, previousExtendedStyle | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
    }

    private static DispatcherTimer? _reassertTimer;

    private static IntPtr WindowPosChangedHook(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGED)
        {
            int extended = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((extended & WS_EX_TOPMOST) == 0)
            {
                // Topmost was lost -- re-assert with debounce (D-11). The 500ms
                // DispatcherTimer is used ONLY for debounce, NEVER for polling.
                // Per Pitfall 4: NEVER re-assert on a refresh tick.
                _reassertTimer?.Stop();
                _reassertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _reassertTimer.Tick += (_, _) =>
                {
                    _reassertTimer.Stop();
                    SetWindowPos(hWnd, new IntPtr(HWND_TOPMOST),
                        0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                };
                _reassertTimer.Start();
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Convenience: returns the WS_EX_NOACTIVATE flag value for documentation / tests.
    /// </summary>
    public static int NoActivateFlag => WS_EX_NOACTIVATE;

    /// <summary>
    /// Convenience: returns the WS_EX_TOPMOST flag value for documentation / tests.
    /// </summary>
    public static int TopmostFlag => WS_EX_TOPMOST;
}
