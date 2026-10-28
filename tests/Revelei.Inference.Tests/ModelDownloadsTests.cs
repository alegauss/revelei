using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB13, PB58: models are fetched from the manifest through .part files, checked by SHA-256 before
/// they are renamed into place, a file already here or in the installer's folder is not fetched,
/// an interrupted file resumes by HTTP Range, and a network failure is retried before it is named.
/// </summary>
public sealed class ModelDownloadsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-downloads-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> served = new(StringComparer.Ordinal);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EveryFileIsFetchedVerifiedAndPutInItsModelFolder()
    {
        ModelManifest manifest = Manifest(("tiny-x4", "weights"u8.ToArray()));
        using HttpClient http = new(new Served(served));

        DownloadResult result = await ModelDownloads.DownloadAsync(http, manifest, Path.Combine(root, "models"), null, TestContext.Current.CancellationToken);

        Assert.Equal(new DownloadResult(2, 0), result);
        Assert.Equal("weights"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(root, "models", "tiny-x4", "fp32.onnx"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(root, "models", "tiny-x4", "meta.json")));
        Assert.Empty(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories));
        Assert.True(ModelDownloads.Complete(manifest, Path.Combine(root, "models")));
    }

    [Fact]
    public async Task AFileThatArrivesDifferentIsDeletedAndNamed()
    {
        ModelManifest manifest = Manifest(("tiny-x4", "weights"u8.ToArray()));
        served["https://host/tiny-x4-fp32.onnx"] = "tampered"u8.ToArray();
        using HttpClient http = new(new Served(served));

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ModelDownloads.DownloadAsync(http, manifest, Path.Combine(root, "models"), null, TestContext.Current.CancellationToken));

        Assert.Contains("tiny-x4/fp32.onnx", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AFileAlreadyHereOrInTheInstallersFolderIsNotFetchedAgain()
    {
        ModelManifest manifest = Manifest(("tiny-x4", "weights"u8.ToArray()), ("tiny-face", "face"u8.ToArray()));
        string bundled = Path.Combine(root, "bundled");
        Directory.CreateDirectory(Path.Combine(bundled, "tiny-face"));
        await File.WriteAllBytesAsync(Path.Combine(bundled, "tiny-face", "fp32.onnx"), "face"u8.ToArray(), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(bundled, "tiny-face", "meta.json"), served["https://host/tiny-face-meta.json"], TestContext.Current.CancellationToken);
        using HttpClient http = new(new Served(served));
        string models = Path.Combine(root, "models");

        Assert.Equal("weights"u8.Length + served["https://host/tiny-x4-meta.json"].Length, ModelDownloads.MissingBytes(manifest, models, [bundled]));
        DownloadResult first = await ModelDownloads.DownloadAsync(http, manifest, models, null, TestContext.Current.CancellationToken, [bundled]);
        DownloadResult again = await ModelDownloads.DownloadAsync(http, manifest, models, null, TestContext.Current.CancellationToken, [bundled]);

        Assert.Equal(new DownloadResult(2, 2), first);
        Assert.Equal(new DownloadResult(0, 4), again);
        Assert.False(Directory.Exists(Path.Combine(models, "tiny-face")));
    }

    [Fact]
    public async Task OnlyTheNamedModelsAreCountedAndFetched()
    {
        ModelManifest manifest = Manifest(("tiny-x4", "weights"u8.ToArray()), ("tiny-face", "face"u8.ToArray()));
        using HttpClient http = new(new Served(served));
        string models = Path.Combine(root, "models");
        string[] face = ["tiny-face"];

        Assert.Equal("face"u8.Length + served["https://host/tiny-face-meta.json"].Length, ModelDownloads.TotalBytes(manifest, face));
        DownloadResult result = await ModelDownloads.DownloadAsync(http, manifest, models, null, TestContext.Current.CancellationToken, models: face);

        Assert.Equal(new DownloadResult(2, 0), result);
        Assert.True(ModelDownloads.Complete(manifest, models, models: face));
        Assert.False(ModelDownloads.Complete(manifest, models));
        Assert.False(Directory.Exists(Path.Combine(models, "tiny-x4")));
    }

    [Fact]
    public async Task ACancelledDownloadKeepsItsPartAndTheNextOneResumesByRange()
    {
        byte[] weights = RandomNumberGenerator.GetBytes(3 << 20);
        ModelManifest manifest = Manifest(("tiny-x4", weights));
        Served server = new(served);
        using HttpClient http = new(server);
        string models = Path.Combine(root, "models");
        using CancellationTokenSource cancel = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ModelDownloads.DownloadAsync(http, manifest, models, new SynchronousProgress(cancel), cancel.Token));

        string part = Path.Combine(models, "tiny-x4", "fp32.onnx.part");
        long kept = new FileInfo(part).Length;
        Assert.InRange(kept, 1, weights.Length - 1);
        Assert.False(File.Exists(Path.Combine(models, "tiny-x4", "fp32.onnx")));
        Assert.Equal(weights.Length - kept + served["https://host/tiny-x4-meta.json"].Length, ModelDownloads.MissingBytes(manifest, models));

        DownloadResult resumed = await ModelDownloads.DownloadAsync(http, manifest, models, null, TestContext.Current.CancellationToken);

        Assert.Equal(new DownloadResult(2, 0), resumed);
        Assert.Contains(kept, server.RangesAsked);
        Assert.Equal(weights, await File.ReadAllBytesAsync(Path.Combine(models, "tiny-x4", "fp32.onnx"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AServerThatIgnoresTheRangeStartsTheFileOver()
    {
        byte[] weights = "weights"u8.ToArray();
        ModelManifest manifest = Manifest(("tiny-x4", weights));
        string models = Path.Combine(root, "models");
        Directory.CreateDirectory(Path.Combine(models, "tiny-x4"));
        await File.WriteAllBytesAsync(Path.Combine(models, "tiny-x4", "fp32.onnx.part"), "wei"u8.ToArray(), TestContext.Current.CancellationToken);
        using HttpClient http = new(new Served(served) { HonoursRange = false });

        await ModelDownloads.DownloadAsync(http, manifest, models, null, TestContext.Current.CancellationToken);

        Assert.Equal(weights, await File.ReadAllBytesAsync(Path.Combine(models, "tiny-x4", "fp32.onnx"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANetworkFailureIsRetriedAndReportedOnlyWhenItLasts()
    {
        ModelManifest manifest = Manifest(("tiny-x4", "weights"u8.ToArray()));
        string models = Path.Combine(root, "models");
        List<DownloadStage> stages = [];
        using HttpClient flaky = new(new Served(served) { FailuresLeft = ModelDownloads.Attempts - 1 });

        DownloadResult result = await ModelDownloads.DownloadAsync(flaky, manifest, models, new Recorded(stages), TestContext.Current.CancellationToken, retryDelay: TimeSpan.Zero);

        Assert.Equal(new DownloadResult(2, 0), result);
        Assert.Equal(ModelDownloads.Attempts - 1, stages.Count(s => s == DownloadStage.Retrying));
        Assert.Contains(DownloadStage.Verifying, stages);

        using HttpClient down = new(new Served(served) { FailuresLeft = int.MaxValue });
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            ModelDownloads.DownloadAsync(down, Manifest(("other", "x"u8.ToArray())), models, null, TestContext.Current.CancellationToken, retryDelay: TimeSpan.Zero));
    }

    [Fact]
    public async Task AFileTheServerDoesNotHaveIsReportedAtOnceRatherThanRetried()
    {
        // PB59: a 404 is the file not being there; retrying it would only make the person wait.
        ModelManifest manifest = Manifest(("tiny-x4", "weights"u8.ToArray()));
        served.Remove("https://host/tiny-x4-fp32.onnx");
        List<DownloadStage> stages = [];
        using HttpClient http = new(new Served(served));

        HttpRequestException missing = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ModelDownloads.DownloadAsync(http, manifest, Path.Combine(root, "models"), new Recorded(stages), TestContext.Current.CancellationToken, retryDelay: TimeSpan.FromMinutes(1)));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain(DownloadStage.Retrying, stages);
        Assert.StartsWith("The download server no longer has this file (error 404).", DownloadFailure.Explain(missing, 0).What, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainHttpIsTrustedOnlyOnLoopbackAndOnlyWhenAskedFor()
    {
        // PB59: --manifest may name the UI cases' fixture server; the pinned manifest stays https.
        const string Local = """{"schema": 1, "version": "f", "models": [{"name": "m", "meta": {"url": "http://127.0.0.1:47623/m.json", "bytes": 1, "sha256": "00"}, "variants": {}, "license": "l"}]}""";
        const string Remote = """{"schema": 1, "version": "f", "models": [{"name": "m", "meta": {"url": "http://example.com/m.json", "bytes": 1, "sha256": "00"}, "variants": {}, "license": "l"}]}""";

        Assert.Equal("f", ModelManifest.Parse(Local, loopback: true).Version);
        Assert.Throws<InvalidDataException>(() => ModelManifest.Parse(Local));
        Assert.Throws<InvalidDataException>(() => ModelManifest.Parse(Remote, loopback: true));
    }

    [Fact]
    public void TimeLeftFollowsTheSpeed()
    {
        Assert.Null(new DownloadProgress("f", 0, 100).Remaining);
        Assert.Equal(TimeSpan.FromSeconds(5), new DownloadProgress("f", 50, 100, DownloadStage.Downloading, 10).Remaining);
    }

    [Fact]
    public void AManifestOfAnotherSchemaOrWithAPlainHttpUrlIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => ModelManifest.Parse("""{"schema": 3, "version": "1", "models": []}"""));
        Assert.Throws<InvalidDataException>(() => ModelManifest.Parse(
            """{"schema": 1, "version": "1", "models": [{"name": "m", "meta": {"url": "http://x/m.json", "bytes": 1, "sha256": "00"}, "variants": {}, "license": "l"}]}"""));
    }

    [Fact]
    public void ThePinnedManifestIsTheOnePublished()
    {
        // scripts/publish-models.py writes published-models.json; the app pins what it says.
        using JsonDocument record = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Root, "scripts", "published-models.json")));
        Assert.Equal(ModelDownloads.ManifestUrl, record.RootElement.GetProperty("manifest_url").GetString());
        Assert.Contains("/resolve/", ModelDownloads.ManifestUrl, StringComparison.Ordinal);
        Assert.Equal(record.RootElement.GetProperty("version").GetString(), ModelDownloads.Embedded.Version);
    }

    [Fact]
    public void TheEmbeddedManifestListsWhatFirstUseFetches()
    {
        // PB58: face restore and background removal fetch these, so their sizes are known offline.
        foreach (string model in (string[])["gfpgan-v1.4", "rmbg-1.4", "vitmatte-small-composition-1k"])
        {
            ManifestModel listed = Assert.IsType<ManifestModel>(ModelDownloads.Embedded.Model(model));
            Assert.True(listed.Variants[ModelDownloads.Variant].Bytes > 1 << 20);
        }
    }

    private ModelManifest Manifest(params (string Name, byte[] Weights)[] models)
    {
        List<object> entries = [];
        foreach ((string name, byte[] weights) in models)
        {
            byte[] meta = System.Text.Encoding.UTF8.GetBytes($$"""{"name": "{{name}}"}""");
            served[$"https://host/{name}-fp32.onnx"] = weights;
            served[$"https://host/{name}-meta.json"] = meta;
            entries.Add(new
            {
                name,
                meta = Listed(meta, $"https://host/{name}-meta.json"),
                variants = new Dictionary<string, object> { ["fp32"] = Listed(weights, $"https://host/{name}-fp32.onnx") },
                license = "test",
            });
        }

        return ModelManifest.Parse(JsonSerializer.Serialize(new { schema = 1, version = "1", models = entries }));
    }

    private static object Listed(byte[] bytes, string url) =>
        new { url, bytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };

    private sealed class Served(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public bool HonoursRange { get; init; } = true;

        public int FailuresLeft { get; set; }

        public List<long> RangesAsked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new HttpRequestException("The network went away.");
            }

            if (!files.TryGetValue(request.RequestUri!.ToString(), out byte[]? bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Headers.Range?.Ranges.Single().From is long from && HonoursRange)
            {
                RangesAsked.Add(from);
                ByteArrayContent rest = new(bytes[(int)from..]);
                rest.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = rest });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    // Progress<T> posts to the thread pool; this reports inline, so the cancel lands mid-file.
    private sealed class SynchronousProgress(CancellationTokenSource cancel) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            if (value.Done > 0)
            {
                cancel.Cancel();
            }
        }
    }

    private sealed class Recorded(List<DownloadStage> stages) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => stages.Add(value.Stage);
    }
}
