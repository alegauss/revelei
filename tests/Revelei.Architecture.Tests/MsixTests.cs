using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Revelei.Architecture.Tests;

/// <summary>
/// PB90: the Store package's manifest. Its identity is the Partner Center reservation, which the
/// Store compares byte for byte on upload; its Windows floor is the installer's (PB53); its version
/// is filled from Directory.Build.props at build time (PB35); and every logo it names, with the
/// target sizes the shell asks for, is a file in build/msix/Assets.
/// </summary>
public sealed partial class MsixTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    private static string MsixFolder => Path.Combine(Repository.Root, "build", "msix");

    private static XDocument Manifest() => XDocument.Load(Path.Combine(MsixFolder, "AppxManifest.xml"));

    [Fact]
    public void TheIdentityIsThePartnerCenterReservation()
    {
        XElement identity = Manifest().Root!.Element(Foundation + "Identity")!;

        Assert.Equal("Alegauss.Revelei", identity.Attribute("Name")!.Value);
        Assert.Equal("CN=1ADDE401-7D09-4729-8E49-C28AEEA80170", identity.Attribute("Publisher")!.Value);
        Assert.Equal("x64", identity.Attribute("ProcessorArchitecture")!.Value);
        Assert.Equal("Alegauss", Manifest().Root!.Element(Foundation + "Properties")!.Element(Foundation + "PublisherDisplayName")!.Value);
    }

    [Fact]
    public void TheVersionIsFilledFromDirectoryBuildProps()
    {
        // A literal version here would be a second place to bump, and the one forgotten.
        Assert.Equal("{VERSION}", Manifest().Root!.Element(Foundation + "Identity")!.Attribute("Version")!.Value);
        Assert.Contains("Directory.Build.props", File.ReadAllText(Path.Combine(Repository.Root, "build", "build-msix.ps1")), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageRefusesTheWindowsTheInstallerRefuses()
    {
        string minimum = Manifest().Descendants(Foundation + "TargetDeviceFamily").Single().Attribute("MinVersion")!.Value;
        Match installer = InstallerMinVersion().Match(File.ReadAllText(Path.Combine(Repository.Root, "build", "installer.iss")));

        Assert.True(installer.Success, "installer.iss sets no MinVersion");
        Assert.Equal(Version.Parse(installer.Groups[1].Value), new Version(Version.Parse(minimum).ToString(3)));
    }

    [Fact]
    public void TheExecutableIsTheOneThePublishWrites()
    {
        string? assembly = XDocument.Load(Path.Combine(Repository.Root, "src", "Revelei.App", "Revelei.App.csproj"))
            .Descendants("AssemblyName").Single().Value;

        Assert.Equal(assembly + ".exe", Manifest().Descendants(Foundation + "Application").Single().Attribute("Executable")!.Value);
    }

    [Fact]
    public void EveryLogoTheManifestNamesIsInTheAssetsFolder()
    {
        XDocument manifest = Manifest();
        string[] named =
        [
            manifest.Descendants(Foundation + "Logo").Single().Value,
            .. manifest.Descendants(Uap + "VisualElements").SelectMany(e => new[] { e.Attribute("Square150x150Logo")!.Value, e.Attribute("Square44x44Logo")!.Value }),
        ];
        string[] missing = [.. named.Where(logo => !File.Exists(Path.Combine(MsixFolder, logo)))];

        Assert.True(missing.Length == 0, "Logos the manifest names with no file (npm run msix-assets in site/): " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(256)]
    public void EveryTaskbarSizeHasAnUnplatedTwin(int size)
    {
        // Without the unplated variant the shell draws the logo on a square in the accent colour.
        string assets = Path.Combine(MsixFolder, "Assets");

        Assert.True(File.Exists(Path.Combine(assets, $"Square44x44Logo.targetsize-{size}.png")));
        Assert.True(File.Exists(Path.Combine(assets, $"Square44x44Logo.targetsize-{size}_altform-unplated.png")));
    }

    [GeneratedRegex(@"^MinVersion=([0-9.]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex InstallerMinVersion();
}
