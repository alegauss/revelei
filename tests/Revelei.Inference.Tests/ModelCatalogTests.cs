using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB70: the catalog is schema 2, and a fetched one is taken only when its signature verifies
/// against the key the app was built with and its sequence is not below the embedded copy's.
/// </summary>
public sealed class ModelCatalogTests : IDisposable
{
    private static readonly Uri Address = new("https://host/catalog.json");

    private readonly ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private string PublicKey => Convert.ToBase64String(signer.ExportSubjectPublicKeyInfo());

    public void Dispose() => signer.Dispose();

    [Fact]
    public void SchemaTwoReadsVersionsKindsRolesAndMemory()
    {
        ModelManifest catalog = ModelManifest.Parse(Catalog(sequence: 4));

        Assert.Equal(4, catalog.Sequence);
        ManifestModel upscale = Assert.IsType<ManifestModel>(catalog.Model("tiny-x4"));
        Assert.Equal(("2", ModelKind.Onnx, false), (upscale.ModelVersion, upscale.Kind, upscale.NonCommercial));
        Assert.Equal("https://host/tiny-x4-fp32.onnx", upscale.Variants["fp32"].Url);
        Assert.Equal(new ManifestMemory(1 << 20, 2 << 20), upscale.Quantisations!["fp32"].Memory);
        Assert.Equal("Sharper edges on text.", upscale.Note);

        ManifestModel diffusion = Assert.IsType<ManifestModel>(catalog.Model("tiny-diffusion"));
        Assert.Equal((ModelKind.Diffusion, true), (diffusion.Kind, diffusion.NonCommercial));
        Assert.Empty(diffusion.Variants);
        Assert.Equal(["transformer", "text-encoder", "vae"], diffusion.Quantisations!["q4"].Files.Select(f => f.Role));
    }

    [Fact]
    public void SchemaOneVariantsReadAsWeightsAlone()
    {
        ManifestModel model = ModelDownloads.Embedded.Models[0];

        ManifestPart weights = Assert.Single(model.Quantisations![ModelDownloads.Variant].Files);
        Assert.Equal(("weights", model.Variants[ModelDownloads.Variant]), (weights.Role, weights.File));
        Assert.Equal(0, ModelDownloads.Embedded.Sequence);
    }

