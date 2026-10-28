using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB71: a download writes installed.json, and comparing it with the catalog says whether each
/// model is up to date, has an update, or was withdrawn.
/// </summary>
public sealed class ModelUpdatesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-updates-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> served = new(StringComparer.Ordinal);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ADownloadRecordsWhatItFetchedAndReadsAsUpToDate()
    {
        ModelManifest catalog = Catalog("2", ("tiny-x4", "weights"u8.ToArray()));

        await Download(catalog);

        InstalledRecord record = Assert.IsType<InstalledRecord>(InstalledRecord.Read(Path.Combine(root, "tiny-x4")));
        Assert.Equal(("2", "fp32"), (record.Version, record.Quantisation));
        Assert.Equal(catalog.Model("tiny-x4")!.Variants["fp32"].Sha256, record.Files["fp32.onnx"]);
        Assert.Equal([("tiny-x4", UpdateState.UpToDate)], ModelUpdates.States(catalog, root));
    }

    [Fact]
    public async Task ANewVersionOrAFixedFileIsAnUpdate()
    {
        await Download(Catalog("1", ("tiny-x4", "weights"u8.ToArray())));

        Assert.Equal(UpdateState.UpdateAvailable, ModelUpdates.State(Catalog("2", ("tiny-x4", "weights"u8.ToArray())), root, "tiny-x4"));
        Assert.Equal(UpdateState.UpdateAvailable, ModelUpdates.State(Catalog("1", ("tiny-x4", "fixed weights"u8.ToArray())), root, "tiny-x4"));
    }

    [Fact]
    public async Task AModelTheCatalogNoLongerListsIsWithdrawnAndKept()
    {
        await Download(Catalog("1", ("tiny-x4", "weights"u8.ToArray())));

        Assert.Equal(UpdateState.Withdrawn, ModelUpdates.State(Catalog("1", ("tiny-face", "face"u8.ToArray())), root, "tiny-x4"));
        Assert.True(File.Exists(Path.Combine(root, "tiny-x4", "fp32.onnx")));
    }

    [Fact]
    public async Task AModelDownloadedBeforeTheRecordIsComparedBySize()
    {
        ModelManifest catalog = Catalog("1", ("tiny-x4", "weights"u8.ToArray()));
        await Download(catalog);
        File.Delete(Path.Combine(root, "tiny-x4", InstalledRecord.FileName));

        Assert.Equal(UpdateState.UpToDate, ModelUpdates.State(catalog, root, "tiny-x4"));
        Assert.Equal(UpdateState.UpdateAvailable, ModelUpdates.State(Catalog("1", ("tiny-x4", "larger weights"u8.ToArray())), root, "tiny-x4"));
    }

    [Fact]
    public async Task ARunThatFetchesNothingLeavesTheRecordAlone()
    {
        ModelManifest catalog = Catalog("1", ("tiny-x4", "weights"u8.ToArray()));
        await Download(catalog);
        string path = Path.Combine(root, "tiny-x4", InstalledRecord.FileName);
        DateTime written = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, written.AddDays(-1));

        await Download(catalog);

        Assert.Equal(written.AddDays(-1), File.GetLastWriteTimeUtc(path));
    }

    private async Task Download(ModelManifest catalog)
    {
        using HttpClient http = new(new Served(served));
        await ModelDownloads.DownloadAsync(http, catalog, root, null, TestContext.Current.CancellationToken);
    }

    private ModelManifest Catalog(string version, params (string Name, byte[] Weights)[] models)
    {
        List<object> entries = [];
        foreach ((string name, byte[] weights) in models)
        {
            byte[] meta = System.Text.Encoding.UTF8.GetBytes($$"""{"name": "{{name}}"}""");
            string url = $"https://host/{name}-{Convert.ToHexStringLower(SHA256.HashData(weights))[..8]}.onnx";
            served[url] = weights;
            served[$"https://host/{name}-meta.json"] = meta;
            entries.Add(new
            {
                name,
                version,
                kind = "onnx",
                meta = Listed(meta, $"https://host/{name}-meta.json"),
                quantisations = new Dictionary<string, object> { ["fp32"] = new { files = new[] { new { role = "weights", file = Listed(weights, url) } } } },
                license = "test",
            });
        }

        return ModelManifest.Parse(JsonSerializer.Serialize(new { schema = 2, version = "c", sequence = 1, models = entries }));
    }

    private static object Listed(byte[] bytes, string url) =>
        new { url, bytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };

    private sealed class Served(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsoluteUri, out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
