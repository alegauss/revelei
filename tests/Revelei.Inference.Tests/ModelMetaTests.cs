namespace Revelei.Inference.Tests;

/// <summary>
/// PB11: the app reads the meta.json the export script writes. scripts/meta.example.json is a
/// real one (realesrgan-x4plus), and the Python suite checks it and every emitted file against
/// scripts/meta.schema.json, so this side and that side share one contract.
/// </summary>
public sealed class ModelMetaTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "pb-meta-" + Guid.NewGuid().ToString("N"));

    public ModelMetaTests() => Directory.CreateDirectory(scratch);

    public void Dispose() => Directory.Delete(scratch, recursive: true);

    private static string Example => Path.Combine(Repository.Root, "scripts", "meta.example.json");

    [Fact]
    public void TheExampleLoads()
    {
        ModelMeta meta = ModelMeta.Load(Example);

        Assert.Equal("realesrgan-x4plus", meta.Name);
        Assert.Equal("upscale", meta.Task);
        Assert.Equal(4, meta.Scale);
        Assert.Equal("NCHW", meta.Input.Layout);
        Assert.Null(meta.Input.Size);
        Assert.Null(meta.Input.Batch);
        Assert.Equal([0.0, 1.0], meta.Input.ValueRange);
        Assert.Equal(256, meta.Tile.Recommended);
        Assert.True(meta.Tile.LargestActivationBytesPerPixel > 0);
        Assert.Matches("^[0-9a-f]{64}$", meta.Variants["fp32"].Sha256);
        Assert.Contains("int8", meta.Variants.Keys);
        Assert.StartsWith("https://", meta.Upstream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void AFixedBatchReadsAsANumber()
    {
        string path = Rewrite("\"batch\": \"dynamic\"", "\"batch\": 1");

        Assert.Equal(1, ModelMeta.Load(path).Input.Batch);
    }

    [Fact]
    public void AnUnknownSchemaIsRefused()
    {
        string path = Rewrite("\"schema\": 1", "\"schema\": 2");

        Assert.Throws<InvalidDataException>(() => ModelMeta.Load(path));
    }

    [Fact]
    public void AMissingRequiredFieldIsRefused()
    {
        string path = Rewrite("\"scale\": 4,", string.Empty);

        Assert.Throws<InvalidDataException>(() => ModelMeta.Load(path));
    }

    [Fact]
    public void AFileWithoutFp32IsRefused()
    {
        string path = Rewrite("\"fp32\":", "\"fp64\":");

        Assert.Throws<InvalidDataException>(() => ModelMeta.Load(path));
    }

    private string Rewrite(string from, string to)
    {
        string text = File.ReadAllText(Example);
        Assert.Contains(from, text, StringComparison.Ordinal);
        string path = Path.Combine(scratch, "meta.json");
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
        return path;
    }
}
