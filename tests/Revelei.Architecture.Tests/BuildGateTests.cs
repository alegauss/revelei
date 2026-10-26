using System.Xml.Linq;

namespace Revelei.Architecture.Tests;

/// <summary>
/// PB2: the analyzer gate. Directory.Build.props turns warnings into errors and the performance
/// analyzers on for every project; these fail when the root file stops doing so, or when one
/// project file quietly turns a gate back off for itself.
/// </summary>
public sealed class BuildGateTests
{
    public static TheoryData<string, string> RootGates => new()
    {
        { "TreatWarningsAsErrors", "true" },
        { "Nullable", "enable" },
        { "EnableNETAnalyzers", "true" },
        { "AnalysisModePerformance", "All" },
        { "PlatformTarget", "x64" },
    };

    private static readonly string[] Gated =
    [
        "TreatWarningsAsErrors",
        "Nullable",
        "EnableNETAnalyzers",
        "AnalysisLevel",
        "AnalysisModePerformance",
        "PlatformTarget",
        "WarningsNotAsErrors",
        "RunAnalyzers",
    ];

    [Theory]
    [MemberData(nameof(RootGates))]
    public void RootPropsSetsTheGate(string property, string value)
    {
        string? actual = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"))
            .Descendants(property)
            .Select(element => element.Value.Trim())
            .LastOrDefault();

        Assert.Equal(value, actual);
    }

    [Fact]
    public void NoProjectOverridesAGate()
    {
        string[] overrides = Directory.GetFiles(Repository.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Where(element => Gated.Contains(element.Name.LocalName, StringComparer.Ordinal))
                .Select(element => $"{Path.GetRelativePath(Repository.Root, path)}: {element.Name.LocalName}"))
            .ToArray();

        Assert.True(overrides.Length == 0, "Gates are set in Directory.Build.props only:\n" + string.Join("\n", overrides));
    }
}
