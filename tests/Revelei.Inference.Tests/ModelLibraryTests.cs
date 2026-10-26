namespace Revelei.Inference.Tests;

/// <summary>PB46: what the About panel lists from a models folder.</summary>
public sealed class ModelLibraryTests : IDisposable
{
    private static readonly ProviderSelection Cpu = new(ExecutionProvider.Cpu, null, null);
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-library-" + Guid.NewGuid().ToString("N"));

    public ModelLibraryTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void InstalledListsReadableModelsWithTheVariantsOnDisk()
    {
        string example = File.ReadAllText(Path.Combine(Repository.Root, "scripts", "meta.example.json"));
        Write("realesrgan-x4plus", example, "fp32.onnx", "int8.onnx");
        Write("broken", "{ not json", "fp32.onnx");
        Directory.CreateDirectory(Path.Combine(root, "no-meta"));
        using ModelLibrary library = new(root, Cpu);

        InstalledModel model = Assert.Single(library.Installed());

        Assert.Equal("realesrgan-x4plus", model.Name);
        Assert.Equal(["fp32", "int8"], model.Variants);
        Assert.StartsWith("https://", model.Meta.Upstream.LicenseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderThatIsNotThereListsNothing()
    {
        using ModelLibrary library = new(Path.Combine(root, "absent"), Cpu);

        Assert.Empty(library.Installed());
    }

    [Fact]
    public void EachVariantIsReadFromTheFirstRootThatHasIt()
    {
        // PB51: the installer's FP16 beside the app, a downloaded FP32 in the user's folder.
        string example = File.ReadAllText(Path.Combine(Repository.Root, "scripts", "meta.example.json"));
        Write("bundled/realesrgan-x4plus", example, "fp16.onnx");
        Write("downloads/realesrgan-x4plus", example, "fp32.onnx");
        using ModelLibrary library = new([Path.Combine(root, "bundled"), Path.Combine(root, "absent"), Path.Combine(root, "downloads")], Cpu);

        Assert.True(library.Has("realesrgan-x4plus", "fp16"));
        Assert.True(library.Has("realesrgan-x4plus", "fp32"));
        Assert.False(library.Has("realesrgan-x4plus", "int8"));
        InstalledModel model = Assert.Single(library.Installed());
        Assert.Equal(["fp16", "fp32"], model.Variants);
    }

    private void Write(string name, string meta, params string[] files)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "meta.json"), meta);
        foreach (string file in files)
        {
            File.WriteAllBytes(Path.Combine(folder, file), [0]);
        }
    }
}
