using System.Xml.Linq;

namespace Revelei.Architecture.Tests;

/// <summary>
/// PB35: the version lives in Directory.Build.props alone. The installer reads it back from the
/// published exe, and app.manifest, which cannot read a property, must say the same.
/// </summary>
public sealed class VersionTests
{
    private static readonly XNamespace Assembly = "urn:schemas-microsoft-com:asm.v1";

    [Fact]
    public void TheManifestCarriesTheBuildVersion()
    {
        string version = XDocument.Load(Path.Combine(Repository.Root, "Directory.Build.props"))
            .Descendants("Version")
            .Single()
            .Value.Trim();
        string manifest = XDocument.Load(Path.Combine(Repository.Root, "src", "Revelei.App", "app.manifest"))
            .Root!
            .Element(Assembly + "assemblyIdentity")!
            .Attribute("version")!
            .Value;

        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal(version + ".0", manifest);
    }

    [Fact]
    public void NoProjectSetsAVersionOfItsOwn()
    {
        string[] own = Directory.GetFiles(Path.Combine(Repository.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => XDocument.Load(path).Descendants().Any(e => e.Name.LocalName is "Version" or "VersionPrefix" or "AssemblyVersion" or "FileVersion"))
            .Select(path => Path.GetRelativePath(Repository.Root, path))
            .ToArray();

        Assert.True(own.Length == 0, "The version is Directory.Build.props's:\n" + string.Join("\n", own));
    }
}
