using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Revelei.Inference.Tests;

/// <summary>
/// PB73: a file two models list is kept once by its SHA-256, downloaded once, and removed only
/// with the last model that uses it.
/// </summary>
public sealed class SharedFilesTests : IDisposable
{
    private static readonly byte[] Encoder = "qwen3 4b text encoder"u8.ToArray();

    private readonly string root = Path.Combine(Path.GetTempPath(), "pb-shared-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> served = new(StringComparer.Ordinal);
    private readonly List<string> asked = [];

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ATextEncoderTwoModelsShareIsFetchedOnceAndGoesWithTheLastOne()
    {
        ModelManifest catalog = Catalog();
        using HttpClient http = new(new Served(served, asked));

        await ModelDownloads.DownloadPartsAsync(http, catalog, root, "z-image", "q4", null, TestContext.Current.CancellationToken);
        DownloadResult second = await ModelDownloads.DownloadPartsAsync(http, catalog, root, "flux-klein", "q4", null, TestContext.Current.CancellationToken);

        Assert.Single(asked, url => url.EndsWith("/encoder", StringComparison.Ordinal));
        Assert.Equal(1, second.Present);
        string encoder = Path.Combine(root, ModelDownloads.SharedFolder, Convert.ToHexStringLower(SHA256.HashData(Encoder)));
        Assert.Equal(Encoder, await File.ReadAllBytesAsync(encoder, TestContext.Current.CancellationToken));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoder)), InstalledRecord.Read(Path.Combine(root, "z-image"))!.Files["text-encoder"]);

        ModelDownloads.Remove(root, "z-image");
        Assert.True(File.Exists(encoder));
        Assert.False(File.Exists(Shared("z-image transformer")));

        ModelDownloads.Remove(root, "flux-klein");
        Assert.Empty(Directory.GetFiles(Path.Combine(root, ModelDownloads.SharedFolder)));
        Assert.Empty(ModelUpdates.States(catalog, root));
    }

    private string Shared(string content) =>
        Path.Combine(root, ModelDownloads.SharedFolder, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));

    private ModelManifest Catalog()
    {
        object Model(string name) => new
        {
            name,
            version = "1",
            kind = "diffusion",
            meta = Listed($"{name}-meta", System.Text.Encoding.UTF8.GetBytes($$"""{"name": "{{name}}"}""")),
            quantisations = new Dictionary<string, object>
            {
                ["q4"] = new
                {
                    files = new[]
                    {
                        new { role = "transformer", file = Listed($"{name}-transformer", System.Text.Encoding.UTF8.GetBytes($"{name} transformer")) },
                        new { role = "text-encoder", file = Listed("encoder", Encoder) },
                    },
                },
            },
            license = "test",
        };

        return ModelManifest.Parse(JsonSerializer.Serialize(new { schema = 2, version = "c", sequence = 1, models = new[] { Model("z-image"), Model("flux-klein") } }));
    }

    private object Listed(string name, byte[] bytes)
    {
        served[$"https://host/{name}"] = bytes;
        return new { url = $"https://host/{name}", bytes = bytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }

    private sealed class Served(Dictionary<string, byte[]> files, List<string> asked) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            asked.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(files.TryGetValue(request.RequestUri.AbsoluteUri, out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
