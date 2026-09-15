using System;

namespace PlanMeter.App;

/// <summary>
/// THM-03 — the live theme authority, mirroring <c>PollIntervalSource</c> (one shared
/// source every ConfigData write reads). Current is the normalized lowercase token
/// ("light" | "dark"); null/missing/unknown degrade to light at Seed/Set time.
///
/// Seed is startup-only (no event — the window is not built yet). Set normalizes,
/// no-ops when unchanged, calls ThemeApplier.Apply, and raises ThemeChanged so
/// MainWindow can re-render FindResource paint sites after the dictionary swap.
///
/// Startup uses Seed + a separate ThemeApplier.Apply (Seed never fires the event).
/// </summary>
public sealed class ThemeSource
{
    /// <summary>The current normalized theme token ("light" or "dark").</summary>
    public string Current { get; private set; } = "light";

    /// <summary>True when the current token is "dark" (case-insensitive).</summary>
    public bool IsDark => string.Equals(Current, "dark", StringComparison.OrdinalIgnoreCase);

    /// <summary>Raised after <see cref="Set"/> actually changes the theme. UI-thread only.</summary>
    public event Action? ThemeChanged;

    /// <summary>
    /// Startup-only seed. Normalizes null/unknown → light; sets Current; NO event
    /// (the window is not built yet). Call ThemeApplier.Apply separately after Seed.
    /// </summary>
    public void Seed(string? theme)
    {
        Current = Normalize(theme);
    }

    /// <summary>
    /// Live-apply a user-selected theme. Normalizes; no-ops when unchanged; sets
    /// Current; calls <see cref="ThemeApplier.Apply"/>; raises <see cref="ThemeChanged"/>.
    /// Startup uses Seed + a separate ThemeApplier.Apply (Seed never fires the event).
    /// </summary>
    public void Set(string theme)
    {
        string normalized = Normalize(theme);
        if (string.Equals(Current, normalized, StringComparison.Ordinal))
        {
            return;
        }

        Current = normalized;
        ThemeApplier.Apply(IsDark);
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Pure normalize helper: "dark" only when EqualsIgnoreCase "dark", else "light".
    /// Null, empty, "blue", "LIGHT" all → "light" (T-15-05 — never crash on unknown).
    /// </summary>
    public static string Normalize(string? theme) =>
        string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
}
