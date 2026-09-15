using System;
using System.Windows;

namespace PlanMeter.App;

/// <summary>
/// THM-03 — swaps the App-level Light.xaml / Dark.xaml token dictionary.
/// Brushes are merged at Application.Resources (not nested inside Generic) so this
/// walk is a single-level replace. Accepts .xaml and .baml Source forms (WPF may
/// rewrite to the compiled pack URI). Idempotent via ThemeSource.Set's Current guard;
/// Apply itself always installs the requested dictionary when the token differs.
/// UI-thread only.
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
    /// Replace the App-level Light/Dark token dictionary with the requested theme.
    /// Also walks one nested Generic merge as a fallback for older layouts.
    /// No-op when Application.Current is null.
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

        // Primary: App.Resources.MergedDictionaries token entry.
        if (TrySwapInList(app.Resources, wantToken, target))
        {
            return;
        }

        // Fallback: nested inside a Generic merge (pre-15 layout).
        foreach (var outer in app.Resources.MergedDictionaries)
        {
            if (!SourceMatchesToken(outer.Source, "Generic"))
            {
                continue;
            }

            if (TrySwapInList(outer, wantToken, target))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Replace the first Light/Dark entry in <paramref name="owner"/>'s
    /// MergedDictionaries with <paramref name="target"/>. Returns false when no token
    /// dictionary is present. Always replaces when the installed token differs from
    /// <paramref name="wantToken"/>.
    /// </summary>
    private static bool TrySwapInList(
        ResourceDictionary owner,
        string wantToken,
        Uri target)
    {
        var list = owner.MergedDictionaries;
        for (int i = 0; i < list.Count; i++)
        {
            var dict = list[i];
            bool isLight = SourceMatchesToken(dict.Source, "Light");
            bool isDark = SourceMatchesToken(dict.Source, "Dark");
            if (!isLight && !isDark)
            {
                continue;
            }

            string currentToken = isDark ? "Dark" : "Light";
            if (string.Equals(currentToken, wantToken, StringComparison.Ordinal))
            {
                // Already the requested token — leave the loaded dictionary in place.
                return true;
            }

            list[i] = new ResourceDictionary { Source = target };
            return true;
        }

        return false;
    }
}