    [Fact]
    public void ACatalogWithAPlainHttpFileOrAnOnnxModelWithoutWeightsIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => ModelManifest.Parse(Catalog(sequence: 1).Replace("https://host/tiny-diffusion-vae", "http://host/tiny-diffusion-vae", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ModelManifest.Parse(Catalog(sequence: 1).Replace("\"weights\"", "\"vae\"", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => ModelManifest.Parse(Catalog(sequence: 1).Replace("\"diffusion\"", "\"gguf\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ASignedCatalogNoOlderThanTheFloorIsTaken()
    {
        byte[] body = Encoding.UTF8.GetBytes(Catalog(sequence: 3));
        using HttpClient http = new(new Served(new() { [Address.AbsoluteUri] = body, [Address.AbsoluteUri + ".sig"] = Sign(body) }));

        CatalogRefresh refresh = await ModelCatalog.RefreshAsync(http, Address, PublicKey, Floor(3), TestContext.Current.CancellationToken);

        Assert.Equal(CatalogSource.Fetched, refresh.Source);
        Assert.Null(refresh.Kept);
        Assert.NotNull(refresh.Manifest.Model("tiny-diffusion"));
    }

    [Fact]
    public async Task ATamperedCatalogOrAnotherKeysSignatureKeepsTheFloor()
    {
        byte[] body = Encoding.UTF8.GetBytes(Catalog(sequence: 3));
        byte[] tampered = Encoding.UTF8.GetBytes(Catalog(sequence: 3).Replace("1048576", "1048577", StringComparison.Ordinal));
        ModelManifest floor = Floor(0);
        using ECDsa stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string strangerKey = Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo());

        CatalogRefresh altered = await Refresh(tampered, Sign(body), PublicKey, floor);
        CatalogRefresh foreign = await Refresh(body, Sign(body), strangerKey, floor);
        CatalogRefresh garbled = await Refresh(body, "not base64!"u8.ToArray(), PublicKey, floor);

        foreach (CatalogRefresh refresh in (CatalogRefresh[])[altered, foreign, garbled])
        {
            Assert.Equal(CatalogSource.Embedded, refresh.Source);
            Assert.Same(floor, refresh.Manifest);
            Assert.Contains("signature", refresh.Kept, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ASignedButOlderCatalogIsAReplayAndKeepsTheFloor()
    {
        byte[] body = Encoding.UTF8.GetBytes(Catalog(sequence: 2));
        ModelManifest floor = Floor(5);

        CatalogRefresh refresh = await Refresh(body, Sign(body), PublicKey, floor);

        Assert.Equal(CatalogSource.Embedded, refresh.Source);
        Assert.Same(floor, refresh.Manifest);
        Assert.Contains("older", refresh.Kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoNetworkOrNoSignatureKeepsTheFloorAndSaysWhy()
    {
        byte[] body = Encoding.UTF8.GetBytes(Catalog(sequence: 3));
        ModelManifest floor = Floor(0);
        using HttpClient unsigned = new(new Served(new() { [Address.AbsoluteUri] = body }));

        CatalogRefresh refresh = await ModelCatalog.RefreshAsync(unsigned, Address, PublicKey, floor, TestContext.Current.CancellationToken);

        Assert.Equal(CatalogSource.Embedded, refresh.Source);
        Assert.Same(floor, refresh.Manifest);
        Assert.StartsWith("The catalog could not be fetched", refresh.Kept, StringComparison.Ordinal);
    }

    [Fact]
    public void ACatalogBuiltAndSignedByTheScriptsVerifiesHere()
    {
        // scripts/build-manifest.py wrote it and scripts/sign-catalog.py signed it with a throwaway key.
        string fixture = Path.Combine(Repository.Root, "tests", "fixtures", "catalog");
        byte[] body = System.IO.File.ReadAllBytes(Path.Combine(fixture, "catalog.json"));
        string signature = System.IO.File.ReadAllText(Path.Combine(fixture, "catalog.json.sig"));
        string publicKey = System.IO.File.ReadAllText(Path.Combine(fixture, "public-key.txt")).Trim();

        ModelManifest catalog = ModelCatalog.Accept(body, signature, publicKey, Floor(7));

        Assert.Equal(7, catalog.Sequence);
        Assert.Equal(("1", false), (catalog.Model("tiny-x4")!.ModelVersion, catalog.Model("tiny-x4")!.NonCommercial));
        Assert.True(catalog.Model("tiny-matte")!.NonCommercial);
        Assert.Equal(2048, catalog.Model("tiny-matte")!.Variants["fp32"].Bytes);
    }

    [Fact]
    public void TheGenerationModelsTheScriptsListGetTheVerdictsTheSpikeMeasured()
    {
        // PB79: scripts/generation-sources.json through build-manifest.py's catalog(). On the
        // RTX 4060 8 GB desk Z-Image Turbo Q4 and FLUX.2 klein fit, and Qwen-Image runs by offload.
        string fixture = Path.Combine(Repository.Root, "tests", "fixtures", "catalog");
        ModelManifest catalog = ModelManifest.Parse(System.IO.File.ReadAllText(Path.Combine(fixture, "catalog.json")));
        MachineProfile rtx4060 = new(8L << 30, 64L << 30, 500L << 30);

        ManifestModel zImage = catalog.Model("z-image-turbo")!;
        Assert.Equal((ModelKind.Diffusion, (ManifestFile?)null), (zImage.Kind, zImage.Meta));
        Assert.Equal(["transformer", "text-encoder", "vae"], zImage.Quantisations!["q4_k"].Files.Select(f => f.Role));
        Assert.Equal(("q4_k", Fit.RunsWell), Recommended(zImage));
        Assert.Equal(Fit.RunsSlower, HardwareFit.Judge(zImage.Quantisations["q8_0"], rtx4060).Fit);
        Assert.Equal(("q4_0", Fit.RunsWell), Recommended(catalog.Model("flux-2-klein-4b")!));
        Assert.Equal(("q4_0", Fit.RunsSlower), Recommended(catalog.Model("qwen-image")!));
        Assert.Equal("Generate", ModelCards.GroupOf(zImage));

        (string, Fit) Recommended(ManifestModel model) =>
            HardwareFit.Recommend(model, rtx4060) is { } chosen ? (chosen.Quantisation, chosen.Verdict.Fit) : (string.Empty, Fit.DoesNotFit);
    }

    private static async Task<CatalogRefresh> Refresh(byte[] body, byte[] signature, string publicKey, ModelManifest floor)
    {
        using HttpClient http = new(new Served(new() { [Address.AbsoluteUri] = body, [Address.AbsoluteUri + ".sig"] = signature }));
        return await ModelCatalog.RefreshAsync(http, Address, publicKey, floor, TestContext.Current.CancellationToken);
    }

    private byte[] Sign(byte[] body) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(signer.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));

    private static ModelManifest Floor(long sequence) => new(2, "floor", [], sequence);

    private static object File(string url, long bytes = 1 << 20) => new { url, bytes, sha256 = new string('0', 64) };

    private static string Catalog(long sequence) => JsonSerializer.Serialize(new
    {
        schema = 2,
        version = "3",
        sequence,
        models = new object[]
        {
            new
            {
                name = "tiny-x4",
                version = "2",
                kind = "onnx",
                meta = File("https://host/tiny-x4-meta.json", 10),
                quantisations = new Dictionary<string, object>
                {
                    ["fp32"] = new
                    {
                        memory = new { video_bytes = 1 << 20, system_bytes = 2 << 20 },
                        files = new[] { new { role = "weights", file = File("https://host/tiny-x4-fp32.onnx") } },
                    },
                },
                license = "BSD-3-Clause",
                note = "Sharper edges on text.",
            },
            new
            {
                name = "tiny-diffusion",
                version = "1",
                kind = "diffusion",
                meta = File("https://host/tiny-diffusion-meta.json", 10),
                quantisations = new Dictionary<string, object>
                {
                    ["q4"] = new
                    {
                        files = new[]
                        {
                            new { role = "transformer", file = File("https://host/tiny-diffusion-transformer") },
                            new { role = "text-encoder", file = File("https://host/tiny-diffusion-text-encoder") },
                            new { role = "vae", file = File("https://host/tiny-diffusion-vae") },
                        },
                    },
                },
                license = "flux-1-dev-non-commercial",
                non_commercial = true,
            },
        },
    });

    private sealed class Served(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsoluteUri, out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
