using System.Net;
using System.Net.Sockets;
using Revelei.Inference.Pipelines;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB58: what face restore and background removal fetch on first use, read from the embedded
/// manifest, and a failed download explained as what happened and what to do.
/// </summary>
public sealed class FirstUseTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-firstuse-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ALeanInstallFetchesGfpganForFacesAndRmbgWithViTMatteForTheBackground()
    {
        using ModelLibrary library = Library();

        Assert.Equal(["gfpgan-v1.4"], FirstUse.Needed(FirstUse.Faces, library, ModelDownloads.Embedded, "fp16").Select(m => m.Name));
        Assert.Equal(
            ["rmbg-1.4", "vitmatte-small-composition-1k"],
            FirstUse.Needed(FirstUse.Background, library, ModelDownloads.Embedded, "fp16").Select(m => m.Name));
    }

    [Fact]
    public void AnOperationWhosePrimaryModelIsHereNeedsNothingEvenWithoutItsCompanion()
    {
        // ViTMatte only refines; without it Remove background runs on the guided filter (PB47).
        using ModelLibrary library = Library((RemoveBackground.Model, "fp32"));

        Assert.Empty(FirstUse.Needed(FirstUse.Background, library, ModelDownloads.Embedded, "fp16"));
    }

    [Fact]
    public void TheCompanionAlreadyHereIsNotFetchedAgain()
    {
        using ModelLibrary library = Library((Matting.Model, "fp32"));

        Assert.Equal(["rmbg-1.4"], FirstUse.Needed(FirstUse.Background, library, ModelDownloads.Embedded, "fp16").Select(m => m.Name));
    }

    [Fact]
    public void TheSourceIsNamedByHostAndRepository()
    {
        ManifestModel rmbg = ModelDownloads.Embedded.Model(RemoveBackground.Model)!;
        ManifestModel gfpgan = ModelDownloads.Embedded.Model(FaceRestore.Model)!;

        Assert.Equal("huggingface.co/briaai/RMBG-1.4", FirstUse.SourceOf(rmbg.Variants[ModelDownloads.Variant]));
        Assert.Equal("huggingface.co/alegauss/photo-boost-models", FirstUse.SourceOf(gfpgan.Variants[ModelDownloads.Variant]));
    }

    [Fact]
    public void AFailureSaysWhatHappenedAndWhatToDo()
    {
        Assert.Equal("This PC seems to be offline.", DownloadFailure.Explain(new HttpRequestException(HttpRequestError.NameResolutionError, "no dns", new SocketException()), 0).What);
        Assert.Contains("503", DownloadFailure.Explain(new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable), 0).What, StringComparison.Ordinal);
        Assert.StartsWith("A file arrived damaged", DownloadFailure.Explain(new InvalidDataException("bad"), 0).What, StringComparison.Ordinal);
        Assert.Equal("The disk is full.", DownloadFailure.Explain(new IOException("full", unchecked((int)0x80070070)), 0).What);
        Assert.EndsWith("The 120 MB already downloaded are kept.", DownloadFailure.Explain(new HttpRequestException("gone"), 120L << 20).Todo, StringComparison.Ordinal);
        Assert.DoesNotContain("kept", DownloadFailure.Explain(new HttpRequestException("gone"), 0).Todo, StringComparison.Ordinal);
    }

    private ModelLibrary Library(params (string Model, string Variant)[] onDisk)
    {
        Directory.CreateDirectory(root);
        foreach ((string model, string variant) in onDisk)
        {
            string folder = Path.Combine(root, model);
            Directory.CreateDirectory(folder);
            File.Copy(Path.Combine(Repository.Root, "scripts", "meta.example.json"), Path.Combine(folder, "meta.json"), overwrite: true);
            File.WriteAllBytes(Path.Combine(folder, variant + ".onnx"), [0]);
        }

        return new ModelLibrary([root], new ProviderSelection(ExecutionProvider.Cpu, null, null));
    }
}
