using System;
using System.Windows;

namespace PlanMeter.App;

/// <summary>
/// THM-03 — replaces the Light.xaml / Dark.xaml entry inside Themes/Generic.xaml's
/// MergedDictionaries. The theme-dictionary property element is MC3074-forbidden
/// (Phase 13); this is the supported runtime path. Idempotent: re-applying the
/// current theme is a no-op. Never touches App's outer Generic merge.
///
/// Source matching accepts both .xaml and .baml — WPF may rewrite the loaded
/// ResourceDictionary.Source to the compiled .baml pack URI, which made a strict
/// EndsWith("Light.xaml") miss and silently no-op the swap (dark never applied).
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
    /// True when a dictionary Source URI points at the named theme token file
    /// (Generic / Light / Dark), whether the pack path ends in .xaml or .baml.
    /// </summary>
    internal static bool SourceMatchesToken(Uri? source, string token)
    {
        if (source is null)
        {
            return false;
        }

        string s = source.OriginalString;
        return s.EndsWith(token + ".xaml", StringComparison.OrdinalIgnoreCase)
               || s.EndsWith(token + ".baml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Swap Generic's inner Light/Dark token dictionary to the requested theme.
    /// Walks Application.Resources.MergedDictionaries for the outer Generic entry,
    /// then its MergedDictionaries for the inner Light/Dark entry.
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
        string wantToken = dark ? "Dark" : "Light";

        foreach (var outer in app.Resources.MergedDictionaries)
        {
            if (!SourceMatchesToken(outer.Source, "Generic"))
            {
                continue;
            }

            for (int i = 0; i < outer.MergedDictionaries.Count; i++)
            {
                var inner = outer.MergedDictionaries[i];
                bool isLight = SourceMatchesToken(inner.Source, "Light");
                bool isDark = SourceMatchesToken(inner.Source, "Dark");
                if (!isLight && !isDark)
                {
                    continue;
                }

                // Already the requested token — idempotent (compare by name, not Uri ==).
                string currentToken = isDark ? "Dark" : "Light";
                if (string.Equals(currentToken, wantToken, StringComparison.Ordinal))
                {
                    return;
                }

                outer.MergedDictionaries[i] =
                    new ResourceDictionary { Source = target };
                return; // only one token dictionary is expected
            }
        }
    }
}
