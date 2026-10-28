using System.Security.Cryptography;
using System.Text.Json;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB75: the Models panel's cards, grouped, with their badge, verdict, recommended quantisation,
/// installed state and update, decided without a window.
/// </summary>
public sealed class ModelCardsTests : IDisposable
{
    private const long GB = 1L << 30;

    private static readonly MachineProfile Rtx4060 = new(8 * GB, 64 * GB, 500 * GB);

    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-cards-" + Guid.NewGuid().ToString("N"));

    private string Downloads => Path.Combine(root, "downloads");

    private string Bundled => Path.Combine(root, "bundled");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheEmbeddedCatalogGroupsEveryModelByWhatItDoes()
    {
        IReadOnlyList<ModelCard> cards = ModelCards.Build(ModelDownloads.Embedded, Downloads, null, Rtx4060);

        Assert.Equal(ModelDownloads.Embedded.Models.Count, cards.Count);
        Assert.DoesNotContain(cards, c => c.Group == "Other");
        Assert.Equal("Upscale", cards[0].Group);
        Assert.True(Assert.Single(cards, c => c.Name == "rmbg-1.4").NonCommercial);
        Assert.All(cards, c => Assert.Equal((Fit.RunsWell, "fp32", false), (c.Verdict!.Fit, c.Quantisation, c.Installed)));
        Assert.Equal(cards, ModelCards.Filter(cards, CardFilter.Available));
        Assert.Empty(ModelCards.Filter(cards, CardFilter.Installed));
    }

    [Fact]
    public void AGenerationModelRecommendsWhatFitsAndItsSizeIsThatQuantisations()
    {
        ModelCard card = Assert.Single(ModelCards.Build(Catalog(version: "1"), Downloads, null, Rtx4060), c => c.Name == "tiny-diffusion");

        Assert.Equal(("Generate", "q4", Fit.RunsWell), (card.Group, card.Quantisation, card.Verdict!.Fit));
        Assert.Equal(6 * GB + 10, card.DownloadBytes);
    }

    [Fact]
    public void ADownloadedModelWithANewerVersionOffersAnUpdateButTheInstallersDoesNot()
    {
        WriteInstalled(Downloads, "tiny-x4", "1");
        WriteInstalled(Bundled, "tiny-face", "1");

        IReadOnlyList<ModelCard> cards = ModelCards.Build(Catalog(version: "2"), Downloads, Bundled, Rtx4060);

        ModelCard upscale = Assert.Single(cards, c => c.Name == "tiny-x4");
        Assert.Equal((true, "1", UpdateState.UpdateAvailable, true, "Sharper."), (upscale.Installed, upscale.InstalledVersion, upscale.State, upscale.CanUpdate, upscale.Note));
        ModelCard face = Assert.Single(cards, c => c.Name == "tiny-face");
        Assert.Equal((true, true, false), (face.Installed, face.Bundled, face.CanUpdate));
        Assert.Equal(["tiny-x4"], ModelCards.Filter(cards, CardFilter.Updates).Select(c => c.Name));
        Assert.Equal(["tiny-diffusion"], ModelCards.Filter(cards, CardFilter.Available).Select(c => c.Name));
    }

    [Fact]
    public void AModelTheCatalogWithdrewKeepsItsCardLabelled()
    {
        WriteInstalled(Downloads, "old-model", "3");

        ModelCard withdrawn = Assert.Single(ModelCards.Build(Catalog(version: "1"), Downloads, null, Rtx4060), c => c.Name == "old-model");

        Assert.Equal((UpdateState.Withdrawn, true, "3", false), (withdrawn.State, withdrawn.Installed, withdrawn.InstalledVersion, withdrawn.CanUpdate));
    }

    private static void WriteInstalled(string folder, string model, string version)
    {
        string path = Path.Combine(folder, model);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "meta.json"), "{}");
        File.WriteAllText(Path.Combine(path, "fp32.onnx"), "weights");
        new InstalledRecord(version, "fp32", DateTimeOffset.Now, new Dictionary<string, string> { ["fp32.onnx"] = Sha("weights"), ["meta.json"] = Sha("{}") }).Write(path);
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private static object Listed(string name, long bytes, string sha) => new { url = $"https://host/{name}", bytes, sha256 = sha };

    private static object Onnx(string name, string task, string version) => new
    {
        name,
        version,
        kind = "onnx",
        task,
        meta = Listed($"{name}-meta", 2, Sha("{}")),
        quantisations = new Dictionary<string, object> { ["fp32"] = new { files = new[] { new { role = "weights", file = Listed($"{name}-fp32", 7, Sha("weights")) } } } },
        license = "BSD-3-Clause",
        note = "Sharper.",
    };

    private static ModelManifest Catalog(string version) => ModelManifest.Parse(JsonSerializer.Serialize(new
    {
        schema = 2,
        version = "c",
        sequence = 1,
        models = new object[]
        {
            Onnx("tiny-x4", "upscale", version),
            Onnx("tiny-face", "face-restore", version),
            new
            {
                name = "tiny-diffusion",
                version = "1",
                kind = "diffusion",
                meta = Listed("tiny-diffusion-meta", 10, Sha("d")),
                quantisations = new Dictionary<string, object>
                {
                    ["q8"] = new { memory = new { video_bytes = 12 * GB, system_bytes = 6 * GB }, files = new[] { new { role = "transformer", file = Listed("q8", 12 * GB, Sha("q8")) } } },
                    ["q4"] = new { memory = new { video_bytes = 6 * GB, system_bytes = 4 * GB }, files = new[] { new { role = "transformer", file = Listed("q4", 6 * GB, Sha("q4")) } } },
                },
                license = "apache-2.0",
            },
        },
    }));
}
