using System.IO;
using Revelei.Inference;

namespace Revelei.App.Tests;

/// <summary>PB39: every flag parses both ways, and anything else is refused with a message that names it.</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void ManifestNamesAFileThatMustExist()
    {
        // PB59: the UI cases' download fixture, read in place of the pinned manifest.
        string manifest = Path.Combine(Repository.Root, "cases", "fixtures", "download", "manifest.json");

        Assert.Equal(manifest, CommandLine.Parse(["--manifest", manifest]).Options!.Manifest);
        Assert.Equal("--manifest nowhere.json is not a file.", CommandLine.Parse(["--manifest", "nowhere.json"]).Error);
    }

    [Fact]
    public void NoArgumentsIsAPlainStart()
    {
        ParsedCommandLine parsed = CommandLine.Parse([]);

        Assert.True(parsed.Ok);
        Assert.Equal(new AppOptions(), parsed.Options);
        Assert.Equal(AppOptions.DefaultModels, parsed.Options!.ModelsFolder);
    }

    [Fact]
    public void EveryFlagParsesWithASpaceOrAnEqualsSign()
    {
        ParsedCommandLine parsed = CommandLine.Parse(
            ["--open", @"C:\photos\a b.jpg", "--models=D:\\models", "--provider", "cpu", "--export=out.png", "--quality", "High"]);

        Assert.Equal(
            new AppOptions(@"C:\photos\a b.jpg", @"D:\models", ProviderPreference.Cpu, "out.png", QualityMode.High),
            parsed.Options);
        Assert.Equal(@"D:\models", parsed.Options!.ModelsFolder);
        Assert.Equal([@"D:\models"], parsed.Options.ModelFolders);
    }

    [Fact]
    public void WithoutModelsTheBundledSetIsReadBeforeTheDownloads()
    {
        // PB51: the installer's models beside the exe, then the folder downloads go to.
        AppOptions options = new();

        Assert.Equal([Path.Combine(AppContext.BaseDirectory, "models"), AppOptions.DefaultModels], options.ModelFolders);
    }

    [Theory]
    [InlineData("auto", ProviderPreference.Auto)]
    [InlineData("dml", ProviderPreference.DirectML)]
    [InlineData("cpu", ProviderPreference.Cpu)]
    [InlineData("winml", ProviderPreference.WindowsML)] // PB43
    public void TheProviderFlagTakesItsFourValues(string value, ProviderPreference expected) =>
        Assert.Equal(expected, CommandLine.Parse(["--provider", value]).Options!.Provider);

    [Theory]
    [InlineData("standard", QualityMode.Standard)]
    [InlineData("compact", QualityMode.Standard)] // PB29: the earlier name still runs a script
    [InlineData("high", QualityMode.High)]
    public void TheQualityFlagNamesStandardOrHigh(string value, QualityMode expected) =>
        Assert.Equal(expected, CommandLine.Parse(["--quality", value]).Options!.Quality);

    [Theory]
    [InlineData(new[] { "--zoom", "2" }, "Unknown option --zoom")]
    [InlineData(new[] { "photo.jpg" }, "use --open \"photo.jpg\"")]
    [InlineData(new[] { "--open" }, "--open needs a value")]
    [InlineData(new[] { "--open", "--models", "x" }, "--open needs a value")]
    [InlineData(new[] { "--export=" }, "--export needs a value")]
    [InlineData(new[] { "--provider", "cuda" }, "not one of auto, winml, dml, cpu")]
    [InlineData(new[] { "--quality", "fast" }, "not one of standard, high")]
    [InlineData(new[] { "--open", "a.jpg", "--open", "b.jpg" }, "--open is given twice")]
    public void AnythingElseIsRefusedWithAMessage(string[] args, string message)
    {
        ParsedCommandLine parsed = CommandLine.Parse(args);

        Assert.False(parsed.Ok);
        Assert.Contains(message, parsed.Error, StringComparison.Ordinal);
    }
}
