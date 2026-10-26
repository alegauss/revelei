using System.Text.RegularExpressions;

namespace Revelei.Architecture.Tests;

/// <summary>
/// PB37: every colour Revelei decides is written in Brand.cs and nowhere else, so changing one
/// is one edit and two surfaces cannot drift apart (from quickshell's ColourTests). The app's
/// sources only, C# and XAML; the matcher is run against text written to break it first.
/// </summary>
public sealed partial class ColourTests
{
    private const string Home = "Brand.cs";

    /// <summary>WPF's constructors given literal channels.</summary>
    [GeneratedRegex(@"\bColor\.From(?:Rgb|Argb|ScRgb)\s*\(\s*(?:0x[0-9A-Fa-f]{1,2}|\d)")]
    private static partial Regex WpfLiteral { get; }

    /// <summary>A hex colour in a string or a XAML attribute: "#1E1E1E", "#FF1E1E1E", "#FFF".</summary>
    [GeneratedRegex(@"""#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3,4})""")]
    private static partial Regex HexLiteral { get; }

    /// <summary>A named colour from WPF's tables, Colors.Red or Brushes.Red, except Transparent.</summary>
    [GeneratedRegex(@"\b(?:Colors|Brushes)\.(?!Transparent\b)[A-Z][A-Za-z]+")]
    private static partial Regex NamedLiteral { get; }

    /// <summary>A XAML attribute given a named colour: Background="Red", Fill="White".</summary>
    [GeneratedRegex(@"\b(?:Background|Foreground|Fill|Stroke|BorderBrush|Color)=""(?!Transparent"")(?!\{)[A-Za-z]+""")]
    private static partial Regex XamlNamed { get; }

    [Fact]
    public void EveryColourIsWrittenInBrand()
    {
        string[] strays = [.. Sources()
            .Where(path => Path.GetFileName(path) != Home)
            .SelectMany(path => Declared(File.ReadAllText(path)).Select(found => $"{Path.GetRelativePath(Repository.Root, path)}: {found}"))];

        Assert.True(strays.Length == 0, "Colours are declared in Brand.cs; refer to Brand or a Fluent brush instead: " + string.Join("; ", strays));
    }

    [Fact]
    public void BrandDeclaresSomething() =>
        Assert.Contains(Sources(), path => Path.GetFileName(path) == Home);

    [Theory]
    [InlineData("var c = Color.FromRgb(10, 20, 30);")]
    [InlineData("Color.FromArgb(0xFF, 1, 2, 3)")]
    [InlineData("<Border Background=\"#1E1E1E\" />")]
    [InlineData("string s = \"#FF00FF00\";")]
    [InlineData("brush = Brushes.Red;")]
    [InlineData("<Rectangle Fill=\"White\" />")]
    public void TheMatcherFindsEverySpelling(string text) => Assert.NotEmpty(Declared(text));

    [Theory]
    [InlineData("Color.FromRgb(Brand.DividerR, Brand.DividerG, Brand.DividerB)")]
    [InlineData("<Border Background=\"{DynamicResource CardBackgroundFillColorDefaultBrush}\" />")]
    [InlineData("<Border Background=\"Transparent\" />")]
    [InlineData("ground.SetValue(Border.BackgroundProperty, Brushes.Transparent);")]
    [InlineData("// see issue #123 and \"#region\"")]
    public void TheMatcherIgnoresWhatOnlyLooksLikeAColour(string text) => Assert.Empty(Declared(text));

    private static IEnumerable<string> Declared(string text) =>
        new[] { WpfLiteral, HexLiteral, NamedLiteral, XamlNamed }.SelectMany(regex => regex.Matches(text).Select(m => m.Value));

    private static string[] Sources() =>
        [.. Directory.GetFiles(Path.Combine(Repository.Root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
}
