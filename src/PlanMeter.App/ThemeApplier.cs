using System;
using System.Windows;

namespace PlanMeter.App;

/// <summary>
/// THM-03 — replaces the Light.xaml / Dark.xaml entry inside Themes/Generic.xaml's
/// MergedDictionaries. The theme-dictionary property element is MC3074-forbidden
/// (Phase 13); this is the supported runtime path. Idempotent: re-applying the
/// current theme is a no-op. Never touches App's outer Generic merge.
/// </summary>
public static class ThemeApplier
{
    private static readonly Uri LightUri =
        new("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);

    private static readonly Uri DarkUri =
        new("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);

    /// <summary>Resolve the pack URI for a theme token. Pure — unit-testable.</summary>
    public static Uri ResolveUri(bool dark) => dark ? DarkUri : LightUri;

    /// <summary>
    /// Swap Generic's inner Light/Dark token dictionary to the requested theme.
    /// Walks Application.Resources.MergedDictionaries for the outer Generic entry,
    /// then its MergedDictionaries for the inner Light.xaml/Dark.xaml entry.
    /// No-op when Application.Current is null or the theme is already applied.
    /// UI-thread only.
    /// </summary>
    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        Uri target = ResolveUri(dark);

        foreach (var outer in app.Resources.MergedDictionaries)
        {
            if (outer.Source is null)
            {
                continue;
            }

            if (!outer.Source.OriginalString.EndsWith(
                    "Generic.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (int i = 0; i < outer.MergedDictionaries.Count; i++)
            {
                var inner = outer.MergedDictionaries[i];
                if (inner.Source is null)
                {
                    continue;
                }

                string s = inner.Source.OriginalString;
                bool isToken =
                    s.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                    s.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase);
                if (!isToken)
                {
                    continue;
                }

                if (inner.Source == target)
                {
                    return; // already correct — idempotent
                }

                outer.MergedDictionaries[i] =
                    new ResourceDictionary { Source = target };
                return; // only one token dictionary is expected
            }
        }
    }
}
