namespace Revelei.Inference.Tests;

/// <summary>
/// PB80: the Create screen offers each downloaded model whose record names a transformer, a text
/// encoder and a VAE present in the shared folder, with the catalog's title, defaults and verdict.
/// </summary>
public sealed class GenerationModelsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-generation-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnInstalledModelResolvesToItsSharedFilesWithTheCatalogsDefaults()
    {
        Install("z-image-turbo", "q4_k", ("transformer", "t1"), ("text-encoder", "te"), ("vae", "v"));
        ModelManifest catalog = ModelManifest.Parse(File.ReadAllText(Path.Combine(Repository.Root, "tests", "fixtures", "catalog", "catalog.json")));

        GenerationModel model = Assert.Single(GenerationModels.Installed(root, catalog, new MachineProfile(8L << 30, 64L << 30, 500L << 30)));

        Assert.Equal(("Z-Image Turbo", "q4_k"), (model.Title, model.Quantisation));
        Assert.Equal(Path.Combine(root, ModelDownloads.SharedFolder, "t1"), model.Transformer);
        Assert.Equal(new ModelDefaults(8, 1), model.Defaults);
        Assert.Equal(Fit.RunsWell, model.Verdict!.Fit);
    }

    [Fact]
    public void AModelMissingAFileOrARoleIsNotOffered()
    {
        Install("half-gone", "q4", ("transformer", "t2"), ("text-encoder", "te2"), ("vae", "v2"));
        File.Delete(Path.Combine(root, ModelDownloads.SharedFolder, "v2"));
        Install("an-upscaler", "fp32", ("weights", "w"));

        Assert.Empty(GenerationModels.Installed(root, ModelDownloads.Embedded, new MachineProfile(0, 16L << 30, 100L << 30)));
    }

    [Fact]
    public void AModelTheCatalogNoLongerListsStillGeneratesWithTheFallbackDefaults()
    {
        Install("old-model", "q4", ("transformer", "t3"), ("text-encoder", "te3"), ("vae", "v3"));

        GenerationModel model = Assert.Single(GenerationModels.Installed(root, ModelDownloads.Embedded, new MachineProfile(0, 16L << 30, 100L << 30)));

        Assert.Equal((GenerationModels.Fallback, (FitVerdict?)null), (model.Defaults, model.Verdict));
    }

    [Fact]
    public void APcWithNoModelIsOfferedTheFirstThatRunsWellElseTheFirstThatRuns()
    {
        ModelManifest catalog = ModelManifest.Parse(File.ReadAllText(Path.Combine(Repository.Root, "tests", "fixtures", "catalog", "catalog.json")));

        var rtx4060 = GenerationModels.Recommend(catalog, new MachineProfile(8L << 30, 64L << 30, 500L << 30));
        var cpuOnly = GenerationModels.Recommend(catalog, new MachineProfile(0, 32L << 30, 500L << 30));
        var tiny = GenerationModels.Recommend(catalog, new MachineProfile(0, 4L << 30, 500L << 30));

        Assert.Equal(("z-image-turbo", "q4_k", Fit.RunsWell), (rtx4060!.Value.Model.Name, rtx4060.Value.Quantisation, rtx4060.Value.Verdict.Fit));
        Assert.Equal(("z-image-turbo", Fit.CpuOnly), (cpuOnly!.Value.Model.Name, cpuOnly.Value.Verdict.Fit));
        Assert.Null(tiny);
        Assert.Null(GenerationModels.Recommend(ModelDownloads.Embedded, new MachineProfile(8L << 30, 64L << 30, 500L << 30)));
    }

    private void Install(string model, string quantisation, params (string Role, string Sha)[] files)
    {
        string folder = Path.Combine(root, model);
        string shared = Path.Combine(root, ModelDownloads.SharedFolder);
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(shared);
        foreach ((_, string sha) in files)
        {
            File.WriteAllText(Path.Combine(shared, sha), sha);
        }

        new InstalledRecord("1", quantisation, DateTimeOffset.Now, files.ToDictionary(f => f.Role, f => f.Sha)).Write(folder);
    }
}
