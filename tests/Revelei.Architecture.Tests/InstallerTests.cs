using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Revelei.Architecture.Tests;

/// <summary>
/// PB53: the installer refuses a Windows the app cannot run on. Windows ML's own README names
/// Windows 10 19H1 (build 18362) as its minimum, and every operation runs through it (PB43).
/// </summary>
public sealed partial class InstallerTests
{
    // The package version the floor below was read from; an upgrade has to be checked again.
    private const string WindowsMLVersion = "2.1.74";
    private static readonly Version WindowsMLMinimum = new(10, 0, 18362);

    [Fact]
    public void TheInstallerRefusesAWindowsOlderThanWindowsMLNeeds()
    {
        string script = File.ReadAllText(Path.Combine(Repository.Root, "build", "installer.iss"));
        Match line = MinVersion().Match(script);

        Assert.True(line.Success, "installer.iss sets no MinVersion");
        Assert.True(Version.Parse(line.Groups[1].Value) >= WindowsMLMinimum, $"MinVersion={line.Groups[1].Value} is below Windows ML's {WindowsMLMinimum}");
    }

    [Fact]
    public void TheFloorWasReadForTheWindowsMLPackageTheAppReferences()
    {
        string? version = XDocument.Load(Path.Combine(Repository.Root, "src", "Revelei.Inference", "Revelei.Inference.csproj"))
            .Descendants("PackageReference")
            .FirstOrDefault(reference => reference.Attribute("Include")?.Value == "Microsoft.Windows.AI.MachineLearning")
            ?.Attribute("Version")?.Value;

        Assert.True(
            version == WindowsMLVersion,
            $"Microsoft.Windows.AI.MachineLearning is {version}; read its README's minimum Windows, then update WindowsMLVersion and, if it rose, MinVersion in installer.iss.");
    }

    [Fact]
    public void TheInstallerLeavesOutTheModelsFirstUseDownloads()
    {
        // PB58: GFPGAN, RMBG-1.4 and ViTMatte are fetched when their operation is first used.
        string script = File.ReadAllText(Path.Combine(Repository.Root, "build", "stage-models.ps1"));
        string[] staged = [.. Quoted().Matches(Required().Match(script).Groups[1].Value).Select(m => m.Groups[1].Value)];

        Assert.Equal(["realesrgan-x4plus", "realesrgan-x2plus", "yunet-2023mar", "scunet-color-real-psnr"], staged);
    }

    [GeneratedRegex(@"^MinVersion=([0-9.]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex MinVersion();

    [GeneratedRegex(@"^\$required = @\((.*?)\)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Required();

    [GeneratedRegex("'([^']+)'")]
    private static partial Regex Quoted();
}
