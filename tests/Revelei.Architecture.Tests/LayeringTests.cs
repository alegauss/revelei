using System.Xml.Linq;

namespace Revelei.Architecture.Tests;

/// <summary>
/// PB2: the direction of the project references, read out of the .csproj files. Imaging is the
/// bottom layer, Inference and Render each stand on it alone, and only the app sees all three. A
/// reference that inverts this fails here rather than in a review somebody has to remember.
/// PB78: Generation stands on Imaging alone too, and its host process on Generation alone.
/// </summary>
public sealed class LayeringTests
{
    private static readonly string[] Nothing = [];
    private static readonly string[] ImagingOnly = ["Revelei.Imaging"];
    private static readonly string[] EveryLayer = ["Revelei.Imaging", "Revelei.Inference", "Revelei.Render", "Revelei.Generation", "Revelei.Generation.Host"];

    // project, the projects it is allowed to reference
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["Revelei.Imaging"] = Nothing,
        ["Revelei.Inference"] = ImagingOnly,
        ["Revelei.Render"] = ImagingOnly,
        ["Revelei.Generation"] = ImagingOnly,
        ["Revelei.Generation.Host"] = ["Revelei.Generation"],
        ["Revelei.App"] = EveryLayer,
    };

    public static TheoryData<string, string[]> Layering
    {
        get
        {
            TheoryData<string, string[]> rows = [];
            foreach ((string project, string[] allowed) in Allowed)
            {
                rows.Add(project, allowed);
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Layering))]
    public void ProjectReferencesOnlyWhatItsLayerAllows(string project, string[] allowed)
    {
        string[] forbidden = ProjectReferencesOf(SourceProject(project))
            .Except(allowed, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            forbidden.Length == 0,
            $"{project} references {string.Join(", ", forbidden)}, which inverts the layering. " +
            $"It may reference {(allowed.Length == 0 ? "nothing" : string.Join(", ", allowed))}.");
    }

    [Fact]
    public void EverySourceProjectHasALayer()
    {
        string[] onDisk = Directory.GetDirectories(Path.Combine(Repository.Root, "src"))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] declared = Allowed.Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, onDisk);
    }

    [Fact]
    public void EveryProjectIsInTheSolution()
    {
        string[] inSolution = XDocument.Load(Path.Combine(Repository.Root, "Revelei.slnx"))
            .Descendants("Project")
            .Select(project => project.Attribute("Path")?.Value.Replace('\\', '/'))
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] onDisk = Directory.GetFiles(Repository.Root, "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Repository.Root, path).Replace('\\', '/'))
            .Where(path => path.StartsWith("src/", StringComparison.Ordinal) || path.StartsWith("tests/", StringComparison.Ordinal) || path.StartsWith("tools/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(onDisk, inSolution);
    }

    [Fact]
    public void TheUiCasesReferenceNeitherTheAppNorWpf()
    {
        // PB38: a case asserts against what a user launches, not against types it was compiled with.
        XDocument cases = XDocument.Load(Path.Combine(Repository.Root, "tests", "Revelei.Cases", "Revelei.Cases.csproj"));

        Assert.Empty(cases.Descendants("ProjectReference"));
        Assert.DoesNotContain(cases.Descendants("UseWPF"), element => element.Value.Trim() == "true");
        Assert.Equal(["Winwright"], cases.Descendants("PackageReference").Select(p => p.Attribute("Include")?.Value));
    }

    private static string SourceProject(string project) =>
        Path.Combine(Repository.Root, "src", project, project + ".csproj");

    private static string[] ProjectReferencesOf(string projectFile) =>
        XDocument.Load(projectFile)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', Path.DirectorySeparatorChar)))
            .ToArray();
}
