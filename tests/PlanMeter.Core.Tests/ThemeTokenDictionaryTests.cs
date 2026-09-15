using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// THM-01/02 parity pins for Light.xaml / Dark.xaml / Generic ThemeDictionaries.
/// Scrapes the XAML as XML — never loads a WPF ResourceDictionary.
/// </summary>
public sealed class ThemeTokenDictionaryTests
{
    private static readonly string XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "PlanMeter.App", "Themes", "Light.xaml")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate src/PlanMeter.App/Themes/Light.xaml walking up from {AppContext.BaseDirectory}");
    }

    private static string ThemesPath(string fileName) =>
        Path.Combine(RepoRoot(), "src", "PlanMeter.App", "Themes", fileName);

    private static Dictionary<string, (string Color, string? Opacity)> BrushKeys(string fileName)
    {
        var doc = XDocument.Load(ThemesPath(fileName));
        var result = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var el in doc.Descendants())
        {
            if (el.Name.LocalName != "SolidColorBrush")
            {
                continue;
            }

            var key = (string?)el.Attribute(XName.Get("Key", XamlNs));
            if (key is null || !key.StartsWith("Brush.", StringComparison.Ordinal))
            {
                continue;
            }

            result[key] = (
                (string?)el.Attribute("Color") ?? string.Empty,
                (string?)el.Attribute("Opacity"));
        }

        return result;
    }

    [Fact]
    public void Light_and_Dark_share_the_same_11_Brush_keys()
    {
        var light = BrushKeys("Light.xaml");
        var dark = BrushKeys("Dark.xaml");

        light.Keys.Should().BeEquivalentTo(dark.Keys);
        light.Should().HaveCount(11, "design B ships exactly 10 existing Brush.* keys + Brush.QuotaTrack");
    }

    [Fact]
    public void QuotaTrack_is_present_in_both()
    {
        BrushKeys("Light.xaml").Should().ContainKey("Brush.QuotaTrack");
        BrushKeys("Dark.xaml").Should().ContainKey("Brush.QuotaTrack");
    }

    [Fact]
    public void Light_hex_values_match_design_B()
    {
        var light = BrushKeys("Light.xaml");

        light["Brush.Surface"].Color.Should().Be("#F8FAF9");
        light["Brush.Surface.Elevated"].Color.Should().Be("#EEF2EF");
        light["Brush.Accent"].Color.Should().Be("#23764B");
        light["Brush.NearLimit"].Color.Should().Be("#99630A");
        light["Brush.Error"].Color.Should().Be("#B3261E");
        light["Brush.OnSurface"].Color.Should().Be("#25332B");
        light["Brush.OnSurface.Dimmed"].Color.Should().Be("#647268");
        light["Brush.Dimmed"].Color.Should().Be("#87948B");
        light["Brush.Divider"].Color.Should().Be("#DBE4DE");
        light["Brush.QuotaTrack"].Color.Should().Be("#E1E9E4");
        light["Brush.FocusRing"].Color.Should().Be("#23764B");
        light["Brush.FocusRing"].Opacity.Should().Be("0.6");
    }

    [Fact]
    public void Dark_hex_values_match_design_B()
    {
        var dark = BrushKeys("Dark.xaml");

        dark["Brush.Surface"].Color.Should().Be("#222724");
        dark["Brush.Surface.Elevated"].Color.Should().Be("#303A33");
        dark["Brush.Accent"].Color.Should().Be("#88D3A4");
        dark["Brush.NearLimit"].Color.Should().Be("#F3BD65");
        dark["Brush.Error"].Color.Should().Be("#FFB4AB");
        dark["Brush.OnSurface"].Color.Should().Be("#EDF4EF");
        dark["Brush.OnSurface.Dimmed"].Color.Should().Be("#B2C1B6");
        dark["Brush.Dimmed"].Color.Should().Be("#A7B4AB");
        dark["Brush.Divider"].Color.Should().Be("#414C45");
        dark["Brush.QuotaTrack"].Color.Should().Be("#3C4840");
        dark["Brush.FocusRing"].Color.Should().Be("#88D3A4");
        dark["Brush.FocusRing"].Opacity.Should().Be("0.6");
    }

    [Fact]
    public void Generic_merges_Light_without_key_and_has_no_inline_Brush_defs()
    {
        string generic = File.ReadAllText(ThemesPath("Generic.xaml"));

        // Phase 13 wiring: Light.xaml is merged WITHOUT an x:Key so its Brush.* keys
        // paint as normal application resources. ThemeDictionaries was the research
        // Option A target, but ResourceDictionary.ThemeDictionaries is
        // DesignerSerializationVisibility.Hidden and MC3074-rejects the property
        // element in a Page-compiled app dictionary (see 13-01-SUMMARY deviation).
        // Dark.xaml is still a compiled Page; Phase 15 swaps the merged entry.
        generic.Should().Contain("Source=\"Light.xaml\"");
        generic.Should().Contain("MergedDictionaries");

        var doc = XDocument.Load(ThemesPath("Generic.xaml"));
        var inlineBrushes = doc.Descendants()
            .Where(el => el.Name.LocalName == "SolidColorBrush")
            .Select(el => (string?)el.Attribute(XName.Get("Key", XamlNs)))
            .Where(k => k is not null && k.StartsWith("Brush.", StringComparison.Ordinal))
            .ToList();

        inlineBrushes.Should().BeEmpty(
            "color brushes live in Light.xaml / Dark.xaml; Generic holds styles only");
    }

    [Fact]
    public void Generic_still_defines_size_tokens_and_TextMutedStyle()
    {
        string generic = File.ReadAllText(ThemesPath("Generic.xaml"));

        generic.Should().Contain("Text.Name.Size");
        generic.Should().Contain("Text.Percent.Size");
        generic.Should().Contain("TextMutedStyle");
        // TextLabelStyle must keep SemiBold for settings labels (decision 5).
        generic.Should().Contain("TextLabelStyle");
    }

    // T-15-06 — ThemeDictionaries is MC3074-forbidden (Phase 13 deviation). This pin
    // guards against someone "simplifying" back to it. Checks the XAML element tree,
    // not raw text, so a comment mentioning the concept is allowed.
    [Fact]
    public void Generic_has_no_ThemeDictionaries()
    {
        var doc = XDocument.Load(ThemesPath("Generic.xaml"));
        var themeDictElements = doc.Descendants()
            .Where(el => el.Name.LocalName == "ThemeDictionaries")
            .ToList();

        themeDictElements.Should().BeEmpty(
            "ResourceDictionary.ThemeDictionaries is MC3074-forbidden in a Page-compiled app dictionary");
    }

    // T-15-06 / MC3074 — Generic must merge EXACTLY ONE token dictionary (Light OR Dark),
    // never both. ThemeApplier swaps this inner entry at runtime.
    [Fact]
    public void Generic_merges_exactly_one_token_dictionary()
    {
        var doc = XDocument.Load(ThemesPath("Generic.xaml"));
        var sourceAttrs = doc.Descendants()
            .SelectMany(el => el.Attributes())
            .Where(a => a.Name.LocalName == "Source")
            .Select(a => a.Value)
            .ToList();

        int tokenCount = sourceAttrs.Count(s =>
            s.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
            s.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase));

        tokenCount.Should().Be(1, "Generic must merge exactly one token dictionary (Light or Dark), not both");
    }
}
